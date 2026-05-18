using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using Puppeteer.Tell;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing
{
	internal class ActorHandler : IActorEventJournalClient
	{
		private readonly SymbolTable symbolTable;
		private readonly DomainLibraries libraries;
		internal Assembly[] LibraryAssemblies { get; }

		internal readonly ConcurrentParametersPool ParametersPool;
		private readonly ConcurrentParsersPool ParsersPool;

		internal const int MAX_NORMAL_LOAD_POOL_SIZE = 250;

		private Diary dairy = null;
		private readonly Actor actor;

		private string commandLineError;
		private DateTime timeStamp;
		private DateTime dateOfLastActivity;

		private readonly Reactions reactions;

		private static readonly object myLock = new object();

		internal readonly string Name;

		internal ActorHandler(Actor actor, String name)
			: this(actor, name, Array.Empty<Assembly>())
		{
		}

		internal ActorHandler(Actor actor, string name, params Assembly[] libraryAssemblies)
		{
			ArgumentNullException.ThrowIfNull(actor);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			ArgumentNullException.ThrowIfNull(libraryAssemblies);

			// Si el caller no aporta libraries, fallback al assembly del actor (back-compat).
			// El path idiomatico es pasar las DLLs de dominio explicitamente.
			LibraryAssemblies = libraryAssemblies.Length > 0
				? libraryAssemblies
				: new[] { actor.GetType().Assembly };

			libraries = DomainLibraries.GetOrLoad(LibraryAssemblies);

			symbolTable = new SymbolTable();
			symbolTable.ActorHandler = this;
			if (actor is ActorV1)
				symbolTable.SetVariable("ItIsThePresent", true, typeof(bool));
			this.Name = name;
			this.actor = actor;

			this.ParametersPool = new ConcurrentParametersPool(MAX_NORMAL_LOAD_POOL_SIZE);
			this.ParsersPool = new ConcurrentParsersPool(libraries, symbolTable, MAX_NORMAL_LOAD_POOL_SIZE);

			this.reactions = new Reactions(this);
		}

		internal SymbolTable SymbolTable { get { return symbolTable; } }

		internal DomainLibraries Libraries => libraries;

		// Transport for the cross-actor Tell primitive. Default null; the developer
		// configures it explicitly when the actor needs to participate in tells.
		// Plan 4 introduced the plumbing; Plan 5 wired outbound dispatch + journal;
		// Plan 6 (this one) auto-registers the ack handler on assignment so that
		// acks coming back from the receiver are journaled in this actor's log.
		// Single-assignment: setting a non-null Transport when one is already
		// configured throws — actors do not silently swap transports while live.
		private ITransport _transport;
		internal ITransport Transport
		{
			get => _transport;
			set
			{
				if (value != null && _transport != null && !ReferenceEquals(value, _transport))
				{
					throw new LanguageException("ActorHandler.Transport is already configured. Re-assigning a different transport while the actor is live would orphan in-flight tells. Build a new actor (or accept the same transport instance).");
				}
				if (value != null && !ReferenceEquals(value, _transport))
				{
					value.RegisterAckHandler(HandleAckEnvelope);
				}
				_transport = value;
			}
		}

		// Tell-only-in-Reaction-Action enforcement. Cross-actor `tell` is a
		// consequence of an intra-actor event observed by a Reaction, not a
		// command/query primitive. The flag is raised while a Reaction's
		// .Do(...) Action body executes; TellStatement.Execute throws
		// LanguageException when the flag is down. Set/cleared by Reaction
		// runtime via EnterReactionActionScope / ExitReactionActionScope; user
		// code never touches it.
		private bool inReactionAction;
		internal bool InReactionAction => inReactionAction;
		internal void EnterReactionActionScope() { inReactionAction = true; }
		internal void ExitReactionActionScope() { inReactionAction = false; }

		// Modo follower: solo el primary tiene autoridad de escribir al journal
		// canonico (invariante 1-escritor). Cuando este flag esta encendido, los
		// Tell terminators de las Reactions corren su match pero NO invocan
		// PerformCmd — eso evita que el follower agregue entradas `tell ...`
		// al journal compartido del actor. Set por Performance.Start(asFollower:true)
		// antes de arrancar las Cued reactions; el primary lo deja false (default)
		// y mantiene su comportamiento de siempre.
		//
		// Etapa 1 (firmada 2026-05-14): el follower's Tell terminator se vuelve
		// un no-op silencioso (no journaliza y no despacha envelope). Etapa 2
		// pendiente: refactor de TellStatement para extraer envelope-construction
		// y permitir dispatch-without-journaling.
		internal bool SuppressReactionJournaling { get; set; }

		// Sentinel reaction id used when the framework emits MarkEventsAsElided
		// for the tell+ack pair. The elision API requires reactionId > 0; real
		// Reaction ids grow from 1 incrementally, so we pick int.MaxValue as a
		// framework-reserved sentinel. A repo with billions of Reactions per
		// actor would have to revisit this; until then it is safely distinct
		// from any real reaction id. Plan 6 (A) of the Tell primitive roadmap.
		internal const int TELL_PAIR_ELISION_REACTION_ID = int.MaxValue;

		// Plan 6: ack ingestion. Invoked by the configured ITransport when an ack
		// is delivered from the receiver. Validates correlation against the tell
		// dedup tables, rejects orphans (no matching tell ever sent) and
		// duplicates (this envelope.Id was already acked), and journals the ack
		// sentence on the actor's own log under a fresh entry id. Acquires the
		// actor's write lock manually — there is no PerformCmd around this
		// callback, so the lock has to be taken explicitly.
		//
		// Plan 6 (A) extension: when the originating tell entry was a "single-tell
		// entry" (its script contained exactly one TellStatement), the framework
		// emits MarkEventsAsElided on the {tell, ack} pair so the journal stays
		// dense in steady state. Multi-statement entries are NOT elided —
		// MarkEventsAsElided is entry-coarse and would discard non-tell siblings.
		internal void HandleAckEnvelope(AckEnvelope envelope)
		{
			if (string.IsNullOrEmpty(envelope.Id))
			{
				Debug.WriteLine($"[Tell.Ack] rejected ack with empty envelope.Id from {envelope.TargetClass}({envelope.TargetId}) — transport contract requires the originating tell id to round-trip.");
				return;
			}

			if (!symbolTable.IsTellEnvelopeIdKnown(envelope.Id))
			{
				Debug.WriteLine($"[Tell.Ack] orphan ack '{envelope.Id}' from {envelope.TargetClass}({envelope.TargetId}) — no matching tell was ever sent from this actor. Likely transport bug or split-brain restart.");
				return;
			}

			rwLock.EnterWriteLock();
			try
			{
				if (symbolTable.IsTellEnvelopeIdAcked(envelope.Id))
				{
					Debug.WriteLine($"[Tell.Ack] duplicate ack '{envelope.Id}' from {envelope.TargetClass}({envelope.TargetId}) — already acked previously.");
					return;
				}

				// The validation set update happens even when there is no Diary
				// configured (e.g. tests that exercise dedup logic in isolation).
				// Without a Diary the ack survives only in-memory; with one it is
				// also persisted as a tell-ack sentence for replay reconstruction.
				symbolTable.MarkTellEnvelopeIdAcked(envelope.Id);

				if (dairy != null)
				{
					string ackSentence = RenderAckSentence(envelope);
					long ackEntryId = TakeAndIncrementEntryId();
					// The ack does not originate from a user-issued PerformCmd, so
					// the log line uses the default IP / anonymous user. The journal
					// sentence still reads as a regular tell ack — the actor's own
					// causation, not anyone else's command.
					DateTime now = DateTime.Now;
					dairy.WriteScriptEntry(ackEntryId, ackSentence, IpAddress.DEFAULT, UserInLog.ANONYMOUS, now, exposeData: null);

					// Plan 6 (A): elide the {tell, ack} pair when the tell entry
					// was single-tell. Multi-statement entries on either side are
					// left intact — eliding them would discard non-tell siblings.
					// The ack entry itself is always single-statement (we just
					// wrote one ack sentence), so the only gating condition is
					// whether the originating tell entry qualifies.
					if (symbolTable.TryLookupTellEntryId(envelope.Id, out long tellEntryId)
						&& symbolTable.IsSingleTellEntry(tellEntryId))
					{
						EventElisionStorage elisionStorage = dairy.Storage?.EventElisionStorage;
						if (elisionStorage != null)
						{
							elisionStorage.MarkEventsAsElided(
								new long[] { tellEntryId, ackEntryId },
								TELL_PAIR_ELISION_REACTION_ID,
								now);
						}
					}
				}
			}
			finally
			{
				rwLock.ExitWriteLock();
			}
		}

		// Plan 6 (A) replay-path hook: invoked from TellAckStatement.Execute when
		// the journal replays an ack whose live MarkEventsAsElided call may have
		// been interrupted before writing the elision marker. Re-emits the
		// elision so storage converges on the elided state regardless of when
		// the interruption happened. Idempotent.
		internal void TryEmitTellPairElision(long tellEntryId, long ackEntryId)
		{
			EventElisionStorage elisionStorage = dairy?.Storage?.EventElisionStorage;
			if (elisionStorage == null) return;
			elisionStorage.MarkEventsAsElided(
				new long[] { tellEntryId, ackEntryId },
				TELL_PAIR_ELISION_REACTION_ID,
				DateTime.Now);
		}

		// Plan 8 of the Tell primitive roadmap: emit MarkEventsAsElided over the
		// full saga trajectory when a `close` statement runs. Generalises Plan 6
		// (A)'s pair elision to the entire saga (start + steps + compensates +
		// close) — claim 12 of Paper 3 lifted from intra-actor MarkAsSkip to
		// cross-actor saga close. Idempotent — re-marking already-elided ids
		// is a no-op at the storage level.
		internal void EmitSagaTrajectoryElision(long[] trajectoryEntryIds)
		{
			ArgumentNullException.ThrowIfNull(trajectoryEntryIds);
			if (trajectoryEntryIds.Length == 0) return;
			EventElisionStorage elisionStorage = dairy?.Storage?.EventElisionStorage;
			if (elisionStorage == null) return;
			elisionStorage.MarkEventsAsElided(
				trajectoryEntryIds,
				TELL_PAIR_ELISION_REACTION_ID,
				DateTime.Now);
		}

		// Render the canonical ack sentence: `tell ack '<id>' from <Target>('<id>');`
		// — same shape TellAckStatement.Write produces, kept in lockstep so that the
		// journal and live-emitted entries are indistinguishable.
		private static string RenderAckSentence(AckEnvelope envelope)
		{
			StringBuilder sb = new StringBuilder(64);
			sb.Append("tell ack '");
			sb.Append(envelope.Id);
			sb.Append("' from ");
			sb.Append(envelope.TargetClass);
			sb.Append("('");
			sb.Append(envelope.TargetId);
			sb.Append("');");
			return sb.ToString();
		}

		internal DateTime DateOfLastActivity
		{
			get
			{
				if (dairy == null) return DateTime.MinValue;
				return dairy.DateOfLastActivity;
			}
		}

		public ActorFollower CreateFollower(int followerId)
		{
			ActorFollower result = ActorFollower.CreateFollowerConActor(this.actor, followerId);

			var culture = new CultureInfo("en-US");
			CultureInfo.DefaultThreadCurrentCulture = culture;
			CultureInfo.DefaultThreadCurrentUICulture = culture;

			return result;
		}

		public ActorFollower CreateFollowerSinActor(int followerId)
		{
			ActorFollower result = ActorFollower.CreateFollowerSinActor(this.actor, followerId);

			var culture = new CultureInfo("en-US");
			CultureInfo.DefaultThreadCurrentCulture = culture;
			CultureInfo.DefaultThreadCurrentUICulture = culture;

			return result;
		}

		internal string CommandLineError
		{
			get
			{
				return this.commandLineError;
			}
		}

		internal DateTime CurrentTimeStamp
		{
			get
			{
				return this.timeStamp;
			}
		}

		internal Reactions Reactions => reactions;

		// Paper 5 / Materialize v2 — Fase 0. Sub-namespace para administrar destinations
		// del actor. La instancia se materializa lazy en el primer acceso; el storage
		// concreto se obtiene del Diary via TryGetMaterializationCheckpointStorage()
		// y por ende requiere EventSourcingStorage configurado primero (consistente con
		// como Reactions necesita SetDairyStorage tras EventSourcingStorage).
		private Materialization materialization;
		internal Materialization Materialization => materialization ??= new Materialization(this);

		internal Puppeteer.EventSourcing.DB.MaterializationCheckpointStorage TryGetMaterializationCheckpointStorage()
		{
			return dairy?.Storage?.MaterializationCheckpointStorage;
		}

		// Fase 2 — Materialize v2 wire verb (a) EnviameDesde. Materialization.cs accede
		// al DiaryStorage para enumerar records raw del journal sin pasar por la API
		// publica de rehidratacion (que filtra elididos).
		internal Puppeteer.EventSourcing.DB.DiaryStorage TryGetDiaryStorage()
		{
			return dairy?.Storage;
		}

		// paper05-lab5: harness needs the Diary facade (not just the inner storage)
		// to drive WriteScriptEntry through the buffered vs direct paths.
		internal Puppeteer.EventSourcing.DB.Diary TryGetDiary()
		{
			return dairy;
		}

		internal ActorV1.LeaderInitializationHandler OnLeaderInitialization;

		internal ActorV1.AfterRecoveringHandler OnAfterRecovering;

		private const string PRODUCTION_DOES_NOT_NEED_IT = "PRODUCTION_DOES_NOT_NEED_IT";

		internal void EventSourcingStorage(DatabaseType dbType, string connection, string localBufferPath = null)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connection);

			dairy = new Diary(dbType, connection, eventJournalClient: this, localBufferPath: localBufferPath);

			Console.WriteLine($"Starting {this.GetType()}'s Actor");

			EventSourcingStorage(dairy);

			reactions.SetDairyStorage(dairy.Storage);

			if (this.OnAfterRecovering != null) this.OnAfterRecovering(dbType, connection, this.Name, this.EntryId);

		}

		internal void EventSourcingStorage(DatabaseType dbType, string connection, ActorFollower actorFollower, string localBufferPath = null)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connection);

			dairy = new Diary(dbType, connection, eventJournalClient: actorFollower, localBufferPath: localBufferPath);

			long lastProcessedEntryId = EventSourcingStorage(dairy);

			dairy.SaveLastProcessedEntryId(actorFollower.FollowerId, lastProcessedEntryId);
		}

		private Parser parserForRecovering;
		private BlockingCollection<EventData> eventsQueue;
		private long EventSourcingStorage(Diary dairy)
		{
			if (parserForRecovering == null) parserForRecovering = new Parser(libraries, symbolTable);
			if (eventsQueue == null) eventsQueue = new BlockingCollection<EventData>(MAX_NORMAL_LOAD_POOL_SIZE);

			BlockingCollection<ProgramTask> programTaskQueue = new BlockingCollection<ProgramTask>(MAX_NORMAL_LOAD_POOL_SIZE);
			BlockingCollection<Program> preparedQueue = new BlockingCollection<Program>(MAX_NORMAL_LOAD_POOL_SIZE);
			BlockingCollection<Program> parsedQueue = new BlockingCollection<Program>(MAX_NORMAL_LOAD_POOL_SIZE);
			BlockingCollection<Program> resolvedQueue = new BlockingCollection<Program>(MAX_NORMAL_LOAD_POOL_SIZE);

			ProgramTaskPool programTaskPool = new ProgramTaskPool(MAX_NORMAL_LOAD_POOL_SIZE);

			rwLock.EnterWriteLock();

			long lastEntryId = 0;

			try
			{
				symbolTable.RecoveringState = true;
				if (actor is ActorV1)
				{
					symbolTable.SetVariable("ItIsThePresent", false, typeof(bool));
					ExecutionContext.Current.SetContext(DateTime.Now, false, actor);
				}

				currentTransition = ActorTransitions.Recovering;

				// Pipeline de rehidratacion: RehydrateFromEvent -> eventsQueue -> preparer ->
				// programTaskQueue -> collector -> parsedQueue -> resolver -> preparedQueue -> exec.
				// Cada task envuelve su foreach en try/finally para garantizar CompleteAdding
				// sobre la queue de output (y la de entry) incluso si lanza excepcion.
				// Sin esto, una excepcion en cualquier etapa dejaria los workers downstream bloqueados
				// indefinidamente en GetConsumingEnumerable() y Task.WaitAll nunca retornaria.
				var preparerTask = Task.Run(async () =>
				{
					try
					{
						foreach (EventData retornableEventData in eventsQueue.GetConsumingEnumerable())
						{
							var task = Task.Run(() => GenerateAndRentProgram(retornableEventData));
							await task.ConfigureAwait(false);
							programTaskQueue.Add(programTaskPool.Rent(retornableEventData, task));
						}
					}
					finally
					{
						eventsQueue.CompleteAdding();
						programTaskQueue.CompleteAdding();
					}
				});

				var programaCollectorTask = Task.Run(async () =>
				{
					try
					{
						foreach (var programTask in programTaskQueue.GetConsumingEnumerable())
						{
							var rentedProgram = await programTask.ProgramaTaskInstance.ConfigureAwait(false);
							// Phase 5: GenerateAndRentProgram now returns null for
							// orphan Invocations (cache miss on the actionId). Skip
							// silently — the orphan path is otherwise unreachable
							// post-Fase-4 by construction.
							if (rentedProgram != null)
							{
								parsedQueue.Add(rentedProgram);
							}

							programTask.EventData.ReturnToEventDataPool();
							programTaskPool.Return(programTask);
						}
					}
					finally
					{
						programTaskQueue.CompleteAdding();
						parsedQueue.CompleteAdding();
					}
				});

				var resolverTask = Task.Run(() =>
				{
					try
					{
						foreach (Program rentedProgram in parsedQueue.GetConsumingEnumerable())
						{
							if (!actionCommands.ContainsAction(rentedProgram.Script))
								rentedProgram.SolveReferences(rentedProgram.Parameters, withStaticValidation: true);

							preparedQueue.Add(rentedProgram);
						}
					}
					finally
					{
						parsedQueue.CompleteAdding();
						preparedQueue.CompleteAdding();
					}
				});

				var executionTask = Task.Run(() =>
				{
					try
					{
						long avanceParcial = 0;
						int avanceDelCienPorCiento = 0;
						foreach (Program rentedProgram in preparedQueue.GetConsumingEnumerable())
						{
							Perform(rentedProgram, rentedProgram.Parameters);
							ReturnProgram(rentedProgram);

							// Paper 5 Lab 1: counts events applied during the bulk
							// replay of EventSourcingStorage (initial Start path).
							// ReplayPendingEventsForRedBlack covers the handover tail.
							LabInstrumentation.IncrementReplayEventsCounted();
							LabInstrumentation.OnReplayEventCounted?.Invoke(rentedProgram.EntryId);

							avanceParcial++;
							if (avanceParcial == _avanceEquivalenteUnoPorciento)
							{
								avanceDelCienPorCiento++;
								avanceParcial = 0;
								Console.Write($"{avanceDelCienPorCiento}%");
							}
						}
					}
					finally
					{
						preparedQueue.CompleteAdding();
					}
				});

				Exception producerException = null;
				try
				{
					lastEntryId = dairy.RehydrateFromEvent(this.EntryId);
				}
				catch (Exception ex)
				{
					producerException = ex;
				}
				finally
				{
					eventsQueue.CompleteAdding();
				}

				try
				{
					Task.WaitAll(preparerTask, programaCollectorTask, resolverTask, executionTask);
				}
				catch (AggregateException agg)
				{
					var inner = agg.Flatten().InnerExceptions;
					if (producerException != null)
					{
						var combined = new List<Exception> { producerException };
						combined.AddRange(inner);
						throw new AggregateException(combined);
					}
					if (inner.Count == 1) throw inner[0];
					throw;
				}

				if (producerException != null) throw producerException;

				symbolTable.RecoveringState = false;
				if (actor is ActorV1)
				{
					symbolTable.SetVariable("ItIsThePresent", true, typeof(bool));
					ExecutionContext.Current.SetContext(DateTime.Now, true, actor);
				}

				currentTransition = ActorTransitions.Recovered;
			}
			finally
			{

				rwLock.ExitWriteLock();
			}

			parserForRecovering = null;
			eventsQueue = null;

			this.EntryId = Int64.Max(lastEntryId, this.EntryId);

			return this.EntryId;
		}
		internal void SaveLastProcessedEntryId(int followerId, long entryId)
		{
			dairy.SaveLastProcessedEntryId(followerId, entryId);
		}

		internal Action<long, byte[]> OnRecordWritten
		{
			set { if (dairy != null) dairy.OnRecordWritten = value; }
		}

		internal void GracefulExit()
		{
			reactions.GracefulShutdown();
		}

		internal void AddRecordWrittenCallback(Action<long, byte[]> callback)
		{
			if (callback == null) throw new ArgumentNullException(nameof(callback));
			if (dairy == null) throw new LanguageException("Diary is not initialized. Call EventSourcingStorage first.");
			dairy.AddRecordWrittenCallback(callback);
		}

		// Phase 5 of the Action refactor: dropped OnNewActionDefined and
		// WriteRawActionDefinition. Both existed for the legacy
		// ActionDefinition-message replication that depended on the lateral
		// _ACTION table being populated. Post-cutover, replication propagates
		// Define + Invocation entries via OnRecordWritten → CueEvent per record
		// (firmado: cross-stage atomicity is unnecessary because the director's
		// journal already persisted the pair transactionally).

		internal void WriteRawRecord(byte[] record, long entryId)
		{
			if (dairy == null) throw new LanguageException("Diary is not initialized. Call EventSourcingStorage first.");
			dairy.WriteRawRecord(record, entryId);

			// Keep the actor's high-water mark in sync with the journal.
			// ApplyReplicatedEvent advances EntryId for Script and Action
			// records (line 659), but the Define branch in
			// StageHook.ApplyReplicatedEvent short-circuits after the
			// AddKnownActionFromDefine dispatch — it never reaches the
			// max-update inside ActorHandler.ApplyReplicatedEvent. Without
			// this bump the cast's CurrentEntryId would freeze at the Define
			// boundary and the very next Invocation would look like a gap
			// to Stage.ListenReplication ("expected N, got N+1"), even
			// though both records were replicated correctly. Bumping in the
			// canonical Raw-write path makes the invariant uniform: any
			// record landing on disk via WriteRawRecord advances the
			// in-memory EntryId to at least its entry id.
			this.EntryId = Int64.Max(entryId, this.EntryId);
		}

		internal void ApplyReplicatedEvent(EventData eventData)
		{
			if (eventData == null) throw new ArgumentNullException(nameof(eventData));

			rwLock.EnterWriteLock();
			try
			{
				symbolTable.RecoveringState = true;

				Parser parser = ParsersPool.Rent();
				Program program;

				switch (eventData)
				{
					case ScriptEventData scriptEvent:
						if (String.IsNullOrEmpty(scriptEvent.Script)) throw new LanguageException("Script cannot be null or empty");
						parser.SetSource(scriptEvent.Script);
						program = parser.Rehydrate();
						program.Parameters = ParametersPool.Rent();
						program.SolveParameters(program.Parameters);
						break;

					case ActionEventData actionEvent:
						// Phase 5 of the Action refactor: the legacy "Action with ID X
						// does not exist in the cache" throw is dropped. By construction
						// of Fase 4 (atomic Define + Invocation write, monotonic
						// entry-id order, replay processes Define entries via
						// AddKnownActionFromDefine), the cache is always populated by
						// the time we reach an Invocation row. A defensive early
						// return covers the otherwise-unreachable orphan path.
						if (!actionCommands.TryGetValue(actionEvent.ActionId, out CommandCacheEntry cacheEntry))
						{
							ParsersPool.Return(parser);
							return;
						}
						program = cacheEntry.Program;
						program.Parameters.LoadArguments(actionEvent.Arguments);
						break;

					default:
						throw new LanguageException($"Unsupported event data type: {eventData.GetType().Name}");
				}

				IpAddress ip = eventData.Ip == IpAddress.DEFAULT.Ip ? IpAddress.DEFAULT : new IpAddress(eventData.Ip);
				UserInLog user = eventData.User == UserInLog.ANONYMOUS.Id ? UserInLog.ANONYMOUS : UserInLog.GenerateUserBasedOn(eventData.User);

				program.Parameters.SystemParameter<DateTime>("Now", eventData.OccurredAt);
				program.Parameters.SystemParameter<IpAddress>("Ip", ip);
				program.Parameters.SystemParameter<UserInLog>("User", user);

				if (!actionCommands.ContainsAction(program.Script))
					program.SolveReferences(program.Parameters, withStaticValidation: true);

				Perform(program, program.Parameters);

				if (eventData is ScriptEventData)
					ParametersPool.Return(program.Parameters);

				ParsersPool.Return(parser);

				this.EntryId = Int64.Max(eventData.EntryId, this.EntryId);

				symbolTable.RecoveringState = false;
			}
			finally
			{
				rwLock.ExitWriteLock();
			}
		}

		internal bool TryGetAction(int actionId, out CommandCacheEntry entry)
		{
			if (actionId < 0)
			{
				Debug.WriteLine($"[ActorHandler.TryGetAction] Invalid actionId: {actionId}");
				entry = null;
				return false;
			}

			bool found = actionCommands.TryGetValue(actionId, out entry);

			if (!found)
			{
				Debug.WriteLine($"[ActorHandler.TryGetAction] ActionId {actionId} not found in cache");
			}

			return found;
		}

		private class ProgramTaskPool
		{
			private readonly ConcurrentStack<ProgramTask> _pool = new ConcurrentStack<ProgramTask>();
			private readonly int _maxPoolSize;
			private int _count = 0;

			internal ProgramTaskPool(int maxPoolSize = 200)
			{
				if (maxPoolSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxPoolSize), "maxPoolSize must be greater than 0.");
				_maxPoolSize = maxPoolSize;
			}

			internal ProgramTask Rent(EventData eventData, Task<Program> programTask)
			{
				if (_pool.TryPop(out var item))
				{
					Interlocked.Decrement(ref _count);
					item.Reset(eventData, programTask);
					return item;
				}
				if (Volatile.Read(ref _count) < _maxPoolSize)
				{
					var newItem = new ProgramTask(eventData, programTask);
					Interlocked.Increment(ref _count);
					return newItem;
				}
				return new ProgramTask(eventData, programTask);
			}

			internal void Return(ProgramTask item)
			{
				ArgumentNullException.ThrowIfNull(item);
				item.Reset(null, null);
				if (Volatile.Read(ref _count) < _maxPoolSize)
				{
					_pool.Push(item);
					Interlocked.Increment(ref _count);
				}
			}
		}

		// Modification of ProgramTask to support reinitialization.
		private class ProgramTask
		{
			internal EventData EventData { get; private set; }
			internal Task<Program> ProgramaTaskInstance { get; private set; }

			internal ProgramTask(EventData eventData, Task<Program> programTask)
			{
				EventData = eventData;
				ProgramaTaskInstance = programTask;
			}

			internal void Reset(EventData eventData, Task<Program> programTask)
			{
				EventData = eventData;
				ProgramaTaskInstance = programTask;
			}
		}

		internal void ReturnProgram(Program rentedProgram)
		{
			if (!actionCommands.ContainsAction(rentedProgram.Script))
			{
				ParametersPool.Return(rentedProgram.Parameters);
			}
		}

		internal Program GenerateAndRentProgram(EventData eventData)
		{
			Program program;
			switch (eventData)
			{
				case ScriptEventData executable:

					if (String.IsNullOrEmpty(executable.Script)) throw new LanguageException("Script cannot be null or empty");

					parserForRecovering.SetSource(executable.Script);

					program = parserForRecovering.Rehydrate();

					program.Parameters = ParametersPool.Rent();

					program.SolveParameters(program.Parameters);

					break;

				case ActionEventData executable:

					if (executable.ActionId < 0) throw new LanguageException("ActionId cannot be negative");
					if (String.IsNullOrEmpty(executable.Arguments)) throw new LanguageException("Arguments cannot be null or empty");

					// Phase 5 of the Action refactor: the legacy "Action with ID X
					// does not exist in the cache" throw is dropped. The cache is
					// always populated by the time an Invocation is processed (atomic
					// Define + Invocation write, monotonic ordering, replay populates
					// via AddKnownActionFromDefine). A defensive early return covers
					// the otherwise-unreachable orphan path — caller must tolerate
					// a null Program (subsequent code is short-circuited).
					if (!actionCommands.TryGetValue(executable.ActionId, out CommandCacheEntry cacheDeComando))
					{
						return null;
					}

					program = cacheDeComando.Program;

					program.Parameters.LoadArguments(executable.Arguments);

					break;
				default:
					throw new LanguageException($"Unsupported event data type: {eventData.GetType().Name}");
			}

			IpAddress ip = eventData.Ip == IpAddress.DEFAULT.Ip ? IpAddress.DEFAULT : new IpAddress(eventData.Ip);
			UserInLog user = eventData.User == UserInLog.ANONYMOUS.Id ? UserInLog.ANONYMOUS : UserInLog.GenerateUserBasedOn(eventData.User);

			program.Parameters.SystemParameter<DateTime>("Now", eventData.OccurredAt);
			program.Parameters.SystemParameter<IpAddress>("Ip", ip);
			program.Parameters.SystemParameter<UserInLog>("User", user);

			program.EntryId = eventData.EntryId;

			return program;
		}


		string IActorEventJournalClient.ActorName => this.Name;

		long IActorEventJournalClient.GetLastProcessedEntryId(int followerId) => dairy.GetLastProcessedEntryId(followerId);

		private long _avanceEquivalenteUnoPorciento = Int64.MaxValue;
		void IActorEventJournalClient.BeginJournalReplay(long totalEventsToApply)
		{
			if (totalEventsToApply < 0) throw new LanguageException($"Total events to apply '{totalEventsToApply}' cannot be negative.");

			_avanceEquivalenteUnoPorciento = (long)(totalEventsToApply / 100.0 + 1.0);
		}

		bool IActorEventJournalClient.CanContinueReplay(long currentEntryId)
		{
			return true;
		}

		void IActorEventJournalClient.ReplayEvent(EventData retornableEventData)
		{
			eventsQueue.Add(retornableEventData);
		}

		void IActorEventJournalClient.EndJournalReplay(bool forcedToEnd)
		{
			if (this.OnLeaderInitialization != null) this.OnLeaderInitialization();
		}

		internal async Task EventSourcingStorageAsync(DatabaseType dbType, string connection, string needsUniqueIdentifierForPaymentHub)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connection);


			bool qaAndStageNeedsToGenerateAnUniqueReferenceBecauseTheyUseSamePaymentHub = needsUniqueIdentifierForPaymentHub != PRODUCTION_DOES_NOT_NEED_IT;
			if (!qaAndStageNeedsToGenerateAnUniqueReferenceBecauseTheyUseSamePaymentHub)
				Debug.WriteLine($"Recovering state in Production mode...");
			else
				Debug.WriteLine("Recovering state in NOT Production mode...");

			rwLock.EnterWriteLock();
			try
			{
				symbolTable.RecoveringState = true;
				if (actor is ActorV1)
				{
					symbolTable.SetVariable("ItIsThePresent", false, typeof(bool));
					ExecutionContext.Current.SetContext(DateTime.Now, false, actor);
				}

				dairy = new Diary(dbType, connection, this);
				await dairy.RehydrateFromEventAsync();
			}
			finally
			{

				rwLock.ExitWriteLock();
			}
			symbolTable.RecoveringState = false;
			if (actor is ActorV1)
			{
				symbolTable.SetVariable("ItIsThePresent", true, typeof(bool));
				ExecutionContext.Current.SetContext(DateTime.Now, true, actor);
			}

			if (this.OnAfterRecovering != null) this.OnAfterRecovering(dbType, connection, this.Name, this.EntryId);
		}


		private readonly ReaderWriterLockSlim rwLock = new ReaderWriterLockSlim();
		private readonly SemaphoreSlim _block = new SemaphoreSlim(1, 1);
		private static string scriptEnEjecucion = "";

		internal string ScriptEnEjecucion
		{
			get
			{
				return scriptEnEjecucion;
			}
		}

		internal bool ItsANewOne { get; private set; }

		bool IActorEventJournalClient.IsNew
		{
			set
			{
				this.ItsANewOne = value;
			}
		}


		internal long EntryId { get; private set; }

		internal long CurrentEntryId => EntryId;

		private long TakeAndIncrementEntryId()
		{
			return ++EntryId;
		}

		private int _currentActionId = 0;
		private int TakeAndIncrementActionId()
		{
			return ++_currentActionId;
		}


		internal string PerformCmd(string script, IpAddress ip, UserInLog user)
		{
			Parameters parameters = ParametersPool.Rent();

			parameters.SystemParameter<IpAddress>("Ip", ip);
			parameters.SystemParameter<UserInLog>("User", user);

			var result = PerformCmd(script, parameters);

			ParametersPool.Return(parameters);

			return result;
		}

		private enum JournalEntry
		{
			Unknown,
			IsExistingAction,
			IsNewAction,
			IsScript
		}

		internal static readonly Parameters EMPTY_PARAMETERS = EmptyParameters();

		private static Parameters EmptyParameters()
		{
			Parameters parameters = new Parameters();
			parameters.SystemParameter<IpAddress>("Ip", IpAddress.DEFAULT);
			parameters.SystemParameter<UserInLog>("User", UserInLog.ANONYMOUS);

			return parameters;
		}



		bool IActorEventJournalClient.IsActionKnown(int actionId)
		{
			if (actionId < 0) throw new ArgumentNullException(nameof(actionId));
			return actionCommands.ContainsAction(actionId);
		}

		void IActorEventJournalClient.AddKnownAction(int actionId, string actionScript, string parameters)
		{
			if (actionId < 0) throw new ArgumentNullException(nameof(actionId));
			ArgumentNullException.ThrowIfNullOrWhiteSpace(actionScript);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(parameters);

			this._currentActionId = Int32.Max(actionId, this._currentActionId);


			Parser parser = ParsersPool.Rent();

			parser.SetSource(actionScript);
			Program program = parser.Parse(isQuery: false, isCheck: false);

			ParsersPool.Return(parser);

			program.SetContextInfo();
			program.AdjustCompilationMode(useInterpretedMode: false, CompilationModePolicy.Automatic);
			program.Parameters = new Parameters(parameters);
			_ = actionCommands.Agregar(actionId, actionScript, program);
		}

		// Phase 4 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// populates the action cache from a Define entry encountered during replay.
		// The Define entry carries the canonical DSL sentence
		//   `define action <id> (params) as <body> end;`
		// which Phase 1's parser reads back as a Program containing one
		// DefineActionStatement. We extract the body and build a Program for just
		// the body (not the wrapper), the same shape the live cache stores.
		//
		// Cache key: the canonical body text (= program.ConvertToString of the body
		// statements). Q1 = (a) firmado at start of Phase 4: post-replay, a write
		// path that re-encounters the same logical body re-resolves to this cached
		// entry (by canonicalising its parsed program before lookup).
		void IActorEventJournalClient.AddKnownActionFromDefine(int actionId, string defineStatementText)
		{
			if (actionId < 0) throw new ArgumentNullException(nameof(actionId));
			ArgumentNullException.ThrowIfNullOrWhiteSpace(defineStatementText);

			this._currentActionId = Int32.Max(actionId, this._currentActionId);

			Parser definePass = ParsersPool.Rent();
			definePass.SetSource(defineStatementText);
			Program defineProgram = definePass.Parse(isQuery: false, isCheck: false);
			ParsersPool.Return(definePass);

			DefineActionStatement defineStmt = defineProgram.Collect<DefineActionStatement>().FirstOrDefault();
			if (defineStmt == null)
			{
				throw new LanguageException($"AddKnownActionFromDefine: parsed Define statement text did not yield a DefineActionStatement (actionId={actionId}). Text: '{defineStatementText}'");
			}

			// Canonical body text = each body statement rendered with Statement.Write
			// at tabs=0 in IN_MEMORY mode. Same shape that ActorHandler's cutover
			// emits on the live path (FormatedScriptForDairy = program.ConvertToString).
			System.Text.StringBuilder bodySb = new System.Text.StringBuilder();
			foreach (Statement source in defineStmt.Body)
			{
				source.Write(bodySb, 0, DatabaseType.IN_MEMORY);
			}
			string canonicalBody = bodySb.ToString();

			// Re-parse the canonical body as a standalone Program — that is what the
			// cache stores. Side-stepping a Program "shrink" operation keeps the AST
			// path uniform with the live cache miss path.
			Parser bodyPass = ParsersPool.Rent();
			bodyPass.SetSource(canonicalBody);
			Program bodyProgram = bodyPass.Parse(isQuery: false, isCheck: false);
			ParsersPool.Return(bodyPass);

			bodyProgram.SetContextInfo();
			bodyProgram.AdjustCompilationMode(useInterpretedMode: false, CompilationModePolicy.Automatic);

			string legacyParametersText = Parameters.CanonicalDeclarationsToLegacyFormat(defineStmt.ParametersText);
			if (!string.IsNullOrEmpty(legacyParametersText))
			{
				bodyProgram.Parameters = new Parameters(legacyParametersText);
			}

			_ = actionCommands.Agregar(actionId, canonicalBody, bodyProgram);
		}


		private class CommandPrepared
		{
			internal Program Program;
			internal CommandCacheEntry CacheEntry;
			internal JournalEntry Entry;
			internal string FormatedScriptForDairy;
			internal bool NeedsToSolveParameters;
			internal bool NeedsToSolveReferences;

			internal void Reset()
			{
				Program = null;
				CacheEntry = null;
				Entry = JournalEntry.Unknown;
				FormatedScriptForDairy = null;
				NeedsToSolveParameters = false;
				NeedsToSolveReferences = false;
			}
		}

		// Instancia reutilizable para PerformCmd sync: es seguro porque PerformCmd se ejecuta bajo write lock (un solo thread a la vez).
		// No aplica para PerformCmdAsync que tiene sus propias variables locales.
		private readonly CommandPrepared _reusableCommandPrepared = new CommandPrepared();

		// Modelo de compilacion y cache para Commands (PerformCmd):
		//
		// Un script funciona como F(x1,x2,...,xn). La primera vez se parsea, se resuelven todas las referencias
		// (SolveReferences: LValues, RValues, variables globales, parameters) y se cachea el Program compilado.
		// En invocaciones subsiguientes, el lambda F compilado se reutiliza; solo se rebindean los parameters
		// (SolveParameters) con la nueva instance de values.
		//
		// Cache: actionCommands (por script string).
		// - Sin parameters de user: modo interpretado, NO se cachea, se persiste como Script en el journal.
		// - Con parameters de user: modo compilado, SE cachea con ActionId, se persiste como Action en el journal.
		//
		// PerformCmd es secuencial (write lock), asi que el mismo Program cacheado puede ser reutilizado
		// sin riesgo de concurrencia. Esto difiere de PerformQry/PerformChk/PerformEmit que usan read lock
		// y pueden ejecutarse en paralelo (ver documentacion en esos methods).
		private void PrepareCommandProgram(string script, Parameters parameters, CommandPrepared commandPrepared)
		{
			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");
			if (parameters == null) throw new ArgumentNullException(nameof(parameters));
			if (commandPrepared == null) throw new ArgumentNullException(nameof(commandPrepared));

			commandPrepared.Reset();

			if (!actionCommands.TryGetValue(script, out commandPrepared.CacheEntry))
			{
				// CACHE MISS: primera vez que se ve este script.
				Parser parser = ParsersPool.Rent();

				parser.SetSource(script);
				commandPrepared.Program = parser.Parse(isQuery: false, isCheck: false);

				ParsersPool.Return(parser);

				commandPrepared.Program.SetContextInfo();

				if (parameters == EMPTY_PARAMETERS || !parameters.HasUserParameter())
				{
					// Sin parameters de user: modo interpretado, no se cachea.
					// Se serializa como Script directo en el journal.
					commandPrepared.Entry = JournalEntry.IsScript;
					commandPrepared.Program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
					commandPrepared.FormatedScriptForDairy = commandPrepared.Program.ConvertToString(this.DatabaseType);
				}
				else
				{
					// Con parameters de user: modo compilado, se cachea con ActionId.
					// Se serializa como Action (ActionId + arguments) en el journal.
					commandPrepared.Entry = JournalEntry.IsNewAction;
					var nextActionId = this.TakeAndIncrementActionId();
					commandPrepared.Program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
					commandPrepared.CacheEntry = actionCommands.Agregar(nextActionId, script, commandPrepared.Program);
					commandPrepared.FormatedScriptForDairy = commandPrepared.Program.ConvertToString(this.DatabaseType);
				}
				// En cache miss siempre se necesita SolveReferences para resolver la estructura completa
				// del program (LValues, RValues, variables globales, parameters).
				commandPrepared.NeedsToSolveReferences = !commandPrepared.Program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
			else
			{
				// CACHE HIT: el Program ya fue parseado, compilado y sus referencias resueltas.
				// Solo se necesita rebindear los parameters con los nuevos values (SolveParameters).
				commandPrepared.Entry = JournalEntry.IsExistingAction;
				commandPrepared.Program = (Program)commandPrepared.CacheEntry.Program;
				commandPrepared.NeedsToSolveParameters = !commandPrepared.Program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
		}

		// Ejecuta el program ya preparado bajo write lock.
		// Flujo: CargarArgumentos -> SolveReferences/SolveParameters -> Perform -> persistir al journal.
		// writeNewEntry es false durante rehidratacion (RecoveringState) para no re-persistir eventos.
		private string ExecuteCommandWithWriteLock(CommandPrepared commandPrepared, Parameters parameters, DateTime now, IpAddress Ip, UserInLog User)
		{
			if (commandPrepared == null) throw new ArgumentNullException(nameof(commandPrepared));
			if (parameters == null) throw new ArgumentNullException(nameof(parameters));

			string result = null;
			bool executionError = false;
			// SuppressReactionJournaling (modo follower, Etapa 2): permite que el
			// script ejecute en write-lock (TellStatement.Execute construye el
			// envelope y lo enqueua) pero NO escribe al journal canonico, asi se
			// preserva el invariante 1-escritor. El drain de PendingTells despues
			// del lock release despacha el envelope via Transport igual que en
			// el primary. El gate solo aplica DENTRO de un .Do(...) Action de
			// Reaction (InReactionAction == true) — PerformCmd directos del usuario
			// (en produccion no llegan al follower porque el gate esta cerrado,
			// pero los tests pueden invocarlos) journalizan normal. Cross-ref:
			// project_follower_materialize_roles.md.
			bool writeNewEntry = dairy != null && !symbolTable.RecoveringState
				&& !(SuppressReactionJournaling && InReactionAction);
			long nextEntryId = -1;

			commandPrepared.Program.CargarArgumentos(parameters);

			if (commandPrepared.NeedsToSolveReferences) commandPrepared.Program.SolveReferences(parameters, withStaticValidation: true);
			if (commandPrepared.NeedsToSolveParameters) commandPrepared.Program.SolveParameters(parameters);

			try
			{
				executionError = true;
				// Phase 4 of the Action refactor: a first invocation (IsNewAction)
				// emits TWO journal rows atomically — the Define declaration and
				// the first Invocation. Take BOTH entry ids upfront so the Define
				// precedes the Invocation in the monotonic journal order. The
				// Program.EntryId attaches to the Invocation row (the actual
				// effect of running the body); TellStatement and friends see this
				// same id during execution and on replay (LoadProgram sets it
				// from the Invocation row).
				long defineEntryIdForCutover = -1;
				if (writeNewEntry)
				{
					if (commandPrepared.Entry == JournalEntry.IsNewAction)
					{
						defineEntryIdForCutover = this.TakeAndIncrementEntryId();
					}
					nextEntryId = this.TakeAndIncrementEntryId();
				}

				// Plan 6 (A) of the Tell primitive roadmap: propagate the entry id
				// to the Program so TellStatement.Execute can stash the
				// (envelope.Id -> entryId) mapping for later ack-side elision.
				// Rehydration sets Program.EntryId in LoadProgram; the live path
				// is where we set it here.
				commandPrepared.Program.EntryId = nextEntryId;

				result = Perform(commandPrepared.Program, parameters);

				executionError = false;
				if (writeNewEntry)
				{
					string argumentValues;
					switch (commandPrepared.Entry)
					{
						case JournalEntry.IsScript:
							if (!String.IsNullOrWhiteSpace(commandPrepared.FormatedScriptForDairy)) dairy.WriteScriptEntry(nextEntryId, commandPrepared.FormatedScriptForDairy, Ip, User, now, commandPrepared.Program.LastExposeData);
							break;

						case JournalEntry.IsExistingAction:
							argumentValues = parameters.ArgumentsAsString(this.DatabaseType);
							var actionId = commandPrepared.CacheEntry.Id;
							dairy.WriteInvocationEntry(actionId, nextEntryId, Ip, User, now, argumentValues, commandPrepared.Program.LastExposeData);
							break;

						case JournalEntry.IsNewAction:
							if (actionCommands == null) throw new LanguageException("cacheDeComandos is null");

							var nextActionId = commandPrepared.CacheEntry.Id;
							argumentValues = parameters.ArgumentsAsString(this.DatabaseType);
							// Phase 4 cutover: emit Define + first Invocation as TWO
							// journal rows atomically. defineText is the canonical
							// `define action <id> (<params>) as <body> end;`
							// sentence Phase 1's parser reads back during replay.
							string defineText = DefineActionStatement.ComposeJournalText(
								nextActionId,
								parameters.UserParametersAsCanonicalText(),
								commandPrepared.FormatedScriptForDairy);
							dairy.WriteDefineWithFirstInvocation(
								nextActionId,
								defineText,
								defineEntryIdForCutover,
								nextEntryId,
								Ip, User, now,
								argumentValues,
								commandPrepared.Program.LastExposeData);
							break;
						default:
							throw new LanguageException($"The dairy entry is not valid: {commandPrepared.Entry}");
					}
				}
			}
			catch
			{
				if (executionError && writeNewEntry)
				{
					commandPrepared.FormatedScriptForDairy = commandPrepared.Program.ConvertToString(dairy.DatabaseType);
					if (!String.IsNullOrWhiteSpace(commandPrepared.FormatedScriptForDairy))
					{
						commandPrepared.FormatedScriptForDairy = Diary.EXECUTION_ERROR_TAG + '\r' + commandPrepared.FormatedScriptForDairy;
						dairy.WriteScriptEntry(nextEntryId, commandPrepared.FormatedScriptForDairy, commandPrepared.Program.Ip, commandPrepared.Program.User, now, null);
					}
				}
				if (executionError) throw;
				commandLineError = commandPrepared.Program.GetCommandErrorLine();
			}

			return result;
		}

		internal string PerformCmd(string script, Parameters parameters)
		{
			return PerformCmd(script, parameters, DateTime.Now);
		}

		// PerformCmd (sync): ejecuta un source contra el actor y persiste al journal.
		// Concurrencia: WRITE LOCK — un solo thread a la vez. Usa _reusableCommandPrepared (instance compartida).
		// Cache: actionCommands — los Programas con parameters se compilan y cachean con ActionId.
		// Journal: escribe el evento (Script o Action) al diary si no esta en rehidratacion.
		internal string PerformCmd(string script, Parameters parameters, DateTime now)
		{
			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");
			if (parameters == null) throw new LanguageException("Can not send null parameters");

			string result = null;

			try
			{
				commandLineError = "";
				scriptEnEjecucion = script;

				IpAddress Ip = parameters.ParameterHasValue("Ip") ? (IpAddress)parameters["Ip"].GetValue() : IpAddress.DEFAULT;
				UserInLog User = parameters.ParameterHasValue("User") ? (UserInLog)parameters["User"].GetValue() : UserInLog.ANONYMOUS;

				parameters.SystemParameter<DateTime>("Now", now);

				rwLock.EnterWriteLock();

				try
				{
					PrepareCommandProgram(script, parameters, _reusableCommandPrepared);
					result = ExecuteCommandWithWriteLock(_reusableCommandPrepared, parameters, now, Ip, User);
				}
				finally
				{
					rwLock.ExitWriteLock();
				}

				// Plan 5 of the Tell primitive roadmap: drain envelopes that
				// TellStatement.Execute enqueued during program.Execute. Sending happens
				// outside the actor's write lock (released above) and after the journal
				// entry has been committed by ExecuteCommandWithWriteLock. The journal
				// sentence is durable before delivery is attempted, and transport
				// failures do not roll back the journal — coherent with the signed
				// principle "delivery is the transport's problem; correlation is the
				// journal's". GetAwaiter().GetResult() blocks because PerformCmd is
				// sync; tests use InMemoryTransport which completes synchronously.
				if (symbolTable.PendingTellCount > 0)
				{
					ITransport transportSnapshot = Transport;
					while (symbolTable.TryDequeuePendingTell(out TellEnvelope envelope))
					{
						if (transportSnapshot != null)
						{
							transportSnapshot.SendAsync(envelope).GetAwaiter().GetResult();
						}
					}
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine($"PerformCmd {DateTime.Now} errorType:{e.GetType()} errorDescri:{e.Message} script:{script}");
				throw;
			}

			timeStamp = parameters["Now"].GetValue<DateTime>();

			return result;
		}

		internal async Task<string> PerformCmdAsync(string script, IpAddress ip, UserInLog user)
		{
			Parameters parameters = ParametersPool.Rent();

			parameters.SystemParameter<IpAddress>("Ip", ip);
			parameters.SystemParameter<UserInLog>("User", user);

			var result = await PerformCmdAsync(script, parameters);

			ParametersPool.Return(parameters);

			return result;
		}

		// PerformCmdAsync: version async de PerformCmd.
		// Concurrencia: usa _block (SemaphoreSlim) en lugar de rwLock.EnterWriteLock, pero garantiza
		// exclusion mutua igualmente. Usa variables locales (no _reusableCommandPrepared) porque
		// la preparacion y ejecucion pueden estar en continuaciones async distintas.
		// Cache y journal: misma logica que PrepareCommandProgram/ExecuteCommandWithWriteLock pero inline.
		// La persistencia al journal (dairy.Write*Async) se hace FUERA del write lock para no bloquear
		// otros readers durante I/O.
		internal async Task<string> PerformCmdAsync(string script, Parameters parameters)
		{
			ArgumentNullException.ThrowIfNull(script);
			ArgumentNullException.ThrowIfNull(parameters);

			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");

			string result = null;
			JournalEntry entry = JournalEntry.Unknown;
			string formatedScriptForDairy = null;
			Program program;
			IpAddress Ip;
			UserInLog User;

			CommandCacheEntry cacheDeComandosEntry = null;

			bool needsToSolveParameters = false;
			bool needsToSolveReferences = false;
			bool executionError = false;
			// Mismo gate que el path sync (ExecuteCommandWithWriteLock): solo
			// suprime journaling cuando estamos DENTRO de un .Do(...) Action
			// de Reaction (InReactionAction == true). Otros PerformCmd directos
			// del usuario journalizan normal aun con el flag encendido.
			bool writeNewEntry = dairy != null && !symbolTable.RecoveringState
				&& !(SuppressReactionJournaling && InReactionAction);
			long nextEntryId = -1;
			Exception executionException = null;

			await _block.WaitAsync();

			try
			{
				if (!actionCommands.TryGetValue(script, out cacheDeComandosEntry))
				{
					// CACHE MISS: parsear, compilar y (opcionalmente) cachear.
					// Misma logica que PrepareCommandProgram pero con variables locales.
					Parser parser = ParsersPool.Rent();

					parser.SetSource(script);
					program = parser.Parse(isQuery: false, isCheck: false);

					ParsersPool.Return(parser);

					program.SetContextInfo();

					if (parameters == EMPTY_PARAMETERS || !parameters.HasUserParameter())
					{
						entry = JournalEntry.IsScript;
						program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
					}
					else
					{
						entry = JournalEntry.IsNewAction;
						var nextActionId = this.TakeAndIncrementActionId();
						program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
						cacheDeComandosEntry = actionCommands.Agregar(nextActionId, script, program);
					}
					formatedScriptForDairy = program.ConvertToString(this.DatabaseType);
					// Cache miss: resolver estructura completa (LValues, RValues, globales, parameters)
					needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}
				else
				{
					// CACHE HIT: Program ya compilado, referencias ya resueltas.
					// Solo rebindear parameters (SolveParameters) si es modo interpretado.
					entry = JournalEntry.IsExistingAction;
					program = cacheDeComandosEntry.Program;
					needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
					needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}

				commandLineError = "";

				Ip = parameters.ParameterHasValue("Ip") ? (IpAddress)parameters["Ip"].GetValue() : IpAddress.DEFAULT;
				User = parameters.ParameterHasValue("User") ? (UserInLog)parameters["User"].GetValue() : UserInLog.ANONYMOUS;

				DateTime now = DateTime.Now;
				parameters.SystemParameter<DateTime>("Now", now);

				rwLock.EnterWriteLock();

				program.CargarArgumentos(parameters);

				if (needsToSolveReferences) program.SolveReferences(parameters, withStaticValidation: true);
				if (needsToSolveParameters) program.SolveParameters(parameters);

				// Phase 4 of the Action refactor: take BOTH entry ids upfront
				// when this is a first invocation (IsNewAction) so the Define row
				// precedes the Invocation row in the monotonic journal. See
				// matching block in ExecuteCommandWithWriteLock for rationale.
				long defineEntryIdForCutover = -1;

				try
				{
					scriptEnEjecucion = script;
					if (writeNewEntry)
					{
						if (entry == JournalEntry.IsNewAction)
						{
							defineEntryIdForCutover = this.TakeAndIncrementEntryId();
						}
						nextEntryId = this.TakeAndIncrementEntryId();
					}

					// Plan 6 (A): propagate entry id for ack-side elision (see
					// matching comment in ExecuteCommandWithWriteLock).
					program.EntryId = nextEntryId;

					result = Perform(program, parameters);
				}
				catch (Exception e)
				{
					executionException = e;
					executionError = true;
					commandLineError = program.GetCommandErrorLine();
				}
				finally
				{
					rwLock.ExitWriteLock();
				}

				if (writeNewEntry && !String.IsNullOrWhiteSpace(formatedScriptForDairy))
				{
					if (executionError)
					{
						formatedScriptForDairy = Diary.EXECUTION_ERROR_TAG + '\r' + formatedScriptForDairy;
					}
					string argumentValues;
					switch (entry)
					{
						case JournalEntry.IsScript:
							if (!String.IsNullOrWhiteSpace(formatedScriptForDairy)) await dairy.WriteScriptEntryAsync(nextEntryId, formatedScriptForDairy, Ip, User, now, program.LastExposeData);
							break;

						case JournalEntry.IsExistingAction:
							argumentValues = parameters.ArgumentsAsString(this.DatabaseType);
							var actionId = cacheDeComandosEntry.Id;
							await dairy.WriteInvocationEntryAsync(actionId, nextEntryId, Ip, User, now, argumentValues, program.LastExposeData);
							break;

						case JournalEntry.IsNewAction:
							if (actionCommands == null) throw new LanguageException("cacheDeComandos is null");
							actionId = cacheDeComandosEntry.Id;
							argumentValues = parameters.ArgumentsAsString(this.DatabaseType);
							// Phase 4 cutover: emit Define + first Invocation as TWO
							// journal rows atomically (see ExecuteCommandWithWriteLock
							// for the canonical-sentence composition rationale).
							string defineText = DefineActionStatement.ComposeJournalText(
								actionId,
								parameters.UserParametersAsCanonicalText(),
								formatedScriptForDairy);
							await dairy.WriteDefineWithFirstInvocationAsync(
								actionId,
								defineText,
								defineEntryIdForCutover,
								nextEntryId,
								Ip, User, now,
								argumentValues,
								program.LastExposeData);
							break;
						default:
							throw new LanguageException($"The dairy entry is not valid: {entry}");
					}
				}

				// Plan 5 of the Tell primitive roadmap: drain envelopes that
				// TellStatement.Execute enqueued during program.Execute. Sending happens
				// here — outside the actor's write lock and after the journal entry has
				// been committed — so transport latency does not block subsequent
				// commands and the journal sentence is durable before delivery is
				// attempted. Transport failures do not roll back the journal: the
				// sentence "tell salió" is factual; delivery is the transport's problem.
				if (!executionError && symbolTable.PendingTellCount > 0)
				{
					ITransport transportSnapshot = Transport;
					while (symbolTable.TryDequeuePendingTell(out TellEnvelope envelope))
					{
						if (transportSnapshot != null)
						{
							await transportSnapshot.SendAsync(envelope);
						}
					}
				}

				if (executionError) throw executionException;
			}
			catch (Exception e)
			{
				Debug.WriteLine($"PerformCmd {DateTime.Now} errorType:{e.GetType()} errorDescri:{e.Message} script:{script}");
				throw;
			}
			finally
			{
				_block.Release();
			}

			timeStamp = parameters["Now"].GetValue<DateTime>();

			return result;
		}


		// PerformQry: ejecuta un query read-only contra el actor. No persiste al journal.
		// Concurrencia: READ LOCK — multiples queries pueden ejecutarse en paralelo entre si
		// y en paralelo con otros read locks (PerformChk, PerformEmit).
		// SetReadOnlyMode(true) protege la SymbolTable contra escrituras accidentales.
		//
		// Cache: QuerysEnCache (ConcurrentDictionary) — compartido con PerformChk y PerformEmit.
		// - Con parameters de user: modo compilado, SE cachea.
		// - Sin parameters de user: modo interpretado, NO se cachea.
		//
		// Nota sobre needsToSolveReferences en cache hit:
		// En produccion (CompiledModePolicy=Automatic) un Program cacheado siempre tiene
		// IsCompiledMode=true, por lo que needsToSolveReferences evalua a false.
		// El SolveReferences original (cache miss) ya resolvio la estructura completa del program;
		// en cache hit solo se rebindean parameters via SolveParameters.
		// needsToSolveReferences es true en cache hit SOLO con AlwaysInterpreted (unit tests).
		internal string PerformQry(string script, Parameters parameters)
		{
			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");
			if (parameters == null) throw new LanguageException("Can not send null parameters");

			bool needsToSolveParameters = false;
			bool needsToSolveReferences = false;
			string result = null;

			if (!QuerysEnCache.TryGetValue(script, out Program program))
			{
				// CACHE MISS: parsear con isQuery:true (bloquea expose y declaracion de variables globales).
				Parser parser = ParsersPool.Rent();

				parser.SetSource(script);
				program = parser.Parse(isQuery: true, isCheck: false);

				ParsersPool.Return(parser);

				program.SetContextInfo();

				if (parameters == EMPTY_PARAMETERS || parameters.HasUserParameter())
				{
					program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
					QuerysEnCache.TryAdd(script, program);
				}
				else
				{
					program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
				}
				// Cache miss: resolver estructura completa (LValues, RValues, globales, parameters)
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
			else
			{
				// CACHE HIT: Program ya compilado, referencias ya resueltas.
				// En Automatic: IsCompiledMode=true -> ambos false -> no se hace nada (el lambda compilado se reutiliza directamente).
				// En AlwaysInterpreted (tests): ambos true -> se re-resuelven referencias y parameters.
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}

			parameters.SystemParameter<DateTime>("Now", DateTime.Now);
			if (!parameters.ParameterHasValue("Ip")) parameters.SystemParameter<IpAddress>("Ip", IpAddress.DEFAULT);
			if (!parameters.ParameterHasValue("User")) parameters.SystemParameter<UserInLog>("User", UserInLog.ANONYMOUS);

			program.CargarArgumentos(parameters);
			if (needsToSolveReferences) program.SolveReferences(parameters, withStaticValidation: true);
			if (needsToSolveParameters) program.SolveParameters(parameters);

			commandLineError = "";

			rwLock.EnterReadLock();

			symbolTable.SetReadOnlyMode(true);

			try
			{
				result = Perform(program, parameters);
			}
			catch (Exception e)
			{
				Debug.WriteLine($"PerformCmd {DateTime.Now} errorType:{e.GetType()} errorDescri:{e.Message} script:{script}");
				commandLineError = program.GetCommandErrorLine();
				throw;
			}
			finally
			{
				symbolTable.SetReadOnlyMode(false);
				rwLock.ExitReadLock();
			}

			timeStamp = parameters["Now"].GetValue<DateTime>();

			return result;
		}

		// PerformEmit: accion de un Cue Reaction. Ejecuta un script read-only contra el actor
		// para producir side effects externos (ej: enviar datos por Kafka).
		// NO persiste al journal — el checkpoint del Reaction rastrea la ejecucion.
		// Concurrencia: READ LOCK — misma semantica que PerformQry.
		// SetReadOnlyMode(true) protege la SymbolTable contra escrituras accidentales.
		// Parser: isQuery:true — bloquea expose (que persistiria al journal) y declaracion de variables globales.
		// Cache: QuerysEnCache — compilado y cacheado con las mismas reglas que PerformQry.
		// Retorna void (no string) porque el resultado es un side effect externo, no un value de retorno.
		internal void PerformEmit(string script, Parameters parameters)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(script);
			ArgumentNullException.ThrowIfNull(parameters);
			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");

			bool needsToSolveParameters = false;
			bool needsToSolveReferences = false;

			if (!QuerysEnCache.TryGetValue(script, out Program program))
			{
				// CACHE MISS: parsear con isQuery:true (bloquea expose y declaracion de variables globales).
				Parser parser = ParsersPool.Rent();

				parser.SetSource(script);
				program = parser.Parse(isQuery: true, isCheck: false);

				ParsersPool.Return(parser);

				program.SetContextInfo();

				if (parameters == EMPTY_PARAMETERS || parameters.HasUserParameter())
				{
					program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
					QuerysEnCache.TryAdd(script, program);
				}
				else
				{
					program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
				}
				// Cache miss: resolver estructura completa (LValues, RValues, globales, parameters)
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
			else
			{
				// CACHE HIT: ver documentacion en PerformQry sobre needsToSolveReferences en cache hit.
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}

			parameters.SystemParameter<DateTime>("Now", DateTime.Now);
			if (!parameters.ParameterHasValue("Ip")) parameters.SystemParameter<IpAddress>("Ip", IpAddress.DEFAULT);
			if (!parameters.ParameterHasValue("User")) parameters.SystemParameter<UserInLog>("User", UserInLog.ANONYMOUS);

			program.CargarArgumentos(parameters);
			if (needsToSolveReferences) program.SolveReferences(parameters, withStaticValidation: true);
			if (needsToSolveParameters) program.SolveParameters(parameters);

			commandLineError = "";

			rwLock.EnterReadLock();

			symbolTable.SetReadOnlyMode(true);

			try
			{
				Perform(program, parameters);
			}
			catch (Exception e)
			{
				Debug.WriteLine($"PerformEmit {DateTime.Now} errorType:{e.GetType()} errorDescri:{e.Message} script:{script}");
				commandLineError = program.GetCommandErrorLine();
				throw;
			}
			finally
			{
				symbolTable.SetReadOnlyMode(false);
				rwLock.ExitReadLock();
			}

			timeStamp = parameters["Now"].GetValue<DateTime>();
		}

		// PerformChk: ejecuta un check read-only contra el actor. No persiste al journal.
		// Retorna null/empty si el check pasa, o un mensaje de error si falla.
		// Concurrencia: READ LOCK — misma semantica que PerformQry.
		// Parser: isCheck:true — produce un Program que ejecuta via EjecutarCheck() en lugar de Perform().
		// Cache: QuerysEnCache — mismo cache que PerformQry y PerformEmit.
		//
		// Diferencia con PerformQry en cache hit:
		// PerformChk solo asigna needsToSolveParameters (no needsToSolveReferences).
		// En produccion (Automatic) esto es equivalente: ambos evaluan a false en cache hit
		// porque IsCompiledMode=true. La diferencia solo es observable con AlwaysInterpreted (tests),
		// donde PerformQry re-resuelve referencias en cada invocacion y PerformChk no.
		// Esto no es un bug: PerformChk se invoca tipicamente una vez por PerformCheckThenCommand,
		// y la estructura del Program ya esta resuelta desde el cache miss original.
		internal string PerformChk(string script, Parameters parameters)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(script);
			ArgumentNullException.ThrowIfNull(parameters);
			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");

			bool needsToSolveParameters = false;
			bool needsToSolveReferences = false;
			string result = null;

			if (!QuerysEnCache.TryGetValue(script, out Program program))
			{
				// CACHE MISS: parsear con isCheck:true (produce Program para EjecutarCheck).
				Parser parser = ParsersPool.Rent();
				parser.SetSource(script);
				program = parser.Parse(isQuery: false, isCheck: true);
				ParsersPool.Return(parser);

				program.SetContextInfo();

				if (parameters == EMPTY_PARAMETERS || parameters.HasUserParameter())
				{
					program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
					QuerysEnCache.TryAdd(script, program);
				}
				else
				{
					program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
				}
				// Cache miss: resolver estructura completa (LValues, RValues, globales, parameters)
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
			else
			{
				// CACHE HIT: solo rebindear parameters. Ver comentario del method sobre la diferencia con PerformQry.
				needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}

			parameters.SystemParameter<DateTime>("Now", DateTime.Now);
			if (!parameters.ParameterHasValue("Ip")) parameters.SystemParameter<IpAddress>("Ip", IpAddress.DEFAULT);
			if (!parameters.ParameterHasValue("User")) parameters.SystemParameter<UserInLog>("User", UserInLog.ANONYMOUS);

			program.CargarArgumentos(parameters);
			if (needsToSolveReferences) program.SolveReferences(parameters, withStaticValidation: true);
			if (needsToSolveParameters) program.SolveParameters(parameters);

			rwLock.EnterReadLock();

			symbolTable.SetReadOnlyMode(true);

			try
			{
				result = program.EjecutarCheck();

				dateOfLastActivity = program.Now;

			}
			catch (Exception e)
			{
				commandLineError = program.GetCommandErrorLine();
				Debug.WriteLine($"PerformChk {program.Now} errorType:{e.GetType()} errorDescri:{e.Message} script:{script}");
				throw;
			}
			finally
			{
				symbolTable.SetReadOnlyMode(false);
				rwLock.ExitReadLock();
			}

			timeStamp = parameters["Now"].GetValue<DateTime>();

			return result;
		}

		// PerformCheckThenCmd: ejecuta un check sin bloqueo (via PerformChk con read lock),
		// y si pasa, toma write lock y re-ejecuta el check + source atomicamente.
		//
		// Flujo de dos fases:
		// 1. PerformChk(scriptForChk) — read lock, sin bloqueo. Si falla, retorna inmediatamente.
		// 2. Bajo write lock: re-ejecuta el check (EjecutarCheck) para verificar que sigue valido
		//    (otro writer pudo haber cambiado el estado entre fase 1 y fase 2).
		//    Si el check pasa, ejecuta el source (ExecuteCommandWithWriteLock) que persiste al journal.
		//
		// Concurrencia: WRITE LOCK para la fase 2. Usa _reusableCommandPrepared.
		// Cache: scriptForCmd en actionCommands (via PrepareCommandProgram), scriptForChk en QuerysEnCache.
		//
		// Nota sobre cache hit del check (scriptForChk):
		// Solo asigna needsToSolveParametersChk (no needsToSolveReferencesChk).
		// Misma logica que PerformChk — ver documentacion en ese method.
		internal string PerformCheckThenCmd(string scriptForChk, string scriptForCmd, Parameters parameters)
		{
			return PerformCheckThenCmd(scriptForChk, scriptForCmd, parameters, DateTime.Now);
		}

		internal string PerformCheckThenCmd(string scriptForChk, string scriptForCmd, Parameters parameters, DateTime now)
		{
			if (String.IsNullOrEmpty(scriptForChk)) throw new ArgumentNullException(nameof(scriptForChk));
			if (String.IsNullOrEmpty(scriptForCmd)) throw new ArgumentNullException(nameof(scriptForCmd));
			if (parameters == null) throw new ArgumentNullException(nameof(parameters));

			if (scriptForCmd.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");
			if (scriptForChk.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Check script exceeds the maximun length");

			// Fase 1: check sin bloqueo (read lock). Si falla, no se toma write lock.
			var chkResult = PerformChk(scriptForChk, parameters);
			if (!String.IsNullOrEmpty(chkResult))
			{
				return chkResult;
			}

			// Fase 2: re-check + command bajo write lock.
			string result = null;
			Program programChk;
			bool needsToSolveParametersChk = false;
			bool needsToSolveReferencesChk = false;

			try
			{
				commandLineError = "";
				scriptEnEjecucion = scriptForCmd;

				IpAddress Ip = parameters.ParameterHasValue("Ip") ? (IpAddress)parameters["Ip"].GetValue() : IpAddress.DEFAULT;
				UserInLog User = parameters.ParameterHasValue("User") ? (UserInLog)parameters["User"].GetValue() : UserInLog.ANONYMOUS;

				parameters.SystemParameter<DateTime>("Now", now);

				PrepareCommandProgram(scriptForCmd, parameters, _reusableCommandPrepared);

				if (!QuerysEnCache.TryGetValue(scriptForChk, out programChk))
				{
					// CACHE MISS del check script: parsear con isCheck:true.
					Parser parserChk = ParsersPool.Rent();
					parserChk.SetSource(scriptForChk);
					programChk = parserChk.Parse(isQuery: false, isCheck: true);
					ParsersPool.Return(parserChk);

					programChk.SetContextInfo();

					if (parameters == EMPTY_PARAMETERS || parameters.HasUserParameter())
					{
						programChk.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
						QuerysEnCache.TryAdd(scriptForChk, programChk);
					}
					else
					{
						programChk.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
					}
					// Cache miss: resolver estructura completa
					needsToSolveReferencesChk = !programChk.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}
				else
				{
					// CACHE HIT: solo rebindear parameters. Ver documentacion en PerformChk.
					needsToSolveParametersChk = !programChk.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}

				rwLock.EnterWriteLock();

				try
				{
					programChk.CargarArgumentos(parameters);
					if (needsToSolveReferencesChk) programChk.SolveReferences(parameters, withStaticValidation: true);
					if (needsToSolveParametersChk) programChk.SolveParameters(parameters);

					symbolTable.SetReadOnlyMode(true);

					try
					{
						// Re-check bajo write lock: verificar que el estado no cambio desde fase 1
						chkResult = programChk.EjecutarCheck();
						if (!String.IsNullOrEmpty(chkResult))
						{
							return chkResult;
						}
					}
					finally
					{
						symbolTable.SetReadOnlyMode(false);
					}

					result = ExecuteCommandWithWriteLock(_reusableCommandPrepared, parameters, now, Ip, User);
				}
				finally
				{
					rwLock.ExitWriteLock();
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine($"PerformCheckThenCmd {DateTime.Now} errorType:{e.GetType()} errorDescri:{e.Message} scriptForChk:{scriptForChk} scriptForCmd:{scriptForCmd}");
				throw;
			}

			timeStamp = parameters["Now"].GetValue<DateTime>();

			return result;
		}

		internal string Perform(Program program, Parameters parameters)
		{
			ArgumentNullException.ThrowIfNull(program);

			string resultado;

			switch (this.actor.CompiledModePolicy)
			{
				case CompilationModePolicy.Automatic:
					if (program.IsCompiledMode)
					{
						resultado = program.ExecuteExpression(parameters);
					}
					else
					{
						resultado = program.Execute();
					}
					break;
				case CompilationModePolicy.AlwaysCompiled:
					resultado = program.ExecuteExpression(parameters);
					break;
				case CompilationModePolicy.AlwaysInterpreted:
					resultado = program.Execute();
					break;
				default:
					throw new LanguageException("Unknown compilation mode");
			}

			dateOfLastActivity = DateTime.Now;

			return resultado;
		}

		internal string ComandForDairy(String script, IpAddress ip, UserInLog user)
		{
			Parser parser = new Parser(libraries, symbolTable);
			parser.SetSource(script);
			Program program = parser.Parse(isQuery: false, isCheck: false);
			program.SolveReferences(program.Parameters, withStaticValidation: false);
			String forDairy = program.ConvertToString(this.DatabaseType);
			return forDairy;

		}

		internal string ComandForDairy(String script, Parameters parameters)
		{
			Parser parser = new Parser(libraries, symbolTable);
			parser.SetSource(script);
			Program program = parser.Parse(isQuery: false, isCheck: false);
			program.CargarArgumentos(parameters);
			program.SolveReferences(parameters, withStaticValidation: true);
			string forDairy = program.ConvertToString(this.DatabaseType);
			return forDairy;
		}

		internal void ChangePrimaryKey()
		{
			if (dairy == null) throw new Exception("Repository its no configured yet.");

			dairy.ChangePrimaryKey();
		}

		private enum ActorTransitions { Recovering, Recovered, Lock, Alive }

		private volatile bool RecoveringStatusIsRunning = false;
		private volatile bool isCatchingUp = false;

		private volatile ActorTransitions currentTransition;

		internal bool IsAlive => currentTransition == ActorTransitions.Alive
								|| currentTransition == ActorTransitions.Recovered;

		//TOME EL CONTROL Y EJECUTAR LOS ULTIMOS COMANDOS SI HAY
		internal string LockWhileNotSyncronized()
		{
			if (RecoveringStatusIsRunning) return $"The follower it's already in {currentTransition} status";
			if (currentTransition == ActorTransitions.Recovering) return $"Invalid transition from {currentTransition} to {ActorTransitions.Recovering}";
			if (currentTransition == ActorTransitions.Lock) return $"Invalid transition from {currentTransition} to {ActorTransitions.Recovering}";

			bool alreadyBlocked = false;

			RecoveringStatusIsRunning = true;
			_ = Task.Run(() =>
			{
				rwLock.EnterWriteLock();

				try
				{
					alreadyBlocked = true;
					long lastIdAfterRecoveredState = 0;
					long previousLastIdAfterRecoveredState = 0;

					bool salir = false;
					int reintentos = 0;
					//while (itsFollowerRunning) && lastIdAfterRecoveredState == al lastIdAfterRecoveredState anterior
					while (!salir)
					{
						previousLastIdAfterRecoveredState = lastIdAfterRecoveredState;
						lastIdAfterRecoveredState = ReplayPendingEventsForRedBlack();

						Debug.WriteLine("New Actor Version is trying to reach last Entry Id: " + lastIdAfterRecoveredState);
						Thread.Sleep(TimeSpan.FromSeconds(0.5));

						if (lastIdAfterRecoveredState != previousLastIdAfterRecoveredState)
							reintentos = 0;
						else
							reintentos++;

						bool seAlcanzaron = reintentos >= 3;

						salir = RecoveringStatusIsRunning == false && seAlcanzaron;
					}
					Debug.WriteLine("New Actor Version reached last Entry Id: " + lastIdAfterRecoveredState);

					currentTransition = ActorTransitions.Alive;
				}
				finally
				{
					rwLock.ExitWriteLock();
				}
			});

			while (!alreadyBlocked) ;

			return "Recovering status is running";
		}

		private long ReplayPendingEventsForRedBlack()
		{
			eventsQueue = new BlockingCollection<EventData>(MAX_NORMAL_LOAD_POOL_SIZE);

			long lastEntryId = dairy.RehydrateFromEvent(this.EntryId);
			eventsQueue.CompleteAdding();

			Parser parser = ParsersPool.Rent();
			try
			{
				foreach (EventData eventData in eventsQueue.GetConsumingEnumerable())
				{
					Program program;
					switch (eventData)
					{
						case ScriptEventData scriptEvent:
							if (String.IsNullOrEmpty(scriptEvent.Script)) throw new LanguageException("Script cannot be null or empty");
							parser.SetSource(scriptEvent.Script);
							program = parser.Rehydrate();
							program.Parameters = ParametersPool.Rent();
							program.SolveParameters(program.Parameters);
							break;

						case ActionEventData actionEvent:
							if (actionEvent.ActionId < 0) throw new LanguageException("ActionId cannot be negative");
							// Phase 5: legacy "does not exist" throw dropped — see
							// matching comment in the other replay dispatch paths.
							if (!actionCommands.TryGetValue(actionEvent.ActionId, out CommandCacheEntry cacheEntry))
							{
								continue;
							}
							program = cacheEntry.Program;
							program.Parameters.LoadArguments(actionEvent.Arguments);
							break;

						default:
							throw new LanguageException($"Unsupported event data type: {eventData.GetType().Name}");
					}

					IpAddress ip = eventData.Ip == IpAddress.DEFAULT.Ip ? IpAddress.DEFAULT : new IpAddress(eventData.Ip);
					UserInLog user = eventData.User == UserInLog.ANONYMOUS.Id ? UserInLog.ANONYMOUS : UserInLog.GenerateUserBasedOn(eventData.User);

					program.Parameters.SystemParameter<DateTime>("Now", eventData.OccurredAt);
					program.Parameters.SystemParameter<IpAddress>("Ip", ip);
					program.Parameters.SystemParameter<UserInLog>("User", user);

					if (!actionCommands.ContainsAction(program.Script))
						program.SolveReferences(program.Parameters, withStaticValidation: true);

					try
					{
						Perform(program, program.Parameters);
					}
					catch
					{
						Console.WriteLine("Error during red-black replay at EntryId: " + eventData.EntryId);
					}

					if (eventData is ScriptEventData)
						ParametersPool.Return(program.Parameters);

					this.EntryId = Int64.Max(eventData.EntryId, this.EntryId);

					LabInstrumentation.IncrementReplayEventsCounted();
					LabInstrumentation.OnReplayEventCounted?.Invoke(eventData.EntryId);
				}
			}
			finally
			{
				ParsersPool.Return(parser);
				eventsQueue = null;
			}

			this.EntryId = Int64.Max(lastEntryId, this.EntryId);
			return this.EntryId;
		}

		internal void UnlockAndRunAlive()
		{
			if (!RecoveringStatusIsRunning) throw new Exception("The follower it's already stopped.");
			if (currentTransition == ActorTransitions.Recovering) throw new Exception($"Invalid transition from {currentTransition} to {ActorTransitions.Recovering}");
			if (currentTransition == ActorTransitions.Alive) throw new Exception($"Invalid transition from {currentTransition} to {ActorTransitions.Alive}");

			RecoveringStatusIsRunning = false;
		}

		internal void CatchUpFromJournal(long targetEntryId)
		{
			if (targetEntryId < 0) throw new ArgumentException("targetEntryId must be non-negative", nameof(targetEntryId));
			if (isCatchingUp) throw new InvalidOperationException("CatchUp already in progress for this actor");
			if (currentTransition != ActorTransitions.Recovered && currentTransition != ActorTransitions.Alive)
				throw new InvalidOperationException($"CatchUp requires Recovered or Alive state, current: {currentTransition}");
			if (this.EntryId >= targetEntryId) return;

			long fromEntryId = this.EntryId;
			Stopwatch stopwatch = Stopwatch.StartNew();

			isCatchingUp = true;
			try
			{
				rwLock.EnterWriteLock();
				try
				{
					while (this.EntryId < targetEntryId)
					{
						ReplayPendingEventsForRedBlack();
						if (this.EntryId < targetEntryId)
							Thread.Sleep(TimeSpan.FromSeconds(0.5));
					}
				}
				finally
				{
					rwLock.ExitWriteLock();
				}
			}
			finally
			{
				isCatchingUp = false;
				stopwatch.Stop();
				LabInstrumentation.OnMaterializeCatchUp?.Invoke(fromEntryId, this.EntryId, stopwatch.ElapsedTicks);
			}
		}

		internal string PerformTrim(DateTime trimmed)
		{
			try
			{
				rwLock.EnterWriteLock();
				try
				{
					dairy.Trim(trimmed);
				}
				finally
				{
					rwLock.ExitWriteLock();
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine($"PerformTrim {DateTime.Now} errorType:{e.GetType()} errorDescri:{e.Message}");
				throw;
			}

			string result = $"Trimmed = {trimmed};";

			return result;
		}

		// Etapa 4: Single-flight fail-fast. Politica "1 y solo 1 Distill a la vez";
		// el segundo concurrente recibe LanguageException, no se encola ni coalesce.
		//
		// Razon: el coalescing tenia sentido solo si reactions podian disparar Distill
		// automaticamente (la planeada metadata.Distill, descartada en Etapa 4 — un
		// developer podria ponerla en un patron frecuente sin entender el costo). Sin
		// auto-trigger, la unica fuente de Distill es operacional/humana (cron, admin,
		// comando manual). En ese contexto, fail-fast es honesto: dos operadores
		// invocando simultaneo entienden inmediatamente que hay uno en curso, no que
		// "tarda mucho silenciosamente".
		private readonly SemaphoreSlim distillRunSem = new SemaphoreSlim(1, 1);

		// Counter expuesto para tests: incrementa por cada ejecucion real de
		// dairy.Distill. Util para verificar que llamadas que tiran LanguageException
		// no incrementan.
		internal long DistillRunCount;

		// Test seam: hook que corre dentro del runner, despues de tomar rwLock pero
		// antes de llamar dairy.Distill. Tests lo usan para frenar al runner y probar
		// el comportamiento concurrente. Produccion jamas lo setea.
		internal Action TestHookBeforeRunDistill;

		internal void Distill()
		{
			if (dairy == null) throw new LanguageException("Diary is not initialized. Call EventSourcingStorage first.");

			if (!distillRunSem.Wait(0))
			{
				throw new LanguageException("Distill already in progress. Only one Distill at a time is allowed.");
			}

			try
			{
				rwLock.EnterWriteLock();
				try
				{
					Interlocked.Increment(ref DistillRunCount);
					TestHookBeforeRunDistill?.Invoke();
					dairy.Distill();
				}
				finally
				{
					rwLock.ExitWriteLock();
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine($"Distill {DateTime.Now} errorType:{e.GetType()} errorDescri:{e.Message}");
				throw;
			}
			finally
			{
				distillRunSem.Release();
			}
		}

		internal MemoryStream PerformArchive(DateTime startDate, DateTime endDate)
		{
			MemoryStream compressedInserts;
			try
			{
				try
				{
					compressedInserts = dairy.Archive(startDate, endDate);
				}
				finally
				{

				}
			}
			catch (Exception e)
			{
				Debug.WriteLine($"PerformArchived {DateTime.Now} errorType:{e.GetType()} errorDescription:{e.Message}");
				throw;
			}

			return compressedInserts;
		}

		internal static IEnumerable<string> PerformListActorsToLoad(string dbType, string connectionString, double minimumContributionPercent)
		{
			if (minimumContributionPercent < 0 && minimumContributionPercent > 100) throw new ArgumentException(nameof(minimumContributionPercent));

			IEnumerable<string> result;
			try
			{
				result = Diary.ListActorsToLoad(dbType, connectionString, minimumContributionPercent);
			}
			catch (Exception e)
			{
				Debug.WriteLine($"PerformListActorsToLoad {DateTime.Now} errorType:{e.GetType()} errorDescri:{e.Message}");
				throw;
			}

			return result;
		}

		private DatabaseType DatabaseType
		{
			get
			{
				return dairy == null ? DatabaseType.IN_MEMORY : dairy.DatabaseType;
			}
		}

		private readonly CommandCache actionCommands = new CommandCache();

		// Reflection anchors for cross-class access (see Reaction.IsActionKnown).
		// Using nameof here ensures these break at compile time if the underlying members are renamed.
		internal const string ActionCommandsFieldName = nameof(actionCommands);
		internal const string ContainsActionMethodName = nameof(CommandCache.ContainsAction);

		internal readonly ConcurrentDictionary<string, Program> QuerysEnCache = new ConcurrentDictionary<string, Program>();

		private class CommandCache
		{
			private readonly Dictionary<string, CommandCacheEntry> cacheDeCmdsPorScript = new Dictionary<string, CommandCacheEntry>();
			private readonly Dictionary<int, CommandCacheEntry> cacheDeCmdsPorId = new Dictionary<int, CommandCacheEntry>();
			internal CommandCacheEntry Agregar(int id, string script, Program program)
			{
				if (id < 0) throw new ArgumentNullException(nameof(id));
				ArgumentNullException.ThrowIfNullOrWhiteSpace(script);
				ArgumentNullException.ThrowIfNull(program);

				CommandCacheEntry cacheDeComandosEntry = new CommandCacheEntry(id, script, program);

				cacheDeCmdsPorScript.TryAdd(script, cacheDeComandosEntry);
				cacheDeCmdsPorId.TryAdd(id, cacheDeComandosEntry);

				return cacheDeComandosEntry;
			}

			internal bool TryGetValue(string script, out CommandCacheEntry statements)
			{
				return cacheDeCmdsPorScript.TryGetValue(script, out statements);
			}

			internal bool TryGetValue(int id, out CommandCacheEntry CacheDeComando)
			{
				return cacheDeCmdsPorId.TryGetValue(id, out CacheDeComando);
			}

			internal bool ContainsAction(int actionId)
			{
				return cacheDeCmdsPorId.ContainsKey(actionId);
			}
			internal bool ContainsAction(string script)
			{
				return cacheDeCmdsPorScript.ContainsKey(script);
			}
		}

		internal class CommandCacheEntry
		{
			private readonly int id;
			private readonly string script;
			private readonly Program program;
			internal CommandCacheEntry(int id, string script, Program program)
			{
				ArgumentNullException.ThrowIfNullOrWhiteSpace(script);
				ArgumentNullException.ThrowIfNull(program);

				this.id = id;
				this.script = script;
				this.program = program;
			}

			internal int Id { get { return id; } }

			internal string Script { get { return script; } }

			internal Program Program { get { return program; } }

		}

		internal static void RegisterShutdownHandlers(Action shutdownCallback)
		{
			ArgumentNullException.ThrowIfNull(shutdownCallback);

			// Registrar handlers para SIGTERM y SIGINT (Ctrl+C) para graceful shutdown
			// Kubernetes sends SIGTERM to the pod before forcing a kill.
			Console.CancelKeyPress += (sender, e) =>
			{
				e.Cancel = true; // Prevent immediate termination.
				System.Diagnostics.Debug.WriteLine("[ActorHandler] SIGINT (Ctrl+C) received. Initiating graceful shutdown...");
				shutdownCallback();
			};

			AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
			{
				System.Diagnostics.Debug.WriteLine("[ActorHandler] SIGTERM/ProcessExit received. Initiating graceful shutdown...");
				shutdownCallback();
			};

			System.Diagnostics.Debug.WriteLine("[ActorHandler] Shutdown handlers registered (SIGTERM, SIGINT).");
		}

		internal class ConcurrentParametersPool
		{
			private readonly ConcurrentStack<Parameters> _objects = new ConcurrentStack<Parameters>();
			private readonly int _maxPoolSize;
			private int _count = 0;

			internal ConcurrentParametersPool(int maxPoolSize = 200)
			{
				if (maxPoolSize <= 0) throw new LanguageException($"{nameof(ConcurrentParametersPool)} maxPoolSize {maxPoolSize} must be greater than 0.");
				_maxPoolSize = maxPoolSize;
			}

#if PUPPETEER_HIDE_INTERNALS
			[DebuggerHidden]
#endif
			internal Parameters Rent()
			{
				if (_objects.TryPop(out var item))
				{
					Interlocked.Decrement(ref _count);
					item.PurgeUserParameters();
					return item;
				}
				var result = new Parameters();
				result.SystemParameter<DateTime>("Now", default(DateTime));
				result.SystemParameter<IpAddress>("Ip", IpAddress.DEFAULT);
				result.SystemParameter<UserInLog>("User", UserInLog.ANONYMOUS);
				return result;
			}

#if PUPPETEER_HIDE_INTERNALS
			[DebuggerHidden]
#endif
			internal void Return(Parameters item)
			{
				ArgumentNullException.ThrowIfNull(item);
				item.Clear();
				if (Volatile.Read(ref _count) < _maxPoolSize)
				{
					_objects.Push(item);
					Interlocked.Increment(ref _count);
				}
			}
		}

		private class ConcurrentParsersPool
		{
			private readonly ConcurrentStack<Parser> _objects = new ConcurrentStack<Parser>();
			private readonly int _maxPoolSize;
			private int _count = 0;
			private readonly DomainLibraries _libraries;
		private readonly SymbolTable _symbolTable;

		internal ConcurrentParsersPool(DomainLibraries libraries, SymbolTable symbolTable, int maxPoolSize = 200)
			{
				if (maxPoolSize <= 0) throw new LanguageException($"{nameof(ConcurrentParsersPool)} maxPoolSize {maxPoolSize} must be greater than 0.");
				_libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
				_symbolTable = symbolTable ?? throw new ArgumentNullException(nameof(symbolTable));
				_maxPoolSize = maxPoolSize;
			}

			internal Parser Rent()
			{
				if (_objects.TryPop(out var item))
				{
					Interlocked.Decrement(ref _count);
					return item;
				}
				return new Parser(_libraries, _symbolTable);
			}

			internal void Return(Parser item)
			{
				ArgumentNullException.ThrowIfNull(item);
				if (Volatile.Read(ref _count) < _maxPoolSize)
				{
					_objects.Push(item);
					Interlocked.Increment(ref _count);
				}
			}
		}

	}
}
