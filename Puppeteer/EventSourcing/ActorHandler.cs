using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Formatters;
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
	internal class ActorHandler : IActorEventJournalClient, IActorIntrospection
	{
		private readonly SymbolTable symbolTable;
		private readonly DomainLibraries libraries;
		internal Assembly[] LibraryAssemblies { get; }

		internal readonly ConcurrentParametersPool ParametersPool;
		internal readonly ConcurrentParsersPool ParsersPool;

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

			// If the caller does not supply libraries, fall back to the actor's assembly (back-compat).
			// The idiomatic path is to pass the domain DLLs explicitly.
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

		// Per-actor logger (source-of-truth). The default ConsoleLogger is useful in
		// development: Error -> stderr, Debug -> stdout. The host injects its impl
		// (Serilog, MEL, NLog) via Actor.UseLogger(...). Each ActorHandler has
		// its own sink; two actors in the same process can have different
		// loggers without clobbering each other.
		private IPuppeteerLogger logger = new ConsoleLogger();
		public IPuppeteerLogger Logger => logger;
		internal void UseLogger(IPuppeteerLogger newLogger)
		{
			if (newLogger == null) throw new ArgumentNullException(nameof(newLogger));
			logger = newLogger;
		}

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
					// Plan 10: the failure handler is the non-delivery mirror of the
					// ack handler — the transport invokes it when it declares an
					// envelope dead (dead-letter / exhausted retries), and the actor
					// journals the non-delivery verdict through the same 1-writer path.
					value.RegisterFailureHandler(HandleTellFailure);
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

		// Follower mode: only the primary has authority to write to the canonical
		// journal (1-writer invariant). When this flag is on, the Reactions'
		// Tell terminators DO execute (they build the envelope and enqueue
		// it into PendingTells) and the envelope IS dispatched via Transport
		// after releasing the lock — but the `tell ...` entry is NOT written to the
		// actor's shared journal. Set by Performance.Start(asFollower:true) before
		// starting the Cued reactions; the primary leaves it false (default) and
		// journals normally.
		//
		// Stage 2 (dispatch-without-journaling) implemented: the write gate
		// lives in ExecuteCommandWithWriteLock (writeNewEntry=false when
		// SuppressReactionJournaling && InReactionAction), not in TellStatement.
		internal bool SuppressReactionJournaling { get; set; }

		// Provider of the live role for the ReactionActivation gate
		// (DirectorOnly / CastOnly / Company). Default: the actor acts as
		// director/primary — a standalone actor (without Stage or Performance) is
		// the only writer of itself, so it runs DirectorOnly + Company and
		// never CastOnly. Choreography overrides it with the live role: the P2P
		// Stage passes () => IsDirector; the Theater Performance () => !isFollower
		// (changes at the handover). It is consulted on each Reaction.Execute so
		// a replicated fan-out does not re-fire the reaction on the wrong node.
		private Func<bool> actingAsDirectorProvider = () => true;
		internal bool IsActingAsDirector => actingAsDirectorProvider();
		internal void SetActingAsDirectorProvider(Func<bool> provider)
		{
			ArgumentNullException.ThrowIfNull(provider);
			actingAsDirectorProvider = provider;
		}

		// Transient check from a Causation.Continue(check:, ...). It is NOT evaluated
		// here (the origin would always satisfy it); it is baked into the TellEnvelope.Check
		// that TellStatement.Execute builds during the body's PerformCmd, so that
		// the RECEIVER runs it as a CheckThenCommand. Set/cleared by
		// ExecuteCausation; read by TellStatement via SymbolTable.
		internal string CausationTellCheck
		{
			get => symbolTable.CurrentCausationCheck;
			set => symbolTable.CurrentCausationCheck = value;
		}

		// Shadow Replay — S1 (handoff_shadow_S1_implementation.md / design §3.0).
		// When IsShadow is on, this ActorHandler is a laboratory derivation
		// of a production actor: it reads the real journal (replay) but
		// writes to its OWN storage and produces NO external effect. The isolation
		// is driven from here: a Reaction's cross-actor Tells are NOT dispatched
		// (they are dropped in the PendingTells drain) and EnsureTransportConfigured does not
		// require a Transport. The shadow is also not registered as a
		// Materialization destination of the primary (it is an unregistered reader — it does not block the
		// primary's Distill), by construction: CreateShadow never calls
		// primary.Materialization.Register. The shadow is an actor primitive,
		// NOT a Performance subtype — a ShadowPerformance hosts it by
		// composition.
		internal bool IsShadow { get; private set; }

		// Shadow Replay — S3 (skip-preview). When on (only valid on a
		// shadow), Elide reactions run in dry-run: they capture the batch in
		// Reaction.WouldSkip but do NOT commit the elision (no
		// MarkEventsAsElidedWithCheckpoint). Default false; turn on via EnableSkipPreview().
		internal bool SkipPreviewEnabled { get; private set; }

		internal void EnableSkipPreview()
		{
			if (!IsShadow) throw new LanguageException("Skip-preview (dry-run of Elide) is only valid on a shadow. Build it via actor.Shadow(cfg) / ActorHandler.CreateShadow(cfg).");
			SkipPreviewEnabled = true;
		}

		// Shadow replay source: the primary actor's journal in read-only
		// mode. Set only on the shadow handler (via CreateShadow). The
		// shadow reads raw records from the primary through SyncUntil and re-applies them against
		// its own storage. null on a normal handler.
		private DiaryStorage shadowReplaySource;

		// Shadow Replay — S2 (continuous mirror). shadowingActive: the
		// StartShadowing loop is running. shadowingTask: the mirror's background Task.
		// SHADOW_POLL_MILLIS: poll interval for the primary's head.
		private volatile bool shadowingActive;
		private Task shadowingTask;
		private const int SHADOW_POLL_MILLIS = 50;

		// Shadow Replay — S1. Actor primitive: produces an isolated ActorHandler
		// (its OWN storage, IsShadow=true) fed by replay of THIS actor's journal
		// (the primary). Same Libraries (identical domain). Reactions are NOT
		// cloned automatically from the primary (the builder is imperative and not
		// serializable in S1); the caller re-declares them via cfg.ConfigureReactions
		// with the same Theme A API pointing at the shadow.
		//
		// Isolation guard: the shadow's storage must be DIFFERENT from the primary's.
		// It is validated by actor name — the shadow runs with a derived name
		// (`<primary>-shadow-<Id>`), so that per-name backends (InMemory) or
		// per-path-with-name backends (FileSystem) never share physical storage with the
		// primary even if the connection matches. In addition, if the shadow's connection
		// is literally the primary's AND the backend does not partition by name, it is
		// rejected explicitly.
		internal ActorHandler CreateShadow(ShadowConfig cfg)
		{
			ArgumentNullException.ThrowIfNull(cfg);
			if (dairy == null) throw new LanguageException("Cannot create a shadow before the primary actor has EventSourcingStorage configured: there is no journal to replay from.");

			string shadowName = this.Name + "-shadow-" + cfg.Id;
			if (string.Equals(shadowName, this.Name, StringComparison.Ordinal))
				throw new LanguageException("Shadow name collided with the primary actor name. This is impossible by construction (the name carries a '-shadow-' infix); if you see this, ShadowConfig.Id was empty.");

			// Build the shadow actor in the SAME family (V1/V2) as the primary,
			// with the same LibraryAssemblies (identical domain) and the same
			// CompiledModePolicy. A shadow cannot be of a different family: the
			// handler's rehydration branches discriminate on `actor is ActorV1`.
			Actor shadowActor;
			if (this.actor is ActorV1)
				shadowActor = new ActorV1(shadowName, this.LibraryAssemblies);
			else if (this.actor is ActorV2)
				shadowActor = new ActorV2(shadowName, this.LibraryAssemblies);
			else
				throw new LanguageException($"Cannot shadow an actor of type '{this.actor.GetType().Name}': only ActorV1 and ActorV2 families are supported in S1.");

			shadowActor.CompiledModePolicy = this.actor.CompiledModePolicy;

			ActorHandler shadow = shadowActor.Handler;
			shadow.IsShadow = true;

			// The shadow's OWN storage — NEVER the primary's. This wires the Diary,
			// leaves the handler in Recovered state (IsAlive) and connects the storage to
			// the shadow's Reactions.
			shadow.EventSourcingStorage(cfg.ShadowStorageType, cfg.ShadowStorageConnection);

			// Hard guard: the shadow's physical storage cannot be the same object
			// as the primary's. With distinct actor names this holds by
			// construction across the 4 backends; the assert protects against a
			// future regression in the storage factory.
			DiaryStorage shadowStorage = shadow.TryGetDiaryStorage();
			DiaryStorage primaryStorage = this.TryGetDiaryStorage();
			if (shadowStorage != null && ReferenceEquals(shadowStorage, primaryStorage))
				throw new LanguageException("Shadow isolation violated: the shadow's storage resolved to the SAME storage instance as the primary. A shadow must write to its own storage and never touch the primary's journal.");

			// Replay source = the primary's journal, read-only. The shadow reads raw
			// records from here in SyncUntil and re-applies them against its own storage.
			shadow.shadowReplaySource = primaryStorage;

			// Reactions: the caller re-declares the ones it wants to observe + experimental ones
			// (same Theme A API pointing at the shadow). They are not cloned automatically
			// in S1.
			cfg.ConfigureReactions?.Invoke(shadowActor);

			return shadow;
		}

		// Accessor for the Actor wrapping this handler. Used by the
		// actor.Shadow(cfg) facade to build the Shadow object over the shadow actor
		// just created by CreateShadow.
		internal Actor ShadowActor => actor;

		// Shadow Replay — S1. SyncUntil(toEntryId): replay of the primary's journal
		// from GENESIS (EntryId 0) up to toEntryId inclusive, applied to the
		// shadow's OWN storage. It is a CEILING, not a floor — replay ALWAYS starts at genesis
		// because the state at toEntryId depends on the entire prior history. After
		// SyncUntil the shadow is FORKED: it accepts local PerformCmd on its own
		// storage (a divergent timeline). Continuous mirror (StartShadowing) is
		// S2 and is mutually exclusive with the fork — not implemented here.
		//
		// V1+V2 mechanism (signed: S1 serves both families): the primary's raw
		// records are COPIED into the shadow's storage via the structured write API
		// (WriteScriptEntry for V1; WriteDefineEntry + WriteInvocationEntry for V2),
		// preserving EntryId / OccurredAt / ExposeData and the V2 data
		// (DefineStatementText + Arguments that the MaterializationRecord already carries).
		// Then the shadow's in-memory state is rehydrated from its own storage via
		// CatchUpFromJournal — standard rehydration handles V1 (Script) and V2
		// (Define -> actionCommands, Invocation) uniformly. It is NOT re-executed
		// as a new command: copy+rehydrate is cross-backend, preserves the primary's
		// exact EntryIds, and reuses the already-proven replay machinery (the same as
		// red-black / CatchUpFromJournal).
		internal void SyncUntil(long toEntryId)
		{
			if (!IsShadow) throw new LanguageException("SyncUntil is only valid on a shadow ActorHandler. Build one via actor.Shadow(cfg) / ActorHandler.CreateShadow(cfg).");
			if (toEntryId < 0) throw new ArgumentException("toEntryId must be non-negative", nameof(toEntryId));
			if (shadowReplaySource == null) throw new LanguageException("Shadow replay source is not configured. This shadow was not produced by CreateShadow.");
			if (dairy == null) throw new LanguageException("Shadow storage is not configured. Call EventSourcingStorage on the shadow before SyncUntil.");
			if (shadowingActive) throw new LanguageException("Cannot SyncUntil while continuous shadowing is active. Continuous mirror and point-in-time fork are mutually exclusive — call StopShadowing first.");

			// Copy raw records from the primary starting at GENESIS (ceiling = toEntryId) to
			// the shadow's own storage, then rehydrate. See CopyPrimaryRecordsToShadow.
			long lastWritten = CopyPrimaryRecordsToShadow(0, toEntryId);
			if (lastWritten > 0)
				CatchUpFromJournal(lastWritten);
		}

		// Shadow Replay. Copies raw records from the primary (afterEntryId exclusive;
		// toEntryIdCap inclusive, 0 => no cap) to the shadow's OWN storage via the
		// structured API — V1 (Script) and V2 (Define + Invocation) uniform, preserving
		// EntryId / OccurredAt / ExposeData and the V2 data (DefineStatementText +
		// Arguments that the MaterializationRecord already carries). Unregistered reader: it does NOT
		// go through Materialization.Register, so it does not participate in the primary's watermark
		// nor block its Distill. Returns the last EntryId written (0 if nothing). It does NOT
		// rehydrate — the caller does that (CatchUpFromJournal).
		private long CopyPrimaryRecordsToShadow(long afterEntryId, long toEntryIdCap)
		{
			List<Puppeteer.EventSourcing.DB.MaterializationRecord> records = new List<Puppeteer.EventSourcing.DB.MaterializationRecord>();
			shadowReplaySource.ReadRecordsAfter(afterEntryId, records);

			long lastWritten = 0;
			foreach (var record in records)
			{
				if (toEntryIdCap > 0 && record.EntryId > toEntryIdCap) break;

				switch (record.Kind)
				{
					case Puppeteer.EventSourcing.DB.MaterializationRecordKind.Script:
						if (string.IsNullOrEmpty(record.Script))
							throw new LanguageException($"Primary journal record at EntryId {record.EntryId} is a Script with empty text — cannot copy it into the shadow.");
						dairy.WriteScriptEntry(record.EntryId, record.Script, record.OccurredAt, record.ExposeData);
						break;

					case Puppeteer.EventSourcing.DB.MaterializationRecordKind.Define:
						dairy.WriteDefineEntry(record.ActionId, record.DefineStatementText, record.EntryId, record.OccurredAt, record.ExposeData);
						break;

					case Puppeteer.EventSourcing.DB.MaterializationRecordKind.Invocation:
						dairy.WriteInvocationEntry(record.ActionId, record.EntryId, record.OccurredAt, record.Arguments, record.ExposeData);
						break;

					default:
						throw new LanguageException($"Unknown journal record kind '{record.Kind}' at EntryId {record.EntryId}.");
				}

				lastWritten = record.EntryId;
			}

			return lastWritten;
		}

		// Shadow Replay — S4. Seeds the shadow from its replay source up to toEntryId but
		// with a set of EntryIds marked as ELIDED before rehydrating, so that
		// rehydration skips them (RehydrateFromEvent filters IsEventElided). Used by the
		// elision-impact diff to build the "elided twin". Marks with sentinel reactionId
		// 0 (rehydration only checks presence of the EntryId, not the reactionId).
		internal void SeedElided(long toEntryId, long[] elideEntryIds)
		{
			if (!IsShadow) throw new LanguageException("SeedElided is only valid on a shadow ActorHandler.");
			if (shadowReplaySource == null) throw new LanguageException("Shadow replay source is not configured.");
			if (dairy == null) throw new LanguageException("Shadow storage is not configured.");

			long lastWritten = CopyPrimaryRecordsToShadow(0, toEntryId);

			if (elideEntryIds != null && elideEntryIds.Length > 0)
			{
				DiaryStorage storage = TryGetDiaryStorage();
				if (storage != null && storage.EventElisionStorage != null)
					// positive sentinel reactionId (MarkEventsAsElided requires > 0). The twin
					// has no real reactions, so it does not collide; rehydration only
					// checks presence of the EntryId, not the reactionId.
					storage.EventElisionStorage.MarkEventsAsElided(elideEntryIds, 1, DateTime.UtcNow);
			}

			if (lastWritten > 0)
				CatchUpFromJournal(lastWritten);
		}

		internal bool IsShadowingActive => shadowingActive;

		// Shadow Replay — S2. StartShadowing(): continuous mirror — a background loop
		// that follows the primary's head in near-real-time (incremental pull of new
		// records + rehydration). Mutually exclusive with the SyncUntil fork. Lossy
		// by design (B.2): if an iteration fails, it is retried on the next one. Guarded
		// against double-start. Stop via StopShadowing() (called by the Shadow's Dispose).
		internal void StartShadowing()
		{
			if (!IsShadow) throw new LanguageException("StartShadowing is only valid on a shadow ActorHandler. Build one via actor.Shadow(cfg) / ActorHandler.CreateShadow(cfg).");
			if (shadowReplaySource == null) throw new LanguageException("Shadow replay source is not configured. This shadow was not produced by CreateShadow.");
			if (dairy == null) throw new LanguageException("Shadow storage is not configured. Call EventSourcingStorage on the shadow before StartShadowing.");
			if (shadowingActive) throw new LanguageException("This shadow is already shadowing (continuous mirror).");

			shadowingActive = true;
			shadowingTask = Task.Run(() => ShadowingLoop());
		}

		internal void StopShadowing()
		{
			shadowingActive = false;
			Task task = shadowingTask;
			shadowingTask = null;
			if (task != null)
			{
				try { task.Wait(TimeSpan.FromSeconds(5)); }
				catch { }
			}
		}

		private void ShadowingLoop()
		{
			while (shadowingActive)
			{
				try
				{
					long from = this.EntryId;
					long lastWritten = CopyPrimaryRecordsToShadow(from, 0);
					if (lastWritten > from)
						CatchUpFromJournal(lastWritten);
				}
				catch
				{
					// Lossy by design: a transient failure does not stop the mirror;
					// the next iteration retries from this.EntryId.
				}

				if (shadowingActive)
					Thread.Sleep(SHADOW_POLL_MILLIS);
			}
		}

		// Shadow Replay — S1. TTL kill-all of the shadow's storage. Applies only to a
		// shadow handler (IsShadow). For the InMemory backend it clears the shadow
		// actor's shared event list (its name is unique, it does not touch the primary).
		// For FileSystem/SQL, physical deletion of the schema/PVC is the
		// host/operator's responsibility (S6 K8s) — here it is a silent no-op (a
		// production DB is not deleted by accident). Idempotent.
		internal void TryClearShadowStorage()
		{
			if (!IsShadow) return;
			DiaryStorage storage = dairy?.Storage;
			if (storage is DiaryStorageInMemory inMemory)
			{
				inMemory.Clear();
			}
		}

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
					dairy.WriteScriptEntry(ackEntryId, ackSentence, now, exposeData: null);

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

		// Plan 10 of the Tell primitive roadmap: non-delivery ingestion — the
		// failure-side mirror of HandleAckEnvelope. Invoked by the configured
		// ITransport (by declaration: a dead-letter / exhausted-retries callback)
		// or by the post-rehydration recovery pass (by citation). Records the
		// TERMINAL non-delivery verdict on the actor's own journal so the log
		// becomes self-sufficient about the FATE of the tell, not just its
		// issuance. Acquires the write lock manually — there is no PerformCmd
		// around this callback. The verdict is NOT pair-elided: unlike a completed
		// ack round-trip, a non-delivery is a fact worth keeping visible in the log.
		internal void HandleTellFailure(TellFailure failure)
		{
			if (string.IsNullOrEmpty(failure.Id))
			{
				Debug.WriteLine($"[Tell.Failure] rejected non-delivery with empty envelope.Id (witness '{failure.Witness}') — the originating tell id must round-trip.");
				return;
			}

			if (!symbolTable.IsTellEnvelopeIdKnown(failure.Id))
			{
				Debug.WriteLine($"[Tell.Failure] orphan non-delivery '{failure.Id}' (witness '{failure.Witness}') — no matching tell was ever sent from this actor. Likely transport bug or split-brain restart.");
				return;
			}

			rwLock.EnterWriteLock();
			try
			{
				// Terminal-fate precedence: an envelope already acked is delivered —
				// a later failure report is stale and must not overwrite the verdict.
				if (symbolTable.IsTellEnvelopeIdAcked(failure.Id))
				{
					Debug.WriteLine($"[Tell.Failure] stale non-delivery '{failure.Id}' (witness '{failure.Witness}') — already acked; delivered wins.");
					return;
				}

				if (symbolTable.IsTellEnvelopeIdNotDelivered(failure.Id))
				{
					Debug.WriteLine($"[Tell.Failure] duplicate non-delivery '{failure.Id}' (witness '{failure.Witness}') — already recorded.");
					return;
				}

				// Witness fallback: a transport may decline to name itself in the
				// failure report; the journaled sentence still needs a witness.
				string witness = string.IsNullOrEmpty(failure.Witness) ? (_transport?.WitnessName ?? "transport") : failure.Witness;

				symbolTable.MarkTellEnvelopeIdNotDelivered(failure.Id);

				if (dairy != null)
				{
					string sentence = RenderNotDeliveredSentence(failure.Id, witness);
					long entryId = TakeAndIncrementEntryId();
					DateTime now = DateTime.Now;
					dairy.WriteScriptEntry(entryId, sentence, now, exposeData: null);
				}
			}
			finally
			{
				rwLock.ExitWriteLock();
			}
		}

		// Render the canonical non-delivery sentence:
		// `tell '<id>' not delivered, per '<witness>';` — same shape
		// TellNotDeliveredStatement.Write produces, kept in lockstep so that the
		// journal and live-emitted entries are indistinguishable.
		private static string RenderNotDeliveredSentence(string envelopeId, string witness)
		{
			StringBuilder sb = new StringBuilder(64);
			sb.Append("tell '");
			sb.Append(envelopeId);
			sb.Append("' not delivered, per '");
			sb.Append(witness);
			sb.Append("';");
			return sb.ToString();
		}

		// Plan 10 of the Tell primitive roadmap: post-rehydration tell-fate
		// recovery. Closes the crash window between a tell's journal commit and its
		// post-commit dispatch: if the actor died in that window, the in-memory
		// envelope was lost, so the tell is journaled as issued yet never
		// dispatched, never acked, and — without this pass — unrecoverable.
		//
		// After replay reconstructs the dedup state, the set of PENDING tells
		// (issued, neither acked nor not-delivered) is read back from the journal.
		// For each, the transport TESTIFIES the envelope's fate (by citation) and
		// the journal records the verdict:
		//   - Delivered: only our ack was lost -> journal the ack (existing path).
		//   - Failed:    journal a non-delivery verdict with the transport as witness.
		//   - InFlight:  leave pending — the transport still owns it and will settle
		//                it later through its ack / failure handler.
		//
		// This runs OUTSIDE the rehydration write lock and after RecoveringState has
		// been cleared, so it is a deliberate post-rehydration action — not part of
		// the replay (replay must never re-emit live messages). The verdicts it
		// journals (ack / non-delivery) replay into terminal dedup state on any
		// subsequent rehydration, so the transport is cited at most once per tell.
		internal void RecoverPendingTells()
		{
			// Shadow isolation (S1): a shadow produces zero external effect; it
			// neither cites a transport nor journals verdicts on recovery.
			if (IsShadow) return;
			ITransport transport = _transport;
			if (transport == null) return; // No delivery authority to testify.
			if (dairy == null) return;     // No journal to record the verdict into.

			IReadOnlyList<(string EnvelopeId, TellRecoveryInfo Info)> pending = symbolTable.CollectPendingTellRecoveries();
			if (pending.Count == 0) return;

			foreach ((string envelopeId, TellRecoveryInfo info) in pending)
			{
				TellFate fate;
				try
				{
					fate = transport.GetFateAsync(envelopeId).GetAwaiter().GetResult();
				}
				catch (Exception e)
				{
					// Testimony unavailable (transport unreachable, etc.) — leave the
					// tell pending; a future declaration callback can still settle it.
					Debug.WriteLine($"[Tell.Recovery] could not obtain fate for '{envelopeId}' (witness '{info.Witness}'): {e.GetType().Name}: {e.Message}. Leaving pending.");
					continue;
				}

				switch (fate)
				{
					case TellFate.Delivered:
						// Only the ack round-trip was lost. Journal the ack through the
						// same single-writer path a live ack uses.
						HandleAckEnvelope(new AckEnvelope(envelopeId, info.TargetClass, info.TargetId));
						break;
					case TellFate.Failed:
						HandleTellFailure(new TellFailure(envelopeId, info.Witness));
						break;
					case TellFate.InFlight:
					default:
						// Leave pending — the transport retains ownership of delivery.
						break;
				}
			}
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

		// Paper 5 / Materialize v2 — Phase 0. Sub-namespace to administer the actor's
		// destinations. The instance is materialized lazily on first access; the
		// concrete storage is obtained from the Diary via TryGetMaterializationCheckpointStorage()
		// and therefore requires EventSourcingStorage configured first (consistent with
		// how Reactions needs SetDairyStorage after EventSourcingStorage).
		private Materialization materialization;
		internal Materialization Materialization => materialization ??= new Materialization(this);

		// Journal-outbox emit — delivery side. Lazy like Materialization; resolves
		// the diary's OutboxStorage through TryGetDiaryStorage, so it needs
		// EventSourcingStorage configured first. See notes/reactions-outbox-emit.md.
		private OutboxRelay outboxRelay;
		internal OutboxRelay OutboxRelay => outboxRelay ??= new OutboxRelay(this);

		internal Puppeteer.EventSourcing.DB.MaterializationCheckpointStorage TryGetMaterializationCheckpointStorage()
		{
			return dairy?.Storage?.MaterializationCheckpointStorage;
		}

		// Phase 2 — Materialize v2 wire verb (a) EnviameDesde. Materialization.cs accesses
		// the DiaryStorage to enumerate raw journal records without going through the
		// public rehydration API (which filters elided ones).
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

		internal void EventSourcingStorage(DatabaseType dbType, string connection)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connection);

			dairy = new Diary(dbType, connection, eventJournalClient: this);

			Console.WriteLine($"Starting {this.GetType()}'s Actor");

			EventSourcingStorage(dairy);

			// Plan 10 of the Tell primitive roadmap: now that replay has
			// reconstructed the tell dedup state, settle the fate of any tell left
			// PENDING by the crash window (journaled-but-never-dispatched). Primary
			// path only — a follower replicates the primary's verdicts and must not
			// author its own (1-writer invariant). Runs after replay (RecoveringState
			// cleared) so it is a deliberate post-rehydration action, never part of
			// the replay.
			RecoverPendingTells();

			reactions.SetDairyStorage(dairy.Storage);

			if (this.OnAfterRecovering != null) this.OnAfterRecovering(dbType, connection, this.Name, this.EntryId);

		}

		// Configure storage WITHOUT rehydration. Useful for IActorIntrospection: the CLI
		// wants to read raw entries from any journal without needing the domain's
		// LibraryAssemblies (which may not be available in the
		// generic puppeteer-cli binary). It only enables the raw read paths
		// (ReadRecordsAfter); invocation / rehydration / reactions stay
		// inactive. If one tries to use Perform / Tell / Reactions on an actor
		// configured through here, they will fail because the symbol table is empty and the
		// libraries were not loaded.
		internal void ConfigureStorageForIntrospection(DatabaseType dbType, string connection)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connection);
			if (dairy != null)
				throw new LanguageException($"Actor '{Name}' already has EventSourcingStorage configured.");

			dairy = new Diary(dbType, connection, eventJournalClient: this);
		}

		internal void EventSourcingStorage(DatabaseType dbType, string connection, ActorFollower actorFollower)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connection);

			dairy = new Diary(dbType, connection, eventJournalClient: actorFollower);

			long lastProcessedEntryId = EventSourcingStorage(dairy);

			dairy.SaveLastProcessedEntryId(actorFollower.FollowerId, lastProcessedEntryId);
		}

		private Parser parserForRecovering;
		private BlockingCollection<EventData> eventsQueue;
		private long EventSourcingStorage(Diary dairy)
		{
			// Defensive validation: a silent NRE originating here is very hard to
			// diagnose (no message, the stack only points to this method). If on some
			// path we arrive with dairy or a required collaborator null, we want an
			// explicit LanguageException stating which piece was missing.
			if (dairy == null) throw new LanguageException("Diary backend not initialized. EventSourcingStorage(Diary) was invoked with a null Diary. The caller path constructs the Diary in EventSourcingStorage(DatabaseType, string, string); if you see this, that construction silently returned null — likely a regression in the storage backend factory.");
			if (libraries == null) throw new LanguageException("DomainLibraries is null. The ActorHandler constructor populates 'libraries' via DomainLibraries.GetOrLoad(LibraryAssemblies); if you see this, the loader returned null — likely a regression in the library-loading path.");
			if (symbolTable == null) throw new LanguageException("SymbolTable is null. The ActorHandler constructor populates 'symbolTable' inline; if you see this, the field was never assigned — instance is corrupt and cannot start.");

			if (parserForRecovering == null) parserForRecovering = new Parser(libraries, symbolTable);
			if (eventsQueue == null) eventsQueue = new BlockingCollection<EventData>(MAX_NORMAL_LOAD_POOL_SIZE);

			BlockingCollection<Program> preparedQueue = new BlockingCollection<Program>(MAX_NORMAL_LOAD_POOL_SIZE);
			BlockingCollection<Program> parsedQueue = new BlockingCollection<Program>(MAX_NORMAL_LOAD_POOL_SIZE);

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

				// Rehydration pipeline (3 stages): RehydrateFromEvent -> eventsQueue ->
				// parser -> parsedQueue -> resolver -> preparedQueue -> exec.
				// Each task wraps its foreach in try/finally to guarantee CompleteAdding
				// on the output queue (and the input one) even if it throws.
				// Without this, an exception in any stage would leave the downstream workers blocked
				// indefinitely in GetConsumingEnumerable() and Task.WaitAll would never return.
				//
				// There used to be an extra stage (preparer + collector) where the parse ran in a
				// Task.Run that was awaited immediately. Since GenerateAndRentProgram uses a single
				// parserForRecovering, that did NOT parallelize the parse: it only added a Task
				// allocation + two thread-pool hops + a queue handoff (programTaskQueue) per entry.
				// It was fused into a single synchronous parse stage; the parse||resolve||exec overlap
				// is still provided by the queues and the Task.Run of each stage.
				//
				// Permissive rehydration (signed 2026-05-19):
				// if an individual record fails in any stage (parser, resolver, executor),
				// the error is logged via IPuppeteerLogger.Error with entryId + script + exception
				// and rehydration CONTINUES with the next record. This is the contract of the
				// previous Puppeteer (Dairy.cs:508-535) that was lost in the
				// multi-stage pipeline refactor; the consumer being migrated expected this
				// behaviour. If the host wants to be strict (abort on the first error),
				// it injects a custom IPuppeteerLogger that throws on Error -- the catches
				// let it escape as a faulted Task and Task.WaitAll propagates it.
				bool stageTiming = LabInstrumentation.StageTimingEnabled;
				// Snapshot of accumulated ticks BEFORE this rehydration: the LabInstrumentation
				// accumulators are static/global (they sum across all actors), so the
				// final print reports the DELTA of this run, not the process accumulated total.
				long parseTicksBefore = LabInstrumentation.ParseTicks;
				long resolveTicksBefore = LabInstrumentation.ResolveTicks;
				long executeTicksBefore = LabInstrumentation.ExecuteTicks;
				long replayCountBefore = LabInstrumentation.ReplayEventsCounted;
				long methodCacheHitsBefore = LabInstrumentation.MethodCacheHits;
				long methodCacheMissesBefore = LabInstrumentation.MethodCacheMisses;
				long methodCacheUncacheableBefore = LabInstrumentation.MethodCacheUncacheable;
				var parserTask = Task.Run(() =>
				{
					try
					{
						foreach (EventData retornableEventData in eventsQueue.GetConsumingEnumerable())
						{
							long parserEntryId = retornableEventData.EntryId;
							DateTime parserOccurredAt = retornableEventData.OccurredAt;
							string parserScript = (retornableEventData is ScriptEventData sed) ? sed.Script : null;
							try
							{
								long parseT0 = stageTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
								Program rentedProgram = GenerateAndRentProgram(retornableEventData);
								if (stageTiming) LabInstrumentation.AddParseTicks(System.Diagnostics.Stopwatch.GetTimestamp() - parseT0);
								// GenerateAndRentProgram returns null for orphan Invocations
								// (actionId cache miss), an unreachable path post-Phase-4 by
								// construction; it is silently skipped.
								if (rentedProgram != null)
								{
									parsedQueue.Add(rentedProgram);
								}
							}
							catch (Exception ex)
							{
								// The inner exception's type + message is interleaved into the
								// message because some hosts (e.g. ASP.NET with loggers
								// that do not chain ex.ToString()) discard the second line
								// that ConsoleLogger.Error writes via WriteLine(exception).
								// Without this the consumer only sees "Rehydration parser failed"
								// with no clue about what actually failed.
								Logger.Error(
									$"Rehydration parser failed. EntryId={parserEntryId}, OccurredAt={parserOccurredAt:O}, Cause={ex.GetType().FullName}: {ex.Message}, Script:\n{parserScript ?? "<action invocation>"}",
									ex);
							}
							finally
							{
								// The EventData is no longer needed: GenerateAndRentProgram copied
								// EntryId/OccurredAt/Arguments into the Program and the latter does not retain the
								// EventData. It is returned to the pool exactly once, on success or
								// error (previously the collector did this after the await).
								retornableEventData.ReturnToEventDataPool();
							}
						}
					}
					finally
					{
						eventsQueue.CompleteAdding();
						parsedQueue.CompleteAdding();
					}
				});

				var resolverTask = Task.Run(() =>
				{
					try
					{
						foreach (Program rentedProgram in parsedQueue.GetConsumingEnumerable())
						{
							long resolverEntryId = rentedProgram.EntryId;
							string resolverScript = rentedProgram.Script;
							try
							{
								long resolveT0 = stageTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
								if (!actionCommands.ContainsAction(rentedProgram.Script))
									// Rehydration = replay of events that were ALREADY statically validated
									// when they ran live (the write path validates). Re-validating them
									// here is redundant by definition — it would only find errors that would
									// already have been found when writing them — and it adds CPU that contends with the
									// execution stage (rehydration is CPU-bound). The old 100%-interpreted
									// Puppeteer did NOT validate on replay (it resolved lazily at execution) and
									// rehydrated faster. SolveIdReferences is kept (the binding, which
									// execution needs and the old version also did).
									//
									// withStaticValidation is tied to HasEval: ValidateStatically only has a
									// necessary effect (non-validation) in its eval branch, where it propagates
									// types of globals for cross-entry resolution. Without evals (the whole journal) the
									// full validation is skipped; with evals (rare) that propagation is preserved.
									rentedProgram.SolveReferences(rentedProgram.Parameters, withStaticValidation: rentedProgram.HasEval);
								if (stageTiming) LabInstrumentation.AddResolveTicks(System.Diagnostics.Stopwatch.GetTimestamp() - resolveT0);

								preparedQueue.Add(rentedProgram);
							}
							catch (Exception ex)
							{
								// Inner exception interleaved into the message — see the
								// equivalent comment in the preparerTask. The second
								// line that ConsoleLogger.Error writes
								// (WriteLine(exception)) does not reach the host's stdout in
								// some consumers, leaving the team with no clue about the
								// LanguageException that ValidateStatically is throwing.
								Logger.Error(
									$"Rehydration resolver failed (static validation). EntryId={resolverEntryId}, Cause={ex.GetType().FullName}: {ex.Message}, Script:\n{resolverScript}",
									ex);
								ReturnProgram(rentedProgram);
							}
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
							long execEntryId = rentedProgram.EntryId;
							string execScript = rentedProgram.Script;
							bool executedOk = false;
							try
							{
								long execT0 = stageTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
								Perform(rentedProgram, rentedProgram.Parameters);
								if (stageTiming) LabInstrumentation.AddExecuteTicks(System.Diagnostics.Stopwatch.GetTimestamp() - execT0);
								executedOk = true;
							}
							catch (Exception ex)
							{
								// Inner exception interleaved — same reason as in
								// preparerTask/resolverTask: the consumer loses the
								// second line of ConsoleLogger.Error on hosts without
								// WriteLine(exception) capture.
								Logger.Error(
									$"Rehydration execution failed. EntryId={execEntryId}, Cause={ex.GetType().FullName}: {ex.Message}, Script:\n{execScript}",
									ex);
							}
							ReturnProgram(rentedProgram);

							if (executedOk)
							{
								// Paper 5 Lab 1: counts events applied during the bulk
								// replay of EventSourcingStorage (initial Start path).
								// ReplayPendingEventsForRedBlack covers the handover tail.
								// We only count successfully executed events — a permissive
								// error does NOT count as "applied".
								LabInstrumentation.IncrementReplayEventsCounted();
								LabInstrumentation.OnReplayEventCounted?.Invoke(execEntryId);
							}

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
					Task.WaitAll(parserTask, resolverTask, executionTask);
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

				// Per-stage time breakdown of THIS rehydration. The pipeline runs the 3
				// stages concurrently, so wall-clock ~= max(stage) + fill/drain: the stage
				// with the most ms is the bottleneck and decides where the next improvement pays off
				// (parse -> parallel parse; resolve -> collection-during-parse; exec -> serial floor,
				// the lever is the structural cache or making the interpreter cheaper). Printed only with
				// StageTimingEnabled (or PUPPETEER_STAGE_TIMING=1).
				if (stageTiming)
				{
					double freq = System.Diagnostics.Stopwatch.Frequency;
					double parseMs = (LabInstrumentation.ParseTicks - parseTicksBefore) * 1000.0 / freq;
					double resolveMs = (LabInstrumentation.ResolveTicks - resolveTicksBefore) * 1000.0 / freq;
					double executeMs = (LabInstrumentation.ExecuteTicks - executeTicksBefore) * 1000.0 / freq;
					long timedEvents = LabInstrumentation.ReplayEventsCounted - replayCountBefore;
					long mcHits = LabInstrumentation.MethodCacheHits - methodCacheHitsBefore;
					long mcMisses = LabInstrumentation.MethodCacheMisses - methodCacheMissesBefore;
					long mcUncacheable = LabInstrumentation.MethodCacheUncacheable - methodCacheUncacheableBefore;
					Console.WriteLine(
						$"[Puppeteer rehydration timing] actor={this.Name} events={timedEvents} " +
						$"parse={parseMs:F0}ms resolve={resolveMs:F0}ms exec={executeMs:F0}ms");
					Console.WriteLine(
						$"[Puppeteer rehydration methodcache] hits={mcHits} misses={mcMisses} uncacheable={mcUncacheable}");
				}

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
		// (signed: cross-stage atomicity is unnecessary because the director's
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
						// B.3.1: rehydration also observes the promotion candidate —
						// replaying 30k scripts in a legacy journal decrements the
						// counter the same way live writes do, so post-replay the
						// system arrives in the exact state it would have reached
						// running the same writes live. B.3.3 returns a
						// PromotionResult here only if the counter tipped AND the
						// candidate is not yet in the index — we DISCARD it during
						// rehydration because (signed clarification) nothing
						// changes: the Define only appears from the next
						// PerformCommand that is written onwards. Replay
						// must not write new journal entries; the in-memory
						// materialization (actionCommands + index update) is left
						// in place since it is naturally re-derivable from the
						// next live write.
						_ = ObservePromotionCandidate(program, scriptEvent.Script);
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

				// Now is a SYSTEM parameter excluded from the journal; on rehydration it is
				// re-injected from the journaled OccurredAt (deterministic, not wall-clock).
				// Applies to Script (V1, re-parsed) and Action (V2, reconstructed from the define
				// that already excludes Now). It is injected BEFORE SolveReferences/Perform: on the
				// first Perform, ExecuteExpression calls SolveReferences(program.Parameters)
				// so @Now resolves as a parameter.
				program.Parameters["Now", typeof(DateTime)] = eventData.OccurredAt;

				if (!actionCommands.ContainsAction(program.Script))
					program.SolveReferences(program.Parameters, withStaticValidation: true);

				Perform(program, program.Parameters);

				// B.1c: free the resolved AST of compiled cached Actions during
				// replay so post-restart memory is lean. ActionEventData only —
				// the program is the actionCommands entry. ScriptEventData
				// programs are ephemeral (discarded when this method returns) and
				// under AlwaysCompiled could be published-for-matching elsewhere,
				// so leave them untouched.
				if (eventData is ActionEventData)
					program.ReleaseStatements(this.DatabaseType);

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

					// Symmetry with PrepareCommandProgram (ActorHandler.cs:1209): the
					// live path always calls SetContextInfo() after parsing so that
					// each Statement has its Program backref. We replicate it here after
					// Rehydrate() so the rehydrated program is structurally
					// identical to the freshly-parsed one and the visitors that rely on
					// statement.Program (today EvalStatement) do not fail silently during
					// rehydration.
					program.SetContextInfo();

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

			// Now is a SYSTEM parameter excluded from the journal; it is re-injected from the
			// journaled OccurredAt for Script (V1) and Action (V2). It travels in program.Parameters
			// up to the execution stage of the rehydration pipeline, where the first
			// Perform runs ExecuteExpression -> SolveReferences(program.Parameters) and @Now
			// resolves as a parameter.
			program.Parameters["Now", typeof(DateTime)] = eventData.OccurredAt;

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


		// Playbill final refactor: V1 entry path. The (ip, user) signature survives for compat
		// with StageHook.PerformCmd(script) but the values are ignored — ip/user no longer
		// travel as script parameters. The Now injection (V1 backward compat) is done by
		// the PerformCmd(script, parameters, now) overload after PrepareCommandProgram so as not to
		// alter the IsScript/IsNewAction decision.
		internal string PerformCmd(string script, string ip, string user)
		{
			Parameters parameters = ParametersPool.Rent();

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

		// Phase 4.5 + Playbill final refactor: EMPTY_PARAMETERS no longer preloads ip/user
		// (removed from the journal) nor Now (no longer a system param — only the V1 entry path
		// injects it via the indexer; V2 declares Now explicitly if the script needs it).
		private static Parameters EmptyParameters()
		{
			Parameters parameters = new Parameters();
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
			_ = actionCommands.Add(actionId, actionScript, program);
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
		// statements). Q1 = (a) signed at start of Phase 4: post-replay, a write
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

			string parametersDeclarationText = Parameters.CanonicalDeclarationsToParametersString(defineStmt.ParametersText);
			if (!string.IsNullOrEmpty(parametersDeclarationText))
			{
				// DomainLibraries are passed so that an @parameter typed as a domain
				// enum (journaled by type name) resolves when reconstructing the
				// Parameters from text; without this the internal parser would only accept primitives.
				bodyProgram.Parameters = new Parameters(parametersDeclarationText, libraries);
			}

			_ = actionCommands.Add(actionId, canonicalBody, bodyProgram);
		}


		private class CommandPrepared
		{
			internal Program Program;
			internal CommandCacheEntry CacheEntry;
			internal JournalEntry Entry;
			internal string FormatedScriptForDairy;
			internal bool NeedsToSolveParameters;
			internal bool NeedsToSolveReferences;
			// B.3.3: non-null when the IsScript observation tipped a recurrent
			// promotion candidate over the threshold AND it had not been
			// promoted before. Carries everything the journal write needs to
			// emit an atomic Define + Invocation pair instead of a Script row.
			internal PromotionResult Promotion;
			// B.3.4: non-null when an incoming Script-shape was rerouted to
			// an already-promoted Action via promotionCandidateToActionId.
			// Carries the Parameters object populated with the values
			// extracted from the incoming script's literals — replaces the
			// caller's (typically EMPTY_PARAMETERS) for both LoadArguments
			// and ArgumentsAsString at the journal-write step.
			internal Parameters PromotedArgumentParameters;

			internal void Reset()
			{
				Program = null;
				CacheEntry = null;
				Entry = JournalEntry.Unknown;
				FormatedScriptForDairy = null;
				NeedsToSolveParameters = false;
				NeedsToSolveReferences = false;
				Promotion = null;
				PromotedArgumentParameters = null;
			}
		}

		// Reusable instance for sync PerformCmd: it is safe because PerformCmd runs under the write lock (a single thread at a time).
		// Does not apply to PerformCmdAsync, which has its own local variables.
		private readonly CommandPrepared _reusableCommandPrepared = new CommandPrepared();

		// Compilation and cache model for Commands (PerformCmd):
		//
		// A script behaves as F(x1,x2,...,xn). The first time it is parsed, all references are resolved
		// (SolveReferences: LValues, RValues, global variables, parameters) and the compiled Program is cached.
		// On subsequent invocations, the compiled lambda F is reused; only the parameters are rebound
		// (SolveParameters) with the new values instance.
		//
		// Cache: actionCommands (by script string).
		// - Without user parameters: interpreted mode, NOT cached, persisted as a Script in the journal.
		// - With user parameters: compiled mode, IS cached with an ActionId, persisted as an Action in the journal.
		//
		// PerformCmd is sequential (write lock), so the same cached Program can be reused
		// without concurrency risk. This differs from PerformQry/PerformChk/PerformEmit, which use a read lock
		// and can run in parallel (see documentation in those methods).
		private void PrepareCommandProgram(string script, Parameters parameters, CommandPrepared commandPrepared)
		{
			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");
			if (parameters == null) throw new ArgumentNullException(nameof(parameters));
			if (commandPrepared == null) throw new ArgumentNullException(nameof(commandPrepared));

			commandPrepared.Reset();

			if (!actionCommands.TryGetValue(script, out commandPrepared.CacheEntry))
			{
				// CACHE MISS: first time this script is seen.
				Parser parser = ParsersPool.Rent();

				parser.SetSource(script);
				commandPrepared.Program = parser.Parse(isQuery: false, isCheck: false);

				ParsersPool.Return(parser);

				commandPrepared.Program.SetContextInfo();

				// HasAnyUserParameter excludes the system Now: the classification only
				// depends on the user parameters (see Parameters.HasAnyUserParameter).
				if (parameters == EMPTY_PARAMETERS || !parameters.HasAnyUserParameter())
				{
					// Sin parameters de user: would be a V1 Script unless the
					// candidate hash matches an already-promoted Action — in
					// which case B.3.4 reroutes the incoming script as an
					// invocation of that Action, so the journal grows with
					// a single compact Invocation row instead of a Script row.
					if (TryRouteScriptAsPromotedAction(parameters, commandPrepared))
					{
						// Routed: commandPrepared is now shaped as IsExistingAction
						// targeting the promoted Action; the original Script
						// Program is discarded.
					}
					else
					{
						commandPrepared.Entry = JournalEntry.IsScript;
						commandPrepared.Program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
						commandPrepared.FormatedScriptForDairy = commandPrepared.Program.ConvertToString(this.DatabaseType);
						// B.3.1 + B.3.3: observe this Script's promotion candidate
						// for the automatic-promotion counter. If the observation
						// fires promotion (counter at 0, not yet promoted), the
						// returned PromotionResult tells ExecuteCommandWithWriteLock
						// to journal Define + Invocation instead of a Script row.
						commandPrepared.Promotion = ObservePromotionCandidate(commandPrepared.Program, commandPrepared.FormatedScriptForDairy);
					}
				}
				else
				{
					// With user parameters: compiled mode, cached with an ActionId.
					// Serialized as an Action (ActionId + arguments) in the journal.
					commandPrepared.Entry = JournalEntry.IsNewAction;
					var nextActionId = this.TakeAndIncrementActionId();
					commandPrepared.Program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
					commandPrepared.CacheEntry = actionCommands.Add(nextActionId, script, commandPrepared.Program);
					commandPrepared.FormatedScriptForDairy = commandPrepared.Program.ConvertToString(this.DatabaseType);
				}
				// On a cache miss, SolveReferences is always needed to resolve the program's full
				// structure (LValues, RValues, global variables, parameters).
				commandPrepared.NeedsToSolveReferences = !commandPrepared.Program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
			else
			{
				// CACHE HIT: the Program was already parsed, compiled and its references resolved.
				// Only the parameters need to be rebound with the new values (SolveParameters).
				commandPrepared.Entry = JournalEntry.IsExistingAction;
				commandPrepared.Program = (Program)commandPrepared.CacheEntry.Program;
				commandPrepared.NeedsToSolveParameters = !commandPrepared.Program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
		}

		// Executes the already-prepared program under the write lock.
		// Flow: LoadArguments -> SolveReferences/SolveParameters -> Perform -> persist to the journal.
		// writeNewEntry is false during rehydration (RecoveringState) so events are not re-persisted.
		private string ExecuteCommandWithWriteLock(CommandPrepared commandPrepared, Parameters parameters, DateTime now, string Ip, string User)
		{
			if (commandPrepared == null) throw new ArgumentNullException(nameof(commandPrepared));
			if (parameters == null) throw new ArgumentNullException(nameof(parameters));

			string result = null;
			bool executionError = false;
			// SuppressReactionJournaling (follower mode, Stage 2): lets the
			// script execute under the write-lock (TellStatement.Execute builds the
			// envelope and enqueues it) but does NOT write to the canonical journal, so the
			// 1-writer invariant is preserved. The PendingTells drain after
			// the lock release dispatches the envelope via Transport just like on
			// the primary. The gate only applies INSIDE a Reaction's .Do(...) Action
			// (InReactionAction == true) — direct user PerformCmds
			// (in production they never reach the follower because the gate is closed,
			// but tests can invoke them) journal normally. Cross-ref:
			// project_follower_materialize_roles.md.
			bool writeNewEntry = dairy != null && !symbolTable.RecoveringState
				&& !(SuppressReactionJournaling && InReactionAction);
			long nextEntryId = -1;

			// B.3.4: when the incoming Script was rerouted to a promoted
			// Action, the caller's `parameters` is typically EMPTY; the
			// actual argument values live on PromotedArgumentParameters
			// (extracted from the script's literals). Substitute it here
			// so LoadArguments / SolveParameters / journal-side ArgumentsAsString
			// all see the populated set.
			Parameters effectiveParameters = commandPrepared.PromotedArgumentParameters ?? parameters;

			commandPrepared.Program.LoadArguments(effectiveParameters);

			if (commandPrepared.NeedsToSolveReferences) commandPrepared.Program.SolveReferences(effectiveParameters, withStaticValidation: true);
			if (commandPrepared.NeedsToSolveParameters) commandPrepared.Program.SolveParameters(effectiveParameters);

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
					// B.3.3: a Script observation that fires automatic promotion
					// emits Define + Invocation atomically (same two-row dance
					// IsNewAction uses for an explicit V2 first invocation).
					// B.3.4: but when the materialization reused an existing
					// Action body (idempotent path), only the Invocation is
					// new — we do NOT take a Define entryId for that case.
					bool promotionNeedsDefine = commandPrepared.Promotion != null && commandPrepared.Promotion.RequiresDefineWrite;
					if (commandPrepared.Entry == JournalEntry.IsNewAction || promotionNeedsDefine)
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

				result = Perform(commandPrepared.Program, effectiveParameters);

				// Eval determinism: the dairy snapshot was taken in PrepareCommand
				// BEFORE Perform, when EvalStatement.forDairy was still null — that
				// is why it rendered the LITERAL form `Eval(<expr>);` (not deterministic
				// on replay because it re-evaluates an expression that depends on
				// runtime state). We re-render here, with the program already executed: each executed
				// Eval has its forDairy populated with the EVALUATED assignment (e.g.
				// `available = 5;`), so the journal stays deterministic. An Eval that
				// did not execute (conditional/branch not taken) keeps forDairy==null and
				// still renders the literal, which is correct: replay
				// re-evaluates it in its own context. Gated on HasEval so as not to pay the
				// double ConvertToString on scripts without Eval.
				if (commandPrepared.Program.HasEval)
				{
					commandPrepared.Program.InvalidateDairyRenderCache();
					commandPrepared.FormatedScriptForDairy = commandPrepared.Program.ConvertToString(this.DatabaseType);
				}

				executionError = false;
				if (writeNewEntry)
				{
					string argumentValues;
					switch (commandPrepared.Entry)
					{
						case JournalEntry.IsScript:
							if (commandPrepared.Promotion != null && commandPrepared.Promotion.RequiresDefineWrite)
							{
								// B.3.3: automatic Script → Action promotion. Emit
								// Define + first Invocation atomically; from here
								// onwards this candidate routes via actionCommands.
								dairy.WriteDefineWithFirstInvocation(
									commandPrepared.Promotion.ActionId,
									commandPrepared.Promotion.DefineText,
									defineEntryIdForCutover,
									nextEntryId,
									now,
									commandPrepared.Promotion.ArgumentsString,
									commandPrepared.Program.LastExposeData);
							}
							else if (commandPrepared.Promotion != null)
							{
								// B.3.4: idempotent promotion — the Action body is
								// already journalled (e.g. from a previous run);
								// emit only the Invocation row.
								dairy.WriteInvocationEntry(
									commandPrepared.Promotion.ActionId,
									nextEntryId,
									now,
									commandPrepared.Promotion.ArgumentsString,
									commandPrepared.Program.LastExposeData);
							}
							else if (!String.IsNullOrWhiteSpace(commandPrepared.FormatedScriptForDairy))
							{
								dairy.WriteScriptEntry(nextEntryId, commandPrepared.FormatedScriptForDairy, now, commandPrepared.Program.LastExposeData);
							}
							// B.2 ext: publish into the last-executed-script sliding
							// window. Followers that consume this entry immediately
							// afterwards (push-mode Reactions / Cue feeds) can reuse
							// the parsed Program via TryGetLastExecutedScript and
							// skip the parse. ActionEvents already benefit from
							// actionCommands LRU and are intentionally NOT cached here.
							// Note: even on a promoted write, the runtime effect
							// came from the Script's interpreted Program, so we
							// still publish that instance — followers consuming
							// the Invocation row receive the same Program identity
							// they would have for a regular Script row.
							PublishLastExecutedScript(nextEntryId, commandPrepared.Program);
							break;

						case JournalEntry.IsExistingAction:
							// B.3.4: when the incoming Script was rerouted to a
							// promoted Action, use PromotedArgumentParameters
							// for the journal payload — the caller's
							// `parameters` is EMPTY in that case.
							argumentValues = (commandPrepared.PromotedArgumentParameters ?? parameters).ArgumentsAsString(this.DatabaseType);
							var actionId = commandPrepared.CacheEntry.Id;
							dairy.WriteInvocationEntry(actionId, nextEntryId, now, argumentValues, commandPrepared.Program.LastExposeData);
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
								now,
								argumentValues,
								commandPrepared.Program.LastExposeData);
							break;
						default:
							throw new LanguageException($"The dairy entry is not valid: {commandPrepared.Entry}");
					}
				}
			}
			catch (Exception executionEx)
			{
				if (executionError && writeNewEntry)
				{
					commandPrepared.FormatedScriptForDairy = commandPrepared.Program.ConvertToString(dairy.DatabaseType);
					if (!String.IsNullOrWhiteSpace(commandPrepared.FormatedScriptForDairy))
					{
						// The script is persisted WHOLE in the journal (without an error tag). The
						// failure information lives in the IPuppeteerLogger.Error sink and
						// can reach the host via custom logger injection (Serilog/MEL/
						// NLog/bridge-to-email). This way the journal is a faithful record of the
						// attempted commands, not a transport channel for error metadata.
						Logger.Error(
							$"Script execution failed at write-time. EntryId={nextEntryId}, OccurredAt={now:O}, Script:\n{commandPrepared.FormatedScriptForDairy}",
							executionEx);
						dairy.WriteScriptEntry(nextEntryId, commandPrepared.FormatedScriptForDairy, now, null);
					}
				}
				if (executionError) throw;
				// executionError == false: the IN-MEMORY execution already succeeded and the failure
				// occurred while journaling the command (e.g. serializing arguments/signature).
				// Memory advanced but the diary did not -> on replay the command disappears (silent
				// loss). A command executed-but-not-persisted is corrupt state: it is
				// propagated, never swallowed. (There is no useful backup here: the script already executed and
				// the fallback WriteScriptEntry only applies to EXECUTION failures.)
				commandLineError = commandPrepared.Program.GetCommandErrorLine();
				throw;
			}

			// B.1c: free the resolved AST of the compiled Program now that it
			// executed and journaled. Restricted to Action entries (the cached,
			// unbounded actionCommands Programs) — NOT IsScript. IsScript
			// Programs are published to the lastExecutedScript window and
			// consumed there for pattern matching (PreparePatternMatching walks
			// their statements), so they must keep the AST. That window is
			// bounded (T=32) so retaining its ASTs is negligible; the unbounded
			// cache we trim is actionCommands. Matching of Actions re-parses
			// entry.Script into a separate per-Reaction copy, so releasing the
			// Action's own AST is invisible to it. Self-gated on compiled +
			// executable inside ReleaseStatements.
			if (commandPrepared.Entry == JournalEntry.IsNewAction || commandPrepared.Entry == JournalEntry.IsExistingAction)
			{
				commandPrepared.Program.ReleaseStatements(this.DatabaseType);
			}

			return result;
		}

		internal string PerformCmd(string script, Parameters parameters)
		{
			return PerformCmd(script, parameters, DateTime.Now);
		}

		// PerformCmd (sync): executes a source against the actor and persists to the journal.
		// Concurrency: WRITE LOCK — a single thread at a time. Uses _reusableCommandPrepared (shared instance).
		// Cache: actionCommands — Programs with parameters are compiled and cached with an ActionId.
		// Journal: writes the event (Script or Action) to the diary if not in rehydration.
		internal string PerformCmd(string script, Parameters parameters, DateTime now)
		{
			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");
			if (parameters == null) throw new LanguageException("Can not send null parameters");

			string result = null;

			try
			{
				commandLineError = "";
				scriptEnEjecucion = script;

				string Ip = "";
				string User = "";

				rwLock.EnterWriteLock();

				try
				{
					PrepareCommandProgram(script, parameters, _reusableCommandPrepared);

					// Now is a SYSTEM parameter injected by the framework (V1 and V2) with
					// the command's value. It is per-call (thread-safe) and visible to pattern
					// matching as id.IsParameter, but it is EXCLUDED from the Action signature
					// and from the argument blob (Parameters.IsSystemNow). It is injected AFTER
					// PrepareCommandProgram so the IsScript/IsNewAction decision observes
					// only the user parameters, not the system Now.
					// Lever 1: it is only injected if the program references @Now (ReferencesNow);
					// commands that do not use the clock pay neither box nor set. OccurredAt comes from the
					// local 'now', not from the parameter. Lever 3: typed SetNow (no lookup+ImplicitCast).
					if (parameters != EMPTY_PARAMETERS && _reusableCommandPrepared.Program.ReferencesNow)
					{
						parameters.SetNow(now);
					}

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
						// Shadow isolation (S1): cross-actor Tells are dropped — a
						// shadow produces zero external effect. The envelope was
						// built (and journaled in the shadow's own storage) but is
						// never delivered to the real target actor.
						if (IsShadow) continue;
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

			// Playbill final refactor: timeStamp is no longer read from parameters["Now"] (V2 may not
			// declare it). The `now` arriving as an argument is the authoritative source.
			timeStamp = now;

			return result;
		}

		// Playbill final refactor: V1 entry path async. ip/user are ignored. The Now injection
		// (V1 backward compat) is done by the PerformCmdAsync(script, parameters) overload
		// after the IsScript/IsNewAction branch so as not to alter the persistence decision.
		internal async Task<string> PerformCmdAsync(string script, string ip, string user)
		{
			Parameters parameters = ParametersPool.Rent();

			var result = await PerformCmdAsync(script, parameters);

			ParametersPool.Return(parameters);

			return result;
		}

		// PerformCmdAsync: async version of PerformCmd.
		// Concurrency: uses _block (SemaphoreSlim) instead of rwLock.EnterWriteLock, but still guarantees
		// mutual exclusion. Uses local variables (not _reusableCommandPrepared) because
		// preparation and execution may be on different async continuations.
		// Cache and journal: same logic as PrepareCommandProgram/ExecuteCommandWithWriteLock but inline.
		// Persistence to the journal (dairy.Write*Async) is done OUTSIDE the write lock so as not to block
		// other readers during I/O.
		internal async Task<string> PerformCmdAsync(string script, Parameters parameters)
		{
			ArgumentNullException.ThrowIfNull(script);
			ArgumentNullException.ThrowIfNull(parameters);

			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");

			string result = null;
			JournalEntry entry = JournalEntry.Unknown;
			string formatedScriptForDairy = null;
			Program program;
			string Ip;
			string User;
			DateTime now = DateTime.MinValue;

			CommandCacheEntry cacheDeComandosEntry = null;

			bool needsToSolveParameters = false;
			bool needsToSolveReferences = false;
			bool executionError = false;
			// B.3.3: when an IsScript observation fires automatic promotion,
			// this carries the materialized PromotionResult so the journal
			// write below emits Define + Invocation instead of a Script row.
			PromotionResult promotion = null;
			// B.3.4: when an incoming would-be Script was rerouted to an
			// already-promoted Action, this carries the Parameters populated
			// with the values extracted from the script's literals. Used in
			// place of the caller's `parameters` for LoadArguments and for
			// the Invocation row's ArgumentsAsString.
			Parameters promotedArgumentParameters = null;
			// Same gate as the sync path (ExecuteCommandWithWriteLock): it only
			// suppresses journaling when we are INSIDE a Reaction's .Do(...) Action
			// (InReactionAction == true). Other direct user PerformCmds
			// journal normally even with the flag on.
			bool writeNewEntry = dairy != null && !symbolTable.RecoveringState
				&& !(SuppressReactionJournaling && InReactionAction);
			long nextEntryId = -1;
			Exception executionException = null;

			await _block.WaitAsync();

			try
			{
				if (!actionCommands.TryGetValue(script, out cacheDeComandosEntry))
				{
					// CACHE MISS: parse, compile and (optionally) cache.
					// Same logic as PrepareCommandProgram but with local variables.
					Parser parser = ParsersPool.Rent();

					parser.SetSource(script);
					program = parser.Parse(isQuery: false, isCheck: false);

					ParsersPool.Return(parser);

					program.SetContextInfo();

					// HasAnyUserParameter excludes the system Now (see the sync path).
					if (parameters == EMPTY_PARAMETERS || !parameters.HasAnyUserParameter())
					{
						// B.3.4: before classifying as IsScript, check whether
						// this script's candidate hash matches an already-
						// promoted Action. If so, reshape the local state to
						// look like a regular IsExistingAction invocation.
						int candidateHash = program.PromotionCandidateHash;
						if (promotionCandidateToActionId.TryGetValue(candidateHash, out int routedActionId)
							&& actionCommands.TryGetValue(routedActionId, out CommandCacheEntry routedEntry))
						{
							string canonicalForRouting = program.ConvertToString(this.DatabaseType);
							var routedExtraction = PromotionCandidate.LiteralExtractor.Extract(canonicalForRouting);
							string declarationText = routedEntry.Program.Parameters != null
								? routedEntry.Program.Parameters.ParametersAsString()
								: routedExtraction.ParametersDeclaration;
							Parameters routedArgs = string.IsNullOrWhiteSpace(declarationText)
								? new Parameters()
								: new Parameters(declarationText);
							if (!string.IsNullOrWhiteSpace(routedExtraction.ArgumentsString))
							{
								routedArgs.LoadArguments(routedExtraction.ArgumentsString);
							}
							entry = JournalEntry.IsExistingAction;
							program = routedEntry.Program;
							cacheDeComandosEntry = routedEntry;
							promotedArgumentParameters = routedArgs;
							formatedScriptForDairy = canonicalForRouting;
							needsToSolveReferences = false;
							needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
						}
						else
						{
							entry = JournalEntry.IsScript;
							program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
							formatedScriptForDairy = program.ConvertToString(this.DatabaseType);
							// B.3.1 + B.3.3: observe the promotion candidate
							// (parallel to the sync branch).
							promotion = ObservePromotionCandidate(program, formatedScriptForDairy);
							needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
						}
					}
					else
					{
						entry = JournalEntry.IsNewAction;
						var nextActionId = this.TakeAndIncrementActionId();
						program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
						cacheDeComandosEntry = actionCommands.Add(nextActionId, script, program);
						formatedScriptForDairy = program.ConvertToString(this.DatabaseType);
						needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
					}
				}
				else
				{
					// CACHE HIT: Program already compiled, references already resolved.
					// Only rebind parameters (SolveParameters) if interpreted mode.
					entry = JournalEntry.IsExistingAction;
					program = cacheDeComandosEntry.Program;
					needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
					needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}

				commandLineError = "";

				Ip = "";
				User = "";

				now = DateTime.Now;

				// Now is a SYSTEM parameter injected by the framework (V1 and V2),
				// excluded from the journal signature/args (Parameters.IsSystemNow). The
				// IsScript/IsNewAction decision was already made above over the user
				// parameters, so injecting Now here does not alter it.
				// Lever 1: only if the program references @Now. Lever 3: typed SetNow.
				if (parameters != EMPTY_PARAMETERS && program.ReferencesNow)
				{
					parameters.SetNow(now);
				}

				rwLock.EnterWriteLock();

				// B.3.4: when the incoming Script was rerouted to a promoted
				// Action, substitute the populated Parameters for the
				// caller's (empty) `parameters` — parallel to the sync path.
				Parameters effectiveParameters = promotedArgumentParameters ?? parameters;

				program.LoadArguments(effectiveParameters);

				if (needsToSolveReferences) program.SolveReferences(effectiveParameters, withStaticValidation: true);
				if (needsToSolveParameters) program.SolveParameters(effectiveParameters);

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
						// B.3.3: a Script observation that fires promotion writes
						// Define + Invocation (mirrors the sync path's behaviour).
						// B.3.4: but on the idempotent reuse path no Define is
						// needed — only the Invocation entry id.
						bool promotionNeedsDefine = promotion != null && promotion.RequiresDefineWrite;
						if (entry == JournalEntry.IsNewAction || promotionNeedsDefine)
						{
							defineEntryIdForCutover = this.TakeAndIncrementEntryId();
						}
						nextEntryId = this.TakeAndIncrementEntryId();
					}

					// Plan 6 (A): propagate entry id for ack-side elision (see
					// matching comment in ExecuteCommandWithWriteLock).
					program.EntryId = nextEntryId;

					result = Perform(program, effectiveParameters);

					// Eval determinism: re-render of the dairy snapshot post-Perform
					// (mirror of the sync path in ExecuteCommandWithWriteLock). The snapshot
					// was taken before Perform with EvalStatement.forDairy==null, that is why
					// it emitted the non-deterministic literal `Eval(<expr>);`. Now executed,
					// each executed Eval journals its EVALUATED form.
					if (program.HasEval)
					{
						program.InvalidateDairyRenderCache();
						formatedScriptForDairy = program.ConvertToString(this.DatabaseType);
					}
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
						// The whole script goes to the journal; the error travels via IPuppeteerLogger.Error
						// with the full context (entryId, occurredAt, script, exception).
						// See the equivalent block in ExecuteCommandWithWriteLock for rationale.
						Logger.Error(
							$"Script execution failed at write-time (async path). EntryId={nextEntryId}, OccurredAt={now:O}, Script:\n{formatedScriptForDairy}",
							executionException);
					}
					string argumentValues;
					switch (entry)
					{
						case JournalEntry.IsScript:
							if (promotion != null && promotion.RequiresDefineWrite)
							{
								// B.3.3: automatic Script → Action promotion (async
								// path mirror of the sync branch in
								// ExecuteCommandWithWriteLock).
								await dairy.WriteDefineWithFirstInvocationAsync(
									promotion.ActionId,
									promotion.DefineText,
									defineEntryIdForCutover,
									nextEntryId,
									now,
									promotion.ArgumentsString,
									program.LastExposeData);
							}
							else if (promotion != null)
							{
								// B.3.4: idempotent promotion — Action body already
								// on disk; emit only the Invocation row.
								await dairy.WriteInvocationEntryAsync(
									promotion.ActionId,
									nextEntryId,
									now,
									promotion.ArgumentsString,
									program.LastExposeData);
							}
							else if (!String.IsNullOrWhiteSpace(formatedScriptForDairy))
							{
								await dairy.WriteScriptEntryAsync(nextEntryId, formatedScriptForDairy, now, program.LastExposeData);
							}
							// B.2 ext: publish into the last-executed-script sliding
							// window (parallel to the sync ExecuteCommandWithWriteLock
							// branch).
							PublishLastExecutedScript(nextEntryId, program);
							break;

						case JournalEntry.IsExistingAction:
							// B.3.4: use promotedArgumentParameters when this is
							// a Script-routed-to-Action invocation (parallel to
							// the sync path).
							argumentValues = (promotedArgumentParameters ?? parameters).ArgumentsAsString(this.DatabaseType);
							var actionId = cacheDeComandosEntry.Id;
							await dairy.WriteInvocationEntryAsync(actionId, nextEntryId, now, argumentValues, program.LastExposeData);
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
								now,
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
				// sentence "tell sent" is factual; delivery is the transport's problem.
				if (!executionError && symbolTable.PendingTellCount > 0)
				{
					ITransport transportSnapshot = Transport;
					while (symbolTable.TryDequeuePendingTell(out TellEnvelope envelope))
					{
						// Shadow isolation (S1): cross-actor Tells are dropped — see
						// the equivalent comment in the sync PerformCmd drain.
						if (IsShadow) continue;
						if (transportSnapshot != null)
						{
							await transportSnapshot.SendAsync(envelope);
						}
					}
				}

				if (executionError) throw executionException;

				// B.1c: free the resolved AST of the compiled Action post-execute
				// + journal (async mirror of ExecuteCommandWithWriteLock). Action
				// entries only — IsScript Programs feed the lastExecutedScript
				// matching window and must keep their AST. _block serializes
				// writers, so the mutation is race-free.
				if (entry == JournalEntry.IsNewAction || entry == JournalEntry.IsExistingAction)
				{
					program.ReleaseStatements(this.DatabaseType);
				}
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

			// Now is no longer guaranteed in parameters (it may be EMPTY_PARAMETERS). The `now`
			// captured at the start of the command is the authoritative source of the timestamp.
			timeStamp = now;

			return result;
		}


		// PerformQry: executes a read-only query against the actor. Does not persist to the journal.
		// Concurrency: READ LOCK — multiple queries can run in parallel with each other
		// and in parallel with other read locks (PerformChk, PerformEmit).
		// SetReadOnlyMode(true) protects the SymbolTable against accidental writes.
		//
		// Cache: QuerysEnCache (ConcurrentDictionary) — shared with PerformChk and PerformEmit.
		// - With user parameters: compiled mode, IS cached.
		// - Without user parameters: interpreted mode, NOT cached.
		//
		// Note on needsToSolveReferences on cache hit:
		// In production (CompiledModePolicy=Automatic) a cached Program always has
		// IsCompiledMode=true, so needsToSolveReferences evaluates to false.
		// The original SolveReferences (cache miss) already resolved the program's full structure;
		// on cache hit only parameters are rebound via SolveParameters.
		// needsToSolveReferences is true on cache hit ONLY with AlwaysInterpreted (unit tests).
		internal string PerformQry(string script, Parameters parameters)
		{
			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");
			if (parameters == null) throw new LanguageException("Can not send null parameters");

			bool needsToSolveParameters = false;
			bool needsToSolveReferences = false;
			string result = null;

			if (!QuerysEnCache.TryGetValue(script, out Program program))
			{
				// CACHE MISS: parse with isQuery:true (blocks expose and global variable declaration).
				Parser parser = ParsersPool.Rent();

				parser.SetSource(script);
				program = parser.Parse(isQuery: true, isCheck: false);

				ParsersPool.Return(parser);

				program.SetContextInfo();

				if (parameters == EMPTY_PARAMETERS || parameters.HasAnyParameter())
				{
					program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
					QuerysEnCache.TryAdd(script, program);
				}
				else
				{
					program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
				}
				// Cache miss: resolve the full structure (LValues, RValues, globals, parameters)
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
			else
			{
				// CACHE HIT: Program already compiled, references already resolved.
				// In Automatic: IsCompiledMode=true -> both false -> nothing is done (the compiled lambda is reused directly).
				// In AlwaysInterpreted (tests): both true -> references and parameters are re-resolved.
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}

			// Now is a per-call SYSTEM parameter (V1 and V2): each query carries its own
			// Now in its rented Parameters, so it is thread-safe under the read lock
			// (not shared global state). Excluded from the journal signature/args — queries
			// are not persisted, but the Parameters symmetry keeps it coherent.
			DateTime nowForQry = DateTime.Now;
			// Lever 1: only if the query references @Now. Lever 3: typed SetNow.
			if (parameters != EMPTY_PARAMETERS && program.ReferencesNow)
			{
				parameters.SetNow(nowForQry);
			}

			program.LoadArguments(parameters);
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

			// Playbill final refactor: timeStamp is no longer read from parameters["Now"] (V2 may not
			// declare it). The local nowForQry is the authoritative source.
			timeStamp = nowForQry;

			return result;
		}

		// PerformEmit: action of a Cue Reaction. Executes a read-only script against the actor
		// to produce external side effects (e.g. sending data over Kafka).
		// Does NOT persist to the journal — the Reaction's checkpoint tracks the execution.
		// Concurrency: READ LOCK — same semantics as PerformQry.
		// SetReadOnlyMode(true) protects the SymbolTable against accidental writes.
		// Parser: isQuery:true — blocks expose (which would persist to the journal) and global variable declaration.
		// Cache: QuerysEnCache — compiled and cached with the same rules as PerformQry.
		// Returns void (not string) because the result is an external side effect, not a return value.
		internal void PerformEmit(string script, Parameters parameters)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(script);
			ArgumentNullException.ThrowIfNull(parameters);
			if (script.Length > Lexer.MAX_LEXEME_SIZE) throw new LanguageException("Script exceeds the maximun length");

			bool needsToSolveParameters = false;
			bool needsToSolveReferences = false;

			if (!QuerysEnCache.TryGetValue(script, out Program program))
			{
				// CACHE MISS: parse with isQuery:true (blocks expose and global variable declaration).
				Parser parser = ParsersPool.Rent();

				parser.SetSource(script);
				program = parser.Parse(isQuery: true, isCheck: false);

				ParsersPool.Return(parser);

				program.SetContextInfo();

				if (parameters == EMPTY_PARAMETERS || parameters.HasAnyParameter())
				{
					program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
					QuerysEnCache.TryAdd(script, program);
				}
				else
				{
					program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
				}
				// Cache miss: resolve the full structure (LValues, RValues, globals, parameters)
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
			else
			{
				// CACHE HIT: see documentation in PerformQry about needsToSolveReferences on cache hit.
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}

			// Now is a per-call SYSTEM parameter (V1 and V2), thread-safe under the read
			// lock (it lives in this call's Parameters, not in global state). Excluded
			// from the journal signature/args via Parameters.IsSystemNow.
			DateTime nowForEmit = DateTime.Now;
			// Lever 1: only if the emit references @Now. Lever 3: typed SetNow.
			if (parameters != EMPTY_PARAMETERS && program.ReferencesNow)
			{
				parameters.SetNow(nowForEmit);
			}

			program.LoadArguments(parameters);
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

			// Playbill final refactor: timeStamp is no longer read from parameters["Now"] (V2 may not
			// declare it).
			timeStamp = nowForEmit;
		}

		// PerformChk: executes a read-only check against the actor. Does not persist to the journal.
		// Returns null/empty if the check passes, or an error message if it fails.
		// Concurrency: READ LOCK — same semantics as PerformQry.
		// Parser: isCheck:true — produces a Program that executes via ExecuteCheck() instead of Perform().
		// Cache: QuerysEnCache — same cache as PerformQry and PerformEmit.
		//
		// Difference from PerformQry on cache hit:
		// PerformChk only assigns needsToSolveParameters (not needsToSolveReferences).
		// In production (Automatic) this is equivalent: both evaluate to false on cache hit
		// because IsCompiledMode=true. The difference is only observable with AlwaysInterpreted (tests),
		// where PerformQry re-resolves references on each invocation and PerformChk does not.
		// This is not a bug: PerformChk is typically invoked once per PerformCheckThenCommand,
		// and the Program structure is already resolved from the original cache miss.
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
				// CACHE MISS: parse with isCheck:true (produces a Program for ExecuteCheck).
				Parser parser = ParsersPool.Rent();
				parser.SetSource(script);
				program = parser.Parse(isQuery: false, isCheck: true);
				ParsersPool.Return(parser);

				program.SetContextInfo();

				if (parameters == EMPTY_PARAMETERS || parameters.HasAnyParameter())
				{
					program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
					QuerysEnCache.TryAdd(script, program);
				}
				else
				{
					program.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
				}
				// Cache miss: resolve the full structure (LValues, RValues, globals, parameters)
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
			else
			{
				// CACHE HIT: only rebind parameters. See the method comment about the difference from PerformQry.
				needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}

			// Now is a per-call SYSTEM parameter (V1 and V2), thread-safe under the read
			// lock (it lives in this call's Parameters, not in global state). Excluded
			// from the journal signature/args via Parameters.IsSystemNow.
			DateTime nowForChk = DateTime.Now;
			// Lever 1: only if the check references @Now. Lever 3: typed SetNow.
			if (parameters != EMPTY_PARAMETERS && program.ReferencesNow)
			{
				parameters.SetNow(nowForChk);
			}

			program.LoadArguments(parameters);
			if (needsToSolveReferences) program.SolveReferences(parameters, withStaticValidation: true);
			if (needsToSolveParameters) program.SolveParameters(parameters);

			rwLock.EnterReadLock();

			symbolTable.SetReadOnlyMode(true);

			try
			{
				result = program.ExecuteCheck();

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

			// Playbill final refactor: timeStamp is no longer read from parameters["Now"] (V2 may not
			// declare it).
			timeStamp = nowForChk;

			return result;
		}

		// PerformCheckThenCmd: executes a check without blocking (via PerformChk with a read lock),
		// and if it passes, takes the write lock and re-executes the check + source atomically.
		//
		// Two-phase flow:
		// 1. PerformChk(scriptForChk) — read lock, no blocking. If it fails, returns immediately.
		// 2. Under the write lock: re-executes the check (ExecuteCheck) to verify it is still valid
		//    (another writer may have changed the state between phase 1 and phase 2).
		//    If the check passes, executes the source (ExecuteCommandWithWriteLock) which persists to the journal.
		//
		// Concurrency: WRITE LOCK for phase 2. Uses _reusableCommandPrepared.
		// Cache: scriptForCmd in actionCommands (via PrepareCommandProgram), scriptForChk in QuerysEnCache.
		//
		// Note on the check cache hit (scriptForChk):
		// It only assigns needsToSolveParametersChk (not needsToSolveReferencesChk).
		// Same logic as PerformChk — see documentation in that method.
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

			// Phase 1: check without blocking (read lock). If it fails, the write lock is not taken.
			var chkResult = PerformChk(scriptForChk, parameters);
			if (!String.IsNullOrEmpty(chkResult))
			{
				return chkResult;
			}

			// Phase 2: re-check + command under the write lock.
			string result = null;
			Program programChk;
			bool needsToSolveParametersChk = false;
			bool needsToSolveReferencesChk = false;

			try
			{
				commandLineError = "";
				scriptEnEjecucion = scriptForCmd;

				// Phase 4.5 Playbill refactor: ip/user no longer travel as script parameters.
				string Ip = "";
				string User = "";

				PrepareCommandProgram(scriptForCmd, parameters, _reusableCommandPrepared);

				if (!QuerysEnCache.TryGetValue(scriptForChk, out programChk))
				{
					// CACHE MISS of the check script: parse with isCheck:true.
					Parser parserChk = ParsersPool.Rent();
					parserChk.SetSource(scriptForChk);
					programChk = parserChk.Parse(isQuery: false, isCheck: true);
					ParsersPool.Return(parserChk);

					programChk.SetContextInfo();

					if (parameters == EMPTY_PARAMETERS || parameters.HasAnyParameter())
					{
						programChk.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
						QuerysEnCache.TryAdd(scriptForChk, programChk);
					}
					else
					{
						programChk.AdjustCompilationMode(useInterpretedMode: true, this.actor.CompiledModePolicy);
					}
					// Cache miss: resolve the full structure
					needsToSolveReferencesChk = !programChk.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}
				else
				{
					// CACHE HIT: only rebind parameters. See documentation in PerformChk.
					needsToSolveParametersChk = !programChk.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}

				// Now is a SYSTEM parameter injected by the framework (V1 and V2),
				// excluded from the journal signature/args (Parameters.IsSystemNow). It is injected
				// AFTER PrepareCommandProgram so as not to alter the IsScript/
				// IsNewAction decision (which must observe only the user parameters).
				// Lever 1: only if the command OR the check reference @Now (the injection was
				// moved here, after resolving programChk, to be able to consult both programs);
				// the same Parameters feeds the re-check (ExecuteCheck) and the command, so
				// a single set suffices. Lever 3: typed SetNow.
				if (parameters != EMPTY_PARAMETERS &&
					(_reusableCommandPrepared.Program.ReferencesNow || programChk.ReferencesNow))
				{
					parameters.SetNow(now);
				}

				rwLock.EnterWriteLock();

				try
				{
					programChk.LoadArguments(parameters);
					if (needsToSolveReferencesChk) programChk.SolveReferences(parameters, withStaticValidation: true);
					if (needsToSolveParametersChk) programChk.SolveParameters(parameters);

					symbolTable.SetReadOnlyMode(true);

					try
					{
						// Re-check under the write lock: verify the state has not changed since phase 1
						chkResult = programChk.ExecuteCheck();
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

			// Playbill final refactor: timeStamp is no longer read from parameters["Now"] (V2 may not
			// declare it). The `now` arriving as an argument is the authoritative source.
			timeStamp = now;

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

		internal string ComandForDairy(String script, string ip, string user)
		{
			Parser parser = ParsersPool.Rent();
			try
			{
				parser.SetSource(script);
				Program program = parser.Parse(isQuery: false, isCheck: false);
				program.SolveReferences(program.Parameters, withStaticValidation: false);
				String forDairy = program.ConvertToString(this.DatabaseType);
				return forDairy;
			}
			finally
			{
				ParsersPool.Return(parser);
			}
		}

		internal string ComandForDairy(String script, Parameters parameters)
		{
			Parser parser = ParsersPool.Rent();
			try
			{
				parser.SetSource(script);
				Program program = parser.Parse(isQuery: false, isCheck: false);
				program.LoadArguments(parameters);
				program.SolveReferences(parameters, withStaticValidation: true);
				string forDairy = program.ConvertToString(this.DatabaseType);
				return forDairy;
			}
			finally
			{
				ParsersPool.Return(parser);
			}
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

		// Take control and run the last commands if there are any
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
					//while (itsFollowerRunning) && lastIdAfterRecoveredState == the previous lastIdAfterRecoveredState
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

					// Now is a SYSTEM parameter excluded from the journal; it is re-injected from
					// the journaled OccurredAt for Script (V1) and Action (V2), before
					// SolveReferences/Perform (synchronous loop of the red-black replay).
					program.Parameters["Now", typeof(DateTime)] = eventData.OccurredAt;

					if (!actionCommands.ContainsAction(program.Script))
						program.SolveReferences(program.Parameters, withStaticValidation: true);

					try
					{
						Perform(program, program.Parameters);
						// B.1c: free the resolved AST of compiled cached Actions
						// during red-black replay (ActionEventData only; mirror of
						// the primary rehydration path).
						if (eventData is ActionEventData)
							program.ReleaseStatements(this.DatabaseType);
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

		// Stage 4: Single-flight fail-fast. Policy "1 and only 1 Distill at a time";
		// the second concurrent caller gets a LanguageException, it is neither queued nor coalesced.
		//
		// Reason: coalescing only made sense if reactions could trigger Distill
		// automatically (the planned metadata.Distill, discarded in Stage 4 — a
		// developer could put it on a frequent pattern without understanding the cost). Without
		// an auto-trigger, the only source of Distill is operational/human (cron, admin,
		// manual command). In that context, fail-fast is honest: two operators
		// invoking it simultaneously immediately understand one is in progress, not that
		// "it takes a long time silently".
		private readonly SemaphoreSlim distillRunSem = new SemaphoreSlim(1, 1);

		// Counter exposed for tests: increments on each real execution of
		// dairy.Distill. Useful to verify that calls throwing LanguageException
		// do not increment.
		internal long DistillRunCount;

		// Test seam: hook that runs inside the runner, after taking rwLock but
		// before calling dairy.Distill. Tests use it to stall the runner and exercise
		// the concurrent behaviour. Production never sets it.
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

		// B.2 ext: sliding window of the most recently executed script entries.
		// Captured at the tail of ExecuteCommandWithWriteLock / PerformCmdAsync
		// after the journal write succeeds, so a Reaction whose EntryId is in
		// the window can reuse the already-parsed-and-resolved Program instead
		// of re-parsing. Scope: V1 JournalEntry.IsScript (no parameters) — these
		// are the entries whose Programs are NOT in actionCommands; ActionEvents
		// already benefit from actionCommands.cacheDeCmdsPorId in O(1).
		//
		// Window size > 1 is required because multiple Reactions consume the
		// same EntryId at slightly different rates: a single-slot cache would
		// be overwritten by the next live write before the slower follower had
		// a chance to read it. The window is sized as a power of two so the
		// cursor → slot mapping is a bitwise AND. T=32 is a default trade-off
		// (small fixed memory, generous enough for typical concurrent fan-out).
		//
		// Concurrency: a monotonically-increasing cursor (Interlocked.Increment)
		// picks the slot for each publish; slots are written/read with Volatile
		// to ensure the Program reference and EntryId are seen consistently by
		// followers. Stale slots are tolerated: TryGetLastExecutedScript scans
		// the whole window and only returns on an EntryId match. Lookups are
		// O(WindowSize); for T=32 that is well under a single parse cost.
		internal sealed class LastExecutedScriptEntry
		{
			internal readonly long EntryId;
			internal readonly Program Program;
			internal LastExecutedScriptEntry(long entryId, Program program)
			{
				ArgumentNullException.ThrowIfNull(program);
				EntryId = entryId;
				Program = program;
			}
		}

		private const int LastExecutedScriptWindowSize = 32;
		private const int LastExecutedScriptWindowMask = LastExecutedScriptWindowSize - 1;
		private readonly LastExecutedScriptEntry[] lastExecutedScriptWindow = new LastExecutedScriptEntry[LastExecutedScriptWindowSize];
		private int lastExecutedScriptCursor = -1;
		private long lastExecutedScriptHits;
		private long lastExecutedScriptMisses;

		private void PublishLastExecutedScript(long entryId, Program program)
		{
			ArgumentNullException.ThrowIfNull(program);

			int next = Interlocked.Increment(ref lastExecutedScriptCursor);
			int slot = next & LastExecutedScriptWindowMask;
			Volatile.Write(ref lastExecutedScriptWindow[slot], new LastExecutedScriptEntry(entryId, program));
		}

		internal Program TryGetLastExecutedScript(long entryId)
		{
			for (int i = 0; i < LastExecutedScriptWindowSize; i++)
			{
				LastExecutedScriptEntry snap = Volatile.Read(ref lastExecutedScriptWindow[i]);
				if (snap != null && snap.EntryId == entryId)
				{
					Interlocked.Increment(ref lastExecutedScriptHits);
					return snap.Program;
				}
			}
			Interlocked.Increment(ref lastExecutedScriptMisses);
			return null;
		}

		internal long LastExecutedScriptHits => Interlocked.Read(ref lastExecutedScriptHits);
		internal long LastExecutedScriptMisses => Interlocked.Read(ref lastExecutedScriptMisses);
		internal int LastExecutedScriptWindowCapacity => LastExecutedScriptWindowSize;

		// B.3.1: promotion-candidate tracking. Each Script (V1,
		// JournalEntry.IsScript path) parses to a Program whose AST has a
		// PromotionCandidateHash that ignores literal *values* but preserves
		// types and structure — scripts that differ only in their literal
		// arguments share the same hash. We maintain an in-memory countdown
		// per candidate hash: starts at the configured threshold (default
		// 10), decrements on each observation, and once it reaches zero the
		// candidate is marked as "ready to promote" (actual promotion is
		// wired in B.3.3+). The counter is rebuilt naturally by rehydration:
		// replay sees the same Scripts in order and decrements identically;
		// once the journal contains a Define for an already-promoted
		// candidate, B.3.3 will register the mapping in
		// promotionCandidateToActionId so subsequent invocations route as
		// Actions immediately.
		internal const int DEFAULT_PROMOTION_CANDIDATE_THRESHOLD = 10;
		private int promotionCandidateThreshold = DEFAULT_PROMOTION_CANDIDATE_THRESHOLD;
		private readonly Dictionary<int, int> promotionCandidateCountdown = new Dictionary<int, int>();
		private readonly Dictionary<int, int> promotionCandidateToActionId = new Dictionary<int, int>();
		private long promotionCandidateObservationsTotal;
		private long promotionCandidateReadyObservations;

		internal void SetPromotionCandidateThreshold(int n)
		{
			if (n < 1) throw new LanguageException($"Promotion candidate threshold must be >= 1, got {n}.");
			promotionCandidateThreshold = n;
		}

		internal int PromotionCandidateThreshold => promotionCandidateThreshold;
		internal long PromotionCandidateObservationsTotal => Interlocked.Read(ref promotionCandidateObservationsTotal);
		internal long PromotionCandidateReadyObservations => Interlocked.Read(ref promotionCandidateReadyObservations);
		internal int DistinctPromotionCandidateCount => promotionCandidateCountdown.Count;
		internal int ReadyPromotionCandidateCount
		{
			get
			{
				int n = 0;
				foreach (var kvp in promotionCandidateCountdown)
				{
					if (kvp.Value == 0) n++;
				}
				return n;
			}
		}

		// B.3.3: carries the materialized state needed to journal a Define +
		// first Invocation pair in place of the would-be Script entry.
		// Produced by ObservePromotionCandidate when an observation tips a
		// candidate from "ready" (counter at 0, not yet promoted) into
		// "promoted" — at that point the Action exists in actionCommands
		// and is indexed by promotionCandidateToActionId, but the journal
		// still needs the rows written.
		//
		// B.3.4: when MaterializePromotion's idempotency path fires (the
		// Action's body is already in actionCommands — e.g. because a
		// previous run wrote the Define and the current run is the first
		// live write of the same shape after restart) DefineText is null
		// and the writer emits only an Invocation row. The journal does not
		// duplicate the Define.
		internal sealed class PromotionResult
		{
			internal readonly int ActionId;
			internal readonly string DefineText;
			internal readonly string ArgumentsString;

			internal bool RequiresDefineWrite => !string.IsNullOrEmpty(DefineText);

			internal PromotionResult(int actionId, string defineText, string argumentsString)
			{
				ArgumentNullException.ThrowIfNull(argumentsString);
				ActionId = actionId;
				DefineText = defineText;
				ArgumentsString = argumentsString;
			}
		}

		// B.3.1 + B.3.3: hook invoked from PrepareCommandProgram after a fresh
		// Script parse (JournalEntry.IsScript). Updates the per-candidate
		// countdown; when the threshold has been crossed AND the candidate
		// has not already been promoted, materializes the equivalent Action
		// and returns a PromotionResult so the caller can switch its journal
		// write from a single Script row to an atomic Define + Invocation
		// pair. Returns null otherwise.
		//
		// Promotion is idempotent: once a candidate is in
		// promotionCandidateToActionId, subsequent observations only tick
		// the ready meter; B.3.4 will route those incoming scripts directly
		// to the promoted Action via the same index.
		private PromotionResult ObservePromotionCandidate(Program program, string canonicalScript)
		{
			ArgumentNullException.ThrowIfNull(program);
			ArgumentNullException.ThrowIfNull(canonicalScript);

			int hash = program.PromotionCandidateHash;
			Interlocked.Increment(ref promotionCandidateObservationsTotal);

			bool ready;
			if (promotionCandidateCountdown.TryGetValue(hash, out int remaining))
			{
				if (remaining == 0)
				{
					Interlocked.Increment(ref promotionCandidateReadyObservations);
					ready = true;
				}
				else
				{
					promotionCandidateCountdown[hash] = remaining - 1;
					ready = (remaining - 1 == 0 && promotionCandidateThreshold == 1);
				}
			}
			else
			{
				// First observation of this candidate. The threshold is the
				// total observation count needed before promotion, so we
				// initialize the countdown at threshold-1 (this observation
				// already counts as #1). When threshold == 1, initial == 0
				// and this very observation is "ready".
				int initial = promotionCandidateThreshold - 1;
				if (initial < 0) initial = 0;
				promotionCandidateCountdown[hash] = initial;
				ready = (initial == 0);
			}

			if (!ready) return null;
			if (promotionCandidateToActionId.ContainsKey(hash)) return null;

			return MaterializePromotion(hash, canonicalScript);
		}

		// B.3.4: route an incoming Script-shape directly as an invocation of
		// a previously-promoted Action. Looks up the parsed Program's
		// PromotionCandidateHash in the promotionCandidateToActionId index;
		// on hit, reshapes commandPrepared from "would-be IsScript" into
		// "IsExistingAction" targeting the promoted Action, populating
		// PromotedArgumentParameters with the values extracted from the
		// incoming script's literals. Returns true when routing happened;
		// false when the candidate has not been promoted yet (the caller
		// proceeds with the regular IsScript path and B.3.3 may decide to
		// fire promotion if the counter is ready).
		//
		// Effect on the journal: the row written for this PerformCmd is a
		// compact Invocation (actionId, arguments) instead of a Script row
		// with the full canonical text. This is the payoff of the whole
		// promotion mechanism — repeated endpoints stop bloating the
		// journal once their shape has been characterized as recurrent.
		private bool TryRouteScriptAsPromotedAction(Parameters callerParameters, CommandPrepared commandPrepared)
		{
			ArgumentNullException.ThrowIfNull(callerParameters);
			ArgumentNullException.ThrowIfNull(commandPrepared);
			if (commandPrepared.Program == null) return false;

			int candidateHash = commandPrepared.Program.PromotionCandidateHash;
			if (!promotionCandidateToActionId.TryGetValue(candidateHash, out int promotedActionId)) return false;
			if (!actionCommands.TryGetValue(promotedActionId, out CommandCacheEntry promotedEntry)) return false;

			// Render the incoming script canonically and extract its literals;
			// the extracted ArgumentsString supplies the runtime parameter
			// values for the promoted Action invocation.
			string canonicalScript = commandPrepared.Program.ConvertToString(this.DatabaseType);
			var extraction = PromotionCandidate.LiteralExtractor.Extract(canonicalScript);

			// Build a Parameters populated with the parameter declaration of
			// the promoted Action (same shape it was registered with) and the
			// extracted argument values.
			string declarationText = promotedEntry.Program.Parameters != null
				? promotedEntry.Program.Parameters.ParametersAsString()
				: extraction.ParametersDeclaration;
			Parameters routedArgs = string.IsNullOrWhiteSpace(declarationText)
				? new Parameters()
				: new Parameters(declarationText);
			if (!string.IsNullOrWhiteSpace(extraction.ArgumentsString))
			{
				routedArgs.LoadArguments(extraction.ArgumentsString);
			}

			// Reshape commandPrepared. The original (Script) Program is
			// discarded; from here onwards the flow behaves as a regular
			// V2 IsExistingAction invocation pointing at the promoted Action.
			commandPrepared.Entry = JournalEntry.IsExistingAction;
			commandPrepared.Program = promotedEntry.Program;
			commandPrepared.CacheEntry = promotedEntry;
			commandPrepared.PromotedArgumentParameters = routedArgs;
			commandPrepared.NeedsToSolveParameters = !promotedEntry.Program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			commandPrepared.NeedsToSolveReferences = false;
			return true;
		}

		// B.3.3: builds the equivalent V2 Action for a recurrent Script,
		// registers it in actionCommands so subsequent Script writes can be
		// routed via the promotionCandidateToActionId index (B.3.4), and
		// returns the Define text + arguments string the caller needs to
		// journal Define + first Invocation atomically. The runtime effect
		// of the current PerformCmd is unaffected — the original Script
		// Program is what executes; the new Action Program exists for
		// future invocations and for replay determinism.
		private PromotionResult MaterializePromotion(int candidateHash, string canonicalScript)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(canonicalScript);

			var extraction = PromotionCandidate.LiteralExtractor.Extract(canonicalScript);

			// Build the Parameters object up front. We need it both to bind
			// the new Action's Program and to render the Define statement's
			// parameter list in canonical `name:type` syntax (different from
			// the `In,name:type` constructor grammar — see Parameters
			// .UserParametersAsCanonicalText vs the Parameters ctor).
			Parameters declaredParameters = string.IsNullOrWhiteSpace(extraction.ParametersDeclaration)
				? new Parameters()
				: new Parameters(extraction.ParametersDeclaration);
			string defineParametersText = declaredParameters.UserParametersAsCanonicalText();

			// B.3.4 idempotent reuse: if an Action with this body text already
			// exists in actionCommands — which happens on the first live
			// write after a restart whose journal already contains a Define
			// for this shape — we wire the candidate→action index without
			// allocating a fresh ActionId AND signal the journal writer to
			// emit only the Invocation row (DefineText = null). This keeps
			// the journal free of duplicate Define rows across restart
			// cycles, while leaving the runtime invariants intact: future
			// live writes of the same shape will hit B.3.4 routing via the
			// freshly-repopulated index.
			int actionId;
			if (actionCommands.TryGetValue(extraction.ActionBodyText, out CommandCacheEntry existingEntry))
			{
				actionId = existingEntry.Id;
				promotionCandidateToActionId[candidateHash] = actionId;
				return new PromotionResult(actionId, defineText: null, extraction.ArgumentsString);
			}

			actionId = this.TakeAndIncrementActionId();

			// Parse the extracted body and bind the parameters declaration
			// so the resulting Program is shaped exactly as it would have
			// been had the user written the Action explicitly.
			Parser parser = ParsersPool.Rent();
			Program actionProgram;
			try
			{
				parser.SetSource(extraction.ActionBodyText);
				actionProgram = parser.Parse(isQuery: false, isCheck: false);
			}
			finally
			{
				ParsersPool.Return(parser);
			}

			actionProgram.SetContextInfo();
			actionProgram.Parameters = declaredParameters;
			actionProgram.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);

			actionCommands.Add(actionId, extraction.ActionBodyText, actionProgram);
			promotionCandidateToActionId[candidateHash] = actionId;

			string defineText = DefineActionStatement.ComposeJournalText(
				actionId,
				defineParametersText,
				extraction.ActionBodyText);

			return new PromotionResult(actionId, defineText, extraction.ArgumentsString);
		}

		private class CommandCache
		{
			private readonly Dictionary<string, CommandCacheEntry> cacheDeCmdsPorScript = new Dictionary<string, CommandCacheEntry>();
			private readonly Dictionary<int, CommandCacheEntry> cacheDeCmdsPorId = new Dictionary<int, CommandCacheEntry>();
			internal CommandCacheEntry Add(int id, string script, Program program)
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

			// Register handlers for SIGTERM and SIGINT (Ctrl+C) for graceful shutdown.
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

		// ================================================================
		// IActorIntrospection — read-only inspection surface (CLI / AI / MCP).
		// Separated from the domain DSL by construction: ANY actor has these
		// verbs by virtue of being Puppeteer, not by virtue of its domain.
		// Today a single verb: ShowEntry. Range / Find / Describe arrive in
		// later steps of the AI-native CLI (handoff 2026-05-31).
		// ================================================================

		public string ShowEntry(long entryId)
		{
			if (entryId <= 0)
				throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			if (dairy == null)
				throw new LanguageException($"Actor '{Name}' has no EventSourcingStorage configured; nothing to introspect.");

			// afterEntryId is exclusive in ReadRecordsAfter — we request from
			// entryId-1 to include entryId. For large journals, the next step
			// (Range / Find) requires a direct ReadRecord(entryId) in DiaryStorage;
			// today the existing path suffices because the hello-world case works on
			// small journals.
			var records = new List<MaterializationRecord>();
			dairy.Storage.ReadRecordsAfter(entryId - 1, records);

			foreach (var record in records)
			{
				if (record.EntryId == entryId)
					return FormatEntryAsToon(record);
				if (record.EntryId > entryId)
					break; // records come sorted ascending
			}

			throw new LanguageException($"Entry {entryId} not found in actor '{Name}'.");
		}

		public string ShowAction(int actionId)
		{
			if (actionId <= 0)
				throw new LanguageException($"ActionId {actionId} must be greater than zero.");
			if (dairy == null)
				throw new LanguageException($"Actor '{Name}' has no EventSourcingStorage configured; nothing to introspect.");

			// Full scan: we filter Define entries with an ActionId match. In case
			// of redefinitions, the highest EntryId wins (signed policy). This
			// is O(n) over the journal — acceptable for the hello-world case. A
			// later layer can index Define-by-actionId in DiaryStorage if
			// performance warrants it.
			var records = new List<MaterializationRecord>();
			dairy.Storage.ReadRecordsAfter(0, records);

			MaterializationRecord? latest = null;
			foreach (var record in records)
			{
				if (record.Kind != MaterializationRecordKind.Define) continue;
				if (record.ActionId != actionId) continue;
				if (!latest.HasValue || record.EntryId > latest.Value.EntryId)
					latest = record;
			}

			if (!latest.HasValue)
				throw new LanguageException($"Action {actionId} has no Define entry in actor '{Name}'.");

			return FormatActionAsToon(latest.Value);
		}

		public string ShowSymbols()
		{
			var sb = new StringBuilder();
			var formatter = new ToonFormatter();
			formatter.BeginDocument(sb);

			formatter.BeginCollection();
			foreach (var symbol in symbolTable.EnumerateGlobalSymbols())
			{
				formatter.BeginCollectionItem();
				formatter.Field("name", symbol.name);
				formatter.Field("staticType", FormatTypeName(symbol.type));
				formatter.Field("runtimeType", FormatTypeName(symbol.value?.GetType() ?? symbol.type));
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("symbols");

			formatter.EndDocument();
			return sb.ToString();
		}

		public string ShowParameterPools()
		{
			var shapes = ParametersPool.SnapshotShapes();
			shapes.Sort((a, b) => b.HighWater.CompareTo(a.HighWater)); // busiest first

			var sb = new StringBuilder();
			var formatter = new ToonFormatter();
			formatter.BeginDocument(sb);

			formatter.BeginCollection();
			foreach (var shape in shapes)
			{
				formatter.BeginCollectionItem();
				formatter.Field("shape", shape.Shape);
				formatter.Field("live", shape.Live);
				formatter.Field("idle", shape.Idle);
				formatter.Field("highWater", shape.HighWater);
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("parameterPools");

			formatter.EndDocument();
			return sb.ToString();
		}

		public string ShowSymbol(string name)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);

			Interpreter.VariableSymbol found = null;
			foreach (var symbol in symbolTable.EnumerateGlobalSymbols())
			{
				if (string.Equals(symbol.name, name, StringComparison.OrdinalIgnoreCase))
				{
					found = symbol;
					break;
				}
			}

			if (found == null)
				throw new LanguageException($"Symbol '{name}' not found in actor '{Name}'.");

			var sb = new StringBuilder();
			var formatter = new ToonFormatter();
			formatter.BeginDocument(sb);

			formatter.Field("name", found.name);
			formatter.Field("staticType", FormatTypeName(found.type));
			Type runtimeType = found.value?.GetType() ?? found.type;
			formatter.Field("runtimeType", FormatTypeName(runtimeType));

			if (found.value != null && HasCustomToString(runtimeType))
				formatter.Field("value", found.value.ToString());

			formatter.EndDocument();
			return sb.ToString();
		}

		public string ShowClass(string className)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(className);

			Type found = null;
			foreach (var asm in LibraryAssemblies)
			{
				foreach (var t in asm.GetTypes())
				{
					if (!t.IsPublic) continue;
					if (string.Equals(t.Name, className, StringComparison.OrdinalIgnoreCase))
					{
						found = t;
						break;
					}
				}
				if (found != null) break;
			}

			if (found == null)
				throw new LanguageException($"Class '{className}' is not in any loaded library. Use --libraries at attach time to load the domain assemblies.");

			var sb = new StringBuilder();
			var formatter = new ToonFormatter();
			formatter.BeginDocument(sb);

			formatter.Field("class", FormatTypeName(found));

			formatter.BeginCollection();
			foreach (var ctor in found.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				if (!IsCallableFromDsl(ctor)) continue;
				formatter.BeginCollectionItem();
				formatter.Field("signature", FormatConstructorSignature(found, ctor));
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("constructors");

			// Implemented interfaces (includes transitive ones inherited from the base
			// chain). The DSL observes them via casting + assignability checks; the AI
			// uses them to know which abstractions the class satisfies.
			formatter.BeginCollection();
			foreach (var iface in found.GetInterfaces())
			{
				formatter.BeginCollectionItem();
				formatter.Field("name", FormatTypeName(iface));
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("interfaces");

			// Fields: same visibility rule (public + internal + protected-internal).
			// Excludes compiler-generated backing fields of auto-properties.
			formatter.BeginCollection();
			foreach (var field in found.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				if (!IsCallableFromDsl(field)) continue;
				if (IsCompilerGenerated(field)) continue;
				formatter.BeginCollectionItem();
				formatter.Field("signature", FormatFieldSignature(field));
				formatter.Field("declaredOn", FormatTypeName(field.DeclaringType));
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("fields");

			// Properties: include if AT LEAST ONE accessor (get or set) is callable
			// from the DSL. The signature emits only the callable accessors —
			// 'Name : String { get; }' for one with a private setter.
			formatter.BeginCollection();
			foreach (var prop in found.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				MethodInfo getter = prop.GetGetMethod(nonPublic: true);
				MethodInfo setter = prop.GetSetMethod(nonPublic: true);
				bool getterCallable = getter != null && IsCallableFromDsl(getter);
				bool setterCallable = setter != null && IsCallableFromDsl(setter);
				if (!getterCallable && !setterCallable) continue;
				formatter.BeginCollectionItem();
				formatter.Field("signature", FormatPropertySignature(prop, getterCallable, setterCallable));
				formatter.Field("declaredOn", FormatTypeName(prop.DeclaringType));
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("properties");

			// Methods: includes PUBLIC and INTERNAL (Assembly) + protected-internal,
			// aligned with the interpreter's ParserValidation.cs. Excludes private and
			// protected (the DSL is not a subclass of the domain, it does not access those).
			// Inherited ones come in via reflection without DeclaredOnly. The declaredOn field
			// makes explicit where each method comes from — the AI distinguishes inheritance.
			formatter.BeginCollection();
			foreach (var method in found.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				if (method.IsSpecialName) continue;                       // Drop get_/set_ + operator overloads
				if (method.DeclaringType == typeof(object)) continue;     // Drop ToString/Equals/GetHashCode/GetType from object
				if (!IsCallableFromDsl(method)) continue;
				formatter.BeginCollectionItem();
				formatter.Field("signature", FormatMethodSignature(method));
				formatter.Field("declaredOn", FormatTypeName(method.DeclaringType));
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("methods");

			formatter.EndDocument();
			return sb.ToString();
		}

		// Accessibility aligned with the DSL: public and internal are always callable;
		// protected-internal too (a mix; in practice it behaves as internal
		// because the DSL is hosted in the same assembly as the class). private and pure
		// protected are not — the DSL is neither a subclass nor code inside the class.
		private static bool IsCallableFromDsl(MethodBase m)
		{
			if (m.IsPublic) return true;
			if (m.IsAssembly) return true;             // internal
			if (m.IsFamilyOrAssembly) return true;     // protected internal
			return false;                               // private, protected (Family), private protected
		}

		// Same rule for fields. FieldInfo does not inherit from MethodBase, so it needs
		// its own check, but the flags mean the same thing.
		private static bool IsCallableFromDsl(System.Reflection.FieldInfo f)
		{
			if (f.IsPublic) return true;
			if (f.IsAssembly) return true;
			if (f.IsFamilyOrAssembly) return true;
			return false;
		}

		// Get-only auto-properties generate backing fields with names like
		// '<PropertyName>k__BackingField' marked with [CompilerGenerated]. Filter them
		// out of the 'fields' list — the user's shape only declares properties; the
		// backing fields are a compiler detail.
		private static bool IsCompilerGenerated(System.Reflection.MemberInfo m)
		{
			return m.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false);
		}

		private static string FormatFieldSignature(System.Reflection.FieldInfo f)
		{
			string prefix = f.IsInitOnly ? "readonly " : "";
			return $"{prefix}{f.Name} : {FormatTypeName(f.FieldType)}";
		}

		private static string FormatPropertySignature(System.Reflection.PropertyInfo p, bool getterCallable, bool setterCallable)
		{
			var sb = new StringBuilder();
			sb.Append(p.Name);
			sb.Append(" : ");
			sb.Append(FormatTypeName(p.PropertyType));
			sb.Append(" { ");
			if (getterCallable) sb.Append("get; ");
			if (setterCallable) sb.Append("set; ");
			sb.Append('}');
			return sb.ToString();
		}

		// Human-readable type format. Handles generics: List`1[T] -> "List<T>".
		// Nested ones resolve recursively: Dictionary`2[K, IEnumerable`1[V]] ->
		// "Dictionary<K, IEnumerable<V>>". The CLR "`N" suffix stays invisible.
		private static string FormatTypeName(Type type)
		{
			if (type == null) return "<null>";
			if (!type.IsGenericType) return type.Name;

			string baseName = type.Name;
			int tickIdx = baseName.IndexOf('`');
			if (tickIdx >= 0) baseName = baseName.Substring(0, tickIdx);

			var args = type.GetGenericArguments();
			string[] argNames = new string[args.Length];
			for (int i = 0; i < args.Length; i++) argNames[i] = FormatTypeName(args[i]);
			return $"{baseName}<{string.Join(", ", argNames)}>";
		}

		private static bool HasCustomToString(Type type)
		{
			var m = type.GetMethod("ToString", Type.EmptyTypes);
			return m != null && m.DeclaringType != typeof(object);
		}

		private static string FormatConstructorSignature(Type type, System.Reflection.ConstructorInfo ctor)
		{
			var pars = ctor.GetParameters();
			string[] parStrs = new string[pars.Length];
			for (int i = 0; i < pars.Length; i++) parStrs[i] = FormatTypeName(pars[i].ParameterType);
			return $"{type.Name}({string.Join(", ", parStrs)})";
		}

		private static string FormatMethodSignature(System.Reflection.MethodInfo m)
		{
			var pars = m.GetParameters();
			string[] parStrs = new string[pars.Length];
			for (int i = 0; i < pars.Length; i++) parStrs[i] = FormatTypeName(pars[i].ParameterType);
			return $"{m.Name}({string.Join(", ", parStrs)}) -> {FormatTypeName(m.ReturnType)}";
		}

		private static string FormatEntryAsToon(MaterializationRecord record)
		{
			var sb = new StringBuilder();
			var formatter = new ToonFormatter();
			formatter.BeginDocument(sb);

			formatter.Field("id", record.EntryId);
			formatter.Field("kind", record.Kind.ToString().ToLowerInvariant());
			formatter.Field("at", record.OccurredAt);

			switch (record.Kind)
			{
				case MaterializationRecordKind.Script:
					formatter.Field("script", record.Script);
					break;
				case MaterializationRecordKind.Invocation:
					formatter.Field("actionId", record.ActionId);
					formatter.Field("arguments", record.Arguments);
					break;
				case MaterializationRecordKind.Define:
					formatter.Field("actionId", record.ActionId);
					formatter.Field("define", record.DefineStatementText);
					break;
			}

			if (!string.IsNullOrEmpty(record.ExposeData))
				formatter.Field("exposeData", record.ExposeData);

			formatter.EndDocument();
			return sb.ToString();
		}

		private static string FormatActionAsToon(MaterializationRecord defineRecord)
		{
			var sb = new StringBuilder();
			var formatter = new ToonFormatter();
			formatter.BeginDocument(sb);

			formatter.Field("actionId", defineRecord.ActionId);
			formatter.Field("defineEntryId", defineRecord.EntryId);
			formatter.Field("at", defineRecord.OccurredAt);
			formatter.Field("define", defineRecord.DefineStatementText);

			formatter.EndDocument();
			return sb.ToString();
		}

		// ================================================================
		// IActorIntrospection — Reactions surface (handoff 2026-06-01).
		// Read-only view over the Follower/Reactions machinery: listing,
		// detail, dry-match. Built on the existing accessors:
		// Phase A counters (MatchCount, SeekEntered, SeekMatched), checkpoint
		// vector (DiaryStorage.GetReactionCheckpoint), MatchSnapshot ring.
		// ================================================================

		public string ShowReactions()
		{
			var sb = new StringBuilder();
			var formatter = new ToonFormatter();
			formatter.BeginDocument(sb);

			formatter.BeginCollection();
			foreach (var reaction in reactions)
			{
				formatter.BeginCollectionItem();
				formatter.Field("name", reaction.Name);
				formatter.Field("matchCount", reaction.MatchCount);
				WriteSeeksCollection(formatter, reaction);
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("reactions");

			formatter.EndDocument();
			return sb.ToString();
		}

		public string ShowReaction(string name)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);

			Follower.Reaction found = null;
			foreach (var reaction in reactions)
			{
				if (string.Equals(reaction.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					found = reaction;
					break;
				}
			}

			if (found == null)
				throw new LanguageException($"Reaction '{name}' is not defined in actor '{Name}'.");

			var sb = new StringBuilder();
			var formatter = new ToonFormatter();
			formatter.BeginDocument(sb);

			formatter.Field("name", found.Name);
			formatter.Field("hydration", FormatHydration(found));
			formatter.Field("action", FormatActionTerminator(found));
			formatter.Field("matchCount", found.MatchCount);

			// Seeks with literal onMatch per level — ShowReactions already spits out the
			// counters; here we add the OnMatch text that defines the correlation.
			formatter.BeginCollection();
			int seekLevel = 0;
			foreach (var engine in found.ReactionEnginesOrEmpty)
			{
				formatter.BeginCollectionItem();
				formatter.Field("name", engine.PatternDescription);
				formatter.Field("isFinal", engine.IsFinalSeek);

				// OnMatch patterns — one or more per Seek. Literal pattern text
				// as the developer wrote it, so the AI can copy/paste
				// to iterate on the pattern without re-deducing it.
				formatter.BeginCollection();
				for (int i = 0; i < engine.Patterns.Count; i++)
				{
					formatter.BeginCollectionItem();
					formatter.Field("text", engine.Patterns[i].PatternText);
					formatter.EndCollectionItem();
				}
				formatter.EndCollection("onMatch");

				formatter.Field("entered", engine.SeekEntered);
				formatter.Field("matched", engine.SeekMatched);

				var (detected, confirmed) = GetCheckpointSafe(found.ReactionId, seekLevel);
				formatter.Field("detected", detected);
				formatter.Field("confirmed", confirmed);

				formatter.EndCollectionItem();
				seekLevel++;
			}
			formatter.EndCollection("seeks");

			// LastMatches ring (up to 32). Empty if the reaction never matched or
			// if ResetCounters was called. Bindings are filtered by construction
			// (RecordCompleteMatch excludes Now/User/Ip).
			formatter.BeginCollection();
			foreach (var snapshot in found.LastMatches)
			{
				formatter.BeginCollectionItem();
				formatter.Field("entryId", snapshot.TriggeringEntryId);
				formatter.Field("occurredAt", snapshot.OccurredAt);
				formatter.BeginCollection();
				foreach (var kvp in snapshot.Bindings)
				{
					formatter.BeginCollectionItem();
					formatter.Field("name", kvp.Key);
					formatter.Field("value", kvp.Value);
					formatter.EndCollectionItem();
				}
				formatter.EndCollection("bindings");
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("lastMatches");

			formatter.EndDocument();
			return sb.ToString();
		}

		public string FindPattern(string patternDsl)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(patternDsl);
			if (dairy == null)
				throw new LanguageException($"Actor '{Name}' has no EventSourcingStorage configured; nothing to match against.");
			// reactions.DiaryStorage is a property that throws if not set —
			// we wire it just in case (the normal EventSourcingStorage path already
			// does, but ConfigureStorageForIntrospection does not). Idempotent: if it was
			// already set, SetDairyStorage overwrites it with the same storage
			// (the same Diary.Storage the normal path builds).
			reactions.SetDairyStorage(dairy.Storage);

			// Temporary Reaction: it is NOT added to the Reactions registry (it is built
			// directly via the internal constructor). Minimal side effect: the first
			// invocation with this pattern creates (formattedReaction -> reactionId) in
			// DiaryStorage.ReactionRegistry; re-invocations of the same pattern reuse
			// the id. Deterministic internal name (hash of the pattern) so the
			// registry bounces the same row instead of growing.
			string ephemeralName = "__find_pattern_" + StableHash(patternDsl);
			var ephemeral = new Follower.Reaction(reactions, ephemeralName, Follower.ReactionMode.Job, Follower.ReactionActivation.Company);
			ephemeral.WithSharedHydration().Seek("FindMatch").OnMatch(patternDsl);
			// No Action plane — ExecuteAction treats ReactionActionType.None as a
			// no-op (explicit case in the switch), but MatchTree still calls
			// RecordCompleteMatch before invoking the action, so the
			// LastMatches ring fills as-is.
			ephemeral.Execute();

			// Idempotency: FindPattern is a query, not a persistent reaction. The
			// Reactions engine saves per-seek checkpoints after matching (necessary
			// for incremental batches in real reactions); for FindPattern that
			// breaks the "same pattern + same journal -> same result" property:
			// the second invocation would start AFTER the last match and return
			// empty. Reset the reactionId/seek checkpoint to (0, 0) so the
			// next query re-starts from genesis. Single-seek by construction
			// of FindPattern (.Seek("FindMatch")), so only level 0.
			if (ephemeral.ReactionId > 0)
			{
				dairy.Storage.SaveReactionLastProcessedEntryId(ephemeral.ReactionId, 0, 0);
			}

			var sb = new StringBuilder();
			var formatter = new ToonFormatter();
			formatter.BeginDocument(sb);

			formatter.Field("pattern", patternDsl);
			formatter.Field("matchesFound", (long)ephemeral.LastMatches.Count);

			formatter.BeginCollection();
			foreach (var snapshot in ephemeral.LastMatches)
			{
				formatter.BeginCollectionItem();
				formatter.Field("entryId", snapshot.TriggeringEntryId);
				formatter.Field("occurredAt", snapshot.OccurredAt);
				formatter.BeginCollection();
				foreach (var kvp in snapshot.Bindings)
				{
					formatter.BeginCollectionItem();
					formatter.Field("name", kvp.Key);
					formatter.Field("value", kvp.Value);
					formatter.EndCollectionItem();
				}
				formatter.EndCollection("bindings");
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("matches");

			formatter.EndDocument();
			return sb.ToString();
		}

		// Helper shared between ShowReactions and ShowReaction: emits the seeks
		// collection with name + entered + matched + detected + confirmed per level.
		// ShowReaction overrides this with a more detailed version (includes
		// onMatch and isFinal); ShowReactions uses this compact shape.
		private void WriteSeeksCollection(ToonFormatter formatter, Follower.Reaction reaction)
		{
			formatter.BeginCollection();
			int level = 0;
			foreach (var engine in reaction.ReactionEnginesOrEmpty)
			{
				formatter.BeginCollectionItem();
				formatter.Field("name", engine.PatternDescription);
				formatter.Field("entered", engine.SeekEntered);
				formatter.Field("matched", engine.SeekMatched);
				var (detected, confirmed) = GetCheckpointSafe(reaction.ReactionId, level);
				formatter.Field("detected", detected);
				formatter.Field("confirmed", confirmed);
				formatter.EndCollectionItem();
				level++;
			}
			formatter.EndCollection("seeks");
		}

		// Reaction never executed -> reactionId == long.MinValue -> there is no checkpoint
		// row. GetReactionCheckpoint rejects ids <= 0, so we short-circuit here
		// and return (0, 0) — semantics equivalent to the "empty checkpoint" the
		// storage would return if it knew about the id.
		//
		// Storage choice: the Reaction uses the storage configured in
		// reactions.SetDairyStorage(...) — which may diverge from actor.Handler.dairy
		// in tests that inject an independent storage. We prefer to query
		// reactions.DiaryStorage if wired; fallback to dairy.Storage when
		// only the EventSourcingStorage(...) path ran (which ties both to the same
		// Diary). If neither is available we return zeros.
		private (long detected, long confirmed) GetCheckpointSafe(long reactionId, int seekLevel)
		{
			if (reactionId <= 0) return (0L, 0L);
			DB.DiaryStorage storage = null;
			try { storage = reactions.DiaryStorage; }
			catch (LanguageException) { storage = null; } // DiaryStorage getter throws if unset
			if (storage == null && dairy != null) storage = dairy.Storage;
			if (storage == null) return (0L, 0L);
			return storage.GetReactionCheckpoint(reactionId, seekLevel);
		}

		// hydration: compact single-line format — the mode + optionally
		// the untilSeek in parentheses. Without untilSeek it is just "Shared" /
		// "Independent"; it helps the AI recognize the BFS/DFS strategy at a
		// glance without having to decode two separate fields.
		private static string FormatHydration(Follower.Reaction reaction)
		{
			string mode = reaction.HydrationMode == Follower.HydrationMode.Shared ? "Shared" : "Independent";
			if (string.IsNullOrWhiteSpace(reaction.HydrationUntilSeek)) return mode;
			return $"{mode}(untilSeek: '{reaction.HydrationUntilSeek}')";
		}

		// action terminator: plane + verb. Metadata.Materialize adds the destination
		// in quotes so the AI sees the target without requesting another show. None is
		// legal at runtime (explicit case in ExecuteAction) — we
		// report it as-is for half-built or observation-only
		// reactions.
		private static string FormatActionTerminator(Follower.Reaction reaction)
		{
			switch (reaction.ActionType)
			{
				case Follower.ReactionActionType.Program:
					return "Program.Emit";
				case Follower.ReactionActionType.Causation:
					return "Causation.Continue";
				case Follower.ReactionActionType.Metadata:
					if (reaction.MetadataKind == Follower.MetadataKind.Elide) return "Metadata.Elide";
					if (reaction.MetadataKind == Follower.MetadataKind.Materialize)
					{
						return string.IsNullOrWhiteSpace(reaction.MaterializeDestination)
							? "Metadata.Materialize"
							: $"Metadata.Materialize '{reaction.MaterializeDestination}'";
					}
					return "Metadata";
				case Follower.ReactionActionType.Outbox:
					return string.IsNullOrWhiteSpace(reaction.OutboxDestination)
						? "Outbox.Emit"
						: $"Outbox.Emit '{reaction.OutboxDestination}'";
				default:
					return "None";
			}
		}

		// Stable hash of the DSL pattern to name FindPattern's ephemeral reaction.
		// The purpose is NOT security but determinism: the SAME pattern must collapse
		// to the SAME reactionId in the DiaryStorage registry, so re-invocations do not
		// inflate the registry. SHA-256 truncated to 8 hex bytes (16 chars) — collisions
		// are astronomically improbable for the range of patterns an actor sees.
		private static string StableHash(string text)
		{
			using var sha = System.Security.Cryptography.SHA256.Create();
			byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
			var sb = new StringBuilder(16);
			for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
			return sb.ToString();
		}

		internal class ConcurrentParametersPool
		{
			private readonly ConcurrentStack<Parameters> _objects = new ConcurrentStack<Parameters>();
			private readonly int _maxPoolSize;
			private int _count = 0;

			// Pooling BY SHAPE (shape-keyed). Each shape key (the script of the
			// cached V2 operation) has its own stack. The parameter shape is
			// invariant per Query/Command (same script => same type/order/count),
			// so the rented instance keeps its slots (Parameter + VariableSymbol)
			// and the caller's configure only overwrites values via SetParameter, without
			// re-assigning. Unlike the keyless pool, it is NOT purged on Rent.
			//
			// Sizing policy (signed): FREE growth up to the signature's
			// concurrency peak (no cap). The high-water = the real peak of
			// simultaneous concurrency of that signature. Capping blindly would turn the
			// single warm-up into recurring churn under peak, right on the hot path (the
			// reads scale N*K without a writeLock).
			private readonly ConcurrentDictionary<string, ShapePool> _byShape
				= new ConcurrentDictionary<string, ShapePool>(StringComparer.Ordinal);

			// Per-shape state. Idle = reusable idle instances. Live = how many
			// are rented (out) now. PeakLiveSinceTrim = peak of Live in the current
			// decay window (reset on each Trim). HighWaterEver = historical peak of Live
			// (observability: concurrency peak the signature experienced).
			private sealed class ShapePool
			{
				internal readonly ConcurrentStack<Parameters> Idle = new ConcurrentStack<Parameters>();
				internal int Live;
				internal int PeakLiveSinceTrim;
				internal int HighWaterEver;
			}

			private static void UpdateMax(ref int location, int value)
			{
				int current;
				while (value > (current = Volatile.Read(ref location)))
				{
					if (Interlocked.CompareExchange(ref location, value, current) == current) break;
				}
			}

			internal ConcurrentParametersPool(int maxPoolSize = 200)
			{
				if (maxPoolSize <= 0) throw new LanguageException($"{nameof(ConcurrentParametersPool)} maxPoolSize {maxPoolSize} must be greater than 0.");
				_maxPoolSize = maxPoolSize;
				// Decay (policy #2) self-driven by memory pressure: each Gen2 GC
				// invokes Trim(). Weak reference => it does not prevent the pool from being collected
				// together with its ActorHandler.
				Gen2GcCallback.Register(static state => { ((ConcurrentParametersPool)state).Trim(); return true; }, this);
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
				// Playbill final refactor: the pool no longer pre-seeds Now.
				// V1 (PerformCmd(string,string,string), PerformCmdAsync(string,string,string))
				// injects Now via the indexer in its entry path before descending to the
				// internal machinery. V2 fluent (.WithParameters(...)) declares Now explicitly.
				return new Parameters();
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

#if PUPPETEER_HIDE_INTERNALS
			[DebuggerHidden]
#endif
			internal Parameters Rent(string shapeKey)
			{
				ArgumentNullException.ThrowIfNull(shapeKey);
				var sp = _byShape.GetOrAdd(shapeKey, static _ => new ShapePool());
				int live = Interlocked.Increment(ref sp.Live);
				UpdateMax(ref sp.PeakLiveSinceTrim, live);
				UpdateMax(ref sp.HighWaterEver, live);
				if (sp.Idle.TryPop(out var item))
				{
					// Slot reuse: NOT purged. The caller's configure overwrites
					// the values on the already-formed Parameter/VariableSymbol.
					return item;
				}
				// First Rent of this shape (or empty stack due to concurrency): a new
				// empty instance; configure shapes it and Return files it under the key.
				return new Parameters();
			}

#if PUPPETEER_HIDE_INTERNALS
			[DebuggerHidden]
#endif
			internal void Return(string shapeKey, Parameters item)
			{
				ArgumentNullException.ThrowIfNull(shapeKey);
				ArgumentNullException.ThrowIfNull(item);
				item.Clear();
				var sp = _byShape.GetOrAdd(shapeKey, static _ => new ShapePool());
				Interlocked.Decrement(ref sp.Live);
				sp.Idle.Push(item); // free growth: no cap per shape (policy #1)
			}

			// Decay (policy #2): one trim step over all shapes. Keeps
			// idle up to covering the concurrency peak of the previous window
			// (keep = previousPeak - live); discards the surplus of a past burst. After
			// ~2 windows without load, idle decays to 0 and the shape is removed from the pool (eviction
			// gate: pool->0 => out of the dictionary). The real TRIGGER of the
			// OPERATION eviction (recency of the Query/Command cache, longer
			// horizon) is a separate piece living in that cache; here only the pool removes its
			// own entry when it reaches 0. No wall clock: it is invoked from a
			// maintenance tick (e.g. Gen2 GC callback) or explicitly.
			internal void Trim()
			{
				foreach (var kv in _byShape)
				{
					var sp = kv.Value;
					int live = Volatile.Read(ref sp.Live);
					int peak = Interlocked.Exchange(ref sp.PeakLiveSinceTrim, live);
					int keep = peak - live;
					if (keep < 0) keep = 0;
					while (sp.Idle.Count > keep && sp.Idle.TryPop(out _)) { }
					// Gate: fully cold shape (none out, no idle) => leaves the pool.
					if (Volatile.Read(ref sp.Live) == 0 && sp.Idle.IsEmpty)
					{
						_byShape.TryRemove(new KeyValuePair<string, ShapePool>(kv.Key, sp));
					}
				}
			}

			// Observability (signed design): instances currently idle for a
			// shape. It also lets tests verify per-shape reuse.
			internal int IdleCount(string shapeKey)
			{
				ArgumentNullException.ThrowIfNull(shapeKey);
				return _byShape.TryGetValue(shapeKey, out var sp) ? sp.Idle.Count : 0;
			}

			// Observability: historical concurrency peak of the signature. It is NOT a memory
			// bound; it is the signal that points to tuning the business logic when
			// an endpoint accumulates unbounded concurrency.
			internal int HighWaterMark(string shapeKey)
			{
				ArgumentNullException.ThrowIfNull(shapeKey);
				return _byShape.TryGetValue(shapeKey, out var sp) ? Volatile.Read(ref sp.HighWaterEver) : 0;
			}

			// Observability: number of distinct shapes with a live pool right now.
			internal int ShapeCount => _byShape.Count;

			// Snapshot of all live shapes with their counters, for the introspection
			// surface. HighWater (historical concurrency peak) is the diagnostic
			// signal that points to tuning the business logic.
			internal List<(string Shape, int Live, int Idle, int HighWater)> SnapshotShapes()
			{
				var list = new List<(string, int, int, int)>(_byShape.Count);
				foreach (var kv in _byShape)
				{
					var sp = kv.Value;
					list.Add((kv.Key, Volatile.Read(ref sp.Live), sp.Idle.Count, Volatile.Read(ref sp.HighWaterEver)));
				}
				return list;
			}
		}

		internal class ConcurrentParsersPool
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
