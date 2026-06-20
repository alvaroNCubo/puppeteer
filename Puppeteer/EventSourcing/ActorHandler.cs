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

		// Logger per-actor (source-of-truth). Default ConsoleLogger es util en
		// desarrollo: Error -> stderr, Debug -> stdout. El host inyecta su impl
		// (Serilog, MEL, NLog) via Actor.UseLogger(...). Cada ActorHandler tiene
		// su propio sink; dos actores en el mismo proceso pueden tener loggers
		// distintos sin pisarse.
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
		// Tell terminators de las Reactions SI ejecutan (construyen el envelope y
		// lo encolan en PendingTells) y el envelope SI se despacha via Transport
		// tras soltar el lock — pero NO se escribe la entrada `tell ...` al journal
		// compartido del actor. Set por Performance.Start(asFollower:true) antes de
		// arrancar las Cued reactions; el primary lo deja false (default) y
		// journaliza normal.
		//
		// Etapa 2 (dispatch-without-journaling) implementada: el gate de la
		// escritura vive en ExecuteCommandWithWriteLock (writeNewEntry=false cuando
		// SuppressReactionJournaling && InReactionAction), no en TellStatement.
		internal bool SuppressReactionJournaling { get; set; }

		// Provider del rol vivo para el gate de ReactionActivation
		// (DirectorOnly / CastOnly / Company). Default: el actor actua como
		// director/primary — un actor standalone (sin Stage ni Performance) es
		// el unico escritor de si mismo, asi que corre DirectorOnly + Company y
		// nunca CastOnly. Choreography lo sobreescribe con el rol vivo: el Stage
		// P2P pasa () => IsDirector; la Performance Theater () => !isFollower
		// (cambia en el handover). Se consulta en cada Reaction.Execute para que
		// un fan-out replicado no re-dispare la reaction en el nodo equivocado.
		private Func<bool> actingAsDirectorProvider = () => true;
		internal bool IsActingAsDirector => actingAsDirectorProvider();
		internal void SetActingAsDirectorProvider(Func<bool> provider)
		{
			ArgumentNullException.ThrowIfNull(provider);
			actingAsDirectorProvider = provider;
		}

		// Check transitorio de una Causation.Continue(check:, ...). NO se evalua
		// aqui (el origen siempre cumpliria); se hornea en el TellEnvelope.Check
		// que TellStatement.Execute construye durante el PerformCmd del body, para
		// que el RECEPTOR lo corra como CheckThenCommand. Set/clear por
		// ExecuteCausation; leido por TellStatement via SymbolTable.
		internal string CausationTellCheck
		{
			get => symbolTable.CurrentCausationCheck;
			set => symbolTable.CurrentCausationCheck = value;
		}

		// Shadow Replay — S1 (handoff_shadow_S1_implementation.md / design §3.0).
		// Cuando IsShadow esta encendido, este ActorHandler es una derivacion
		// laboratorio de un actor de produccion: lee el journal real (replay) pero
		// escribe en su PROPIO storage y NO produce efecto externo. El aislamiento
		// se conduce desde aqui: los Tell cross-actor de una Reaction NO se despachan
		// (se dropean en el drain de PendingTells) y EnsureTransportConfigured no
		// exige un Transport. El shadow tampoco se registra como destination de
		// Materialization del primary (es un unregistered reader — no bloquea el
		// Distill del primary), por construccion: CreateShadow nunca llama
		// primary.Materialization.Register. El shadow es un primitivo del actor,
		// NO un subtipo de Performance — un ShadowPerformance lo hospeda por
		// composicion.
		internal bool IsShadow { get; private set; }

		// Shadow Replay — S3 (skip-preview). Cuando esta encendido (solo valido en un
		// shadow), las reactions Elide corren en dry-run: capturan el batch en
		// Reaction.WouldSkip pero NO commitean la elision (no
		// MarkEventsAsElidedWithCheckpoint). Default false; encender via EnableSkipPreview().
		internal bool SkipPreviewEnabled { get; private set; }

		internal void EnableSkipPreview()
		{
			if (!IsShadow) throw new LanguageException("Skip-preview (dry-run de Elide) solo es valido en un shadow. Construilo via actor.Shadow(cfg) / ActorHandler.CreateShadow(cfg).");
			SkipPreviewEnabled = true;
		}

		// Fuente de replay del shadow: el journal del actor primary en modo
		// solo-lectura. Solo se setea en el handler shadow (via CreateShadow). El
		// shadow lee records crudos del primary por SyncUntil y los re-aplica contra
		// su propio storage. null en un handler normal.
		private DiaryStorage shadowReplaySource;

		// Shadow Replay — S2 (continuous mirror). shadowingActive: el loop de
		// StartShadowing esta corriendo. shadowingTask: el background Task del mirror.
		// SHADOW_POLL_MILLIS: intervalo de poll del head del primary.
		private volatile bool shadowingActive;
		private Task shadowingTask;
		private const int SHADOW_POLL_MILLIS = 50;

		// Shadow Replay — S1. Primitivo del actor: produce un ActorHandler aislado
		// (storage PROPIO, IsShadow=true) alimentado por replay del journal de ESTE
		// actor (el primary). Mismas Libraries (dominio identico). Las reactions NO
		// se clonan automaticamente del primary (el builder es imperativo y no
		// serializable en S1); el caller las re-declara via cfg.ConfigureReactions
		// con la misma API de Tema A apuntando al shadow.
		//
		// Guard de aislamiento: el storage del shadow debe ser DISTINTO del primary.
		// Se valida por nombre de actor — el shadow corre con un nombre derivado
		// (`<primary>-shadow-<Id>`), de modo que los backends por-nombre (InMemory) o
		// por-path-con-nombre (FileSystem) nunca comparten storage fisico con el
		// primary aunque la connection coincida. Ademas, si el connection del shadow
		// es literalmente el del primary Y el backend no particiona por nombre, se
		// rechaza explicitamente.
		internal ActorHandler CreateShadow(ShadowConfig cfg)
		{
			ArgumentNullException.ThrowIfNull(cfg);
			if (dairy == null) throw new LanguageException("Cannot create a shadow before the primary actor has EventSourcingStorage configured: there is no journal to replay from.");

			string shadowName = this.Name + "-shadow-" + cfg.Id;
			if (string.Equals(shadowName, this.Name, StringComparison.Ordinal))
				throw new LanguageException("Shadow name collided with the primary actor name. This is impossible by construction (the name carries a '-shadow-' infix); if you see this, ShadowConfig.Id was empty.");

			// Construye el actor shadow en la MISMA familia (V1/V2) que el primary,
			// con las mismas LibraryAssemblies (dominio identico) y el mismo
			// CompiledModePolicy. Un shadow no puede ser de familia distinta: las
			// ramas de rehidratacion del handler discriminan `actor is ActorV1`.
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

			// Storage PROPIO del shadow — JAMAS el del primary. Esto wirea el Diary,
			// deja el handler en estado Recovered (IsAlive) y conecta el storage a
			// Reactions del shadow.
			shadow.EventSourcingStorage(cfg.ShadowStorageType, cfg.ShadowStorageConnection);

			// Guard duro: el storage fisico del shadow no puede ser el mismo objeto
			// que el del primary. Con nombres de actor distintos esto se cumple por
			// construccion en los 4 backends; el assert protege contra una regresion
			// futura del factory de storage.
			DiaryStorage shadowStorage = shadow.TryGetDiaryStorage();
			DiaryStorage primaryStorage = this.TryGetDiaryStorage();
			if (shadowStorage != null && ReferenceEquals(shadowStorage, primaryStorage))
				throw new LanguageException("Shadow isolation violated: the shadow's storage resolved to the SAME storage instance as the primary. A shadow must write to its own storage and never touch the primary's journal.");

			// Replay source = journal del primary, solo-lectura. El shadow lee records
			// crudos de aqui en SyncUntil y los re-aplica contra su propio storage.
			shadow.shadowReplaySource = primaryStorage;

			// Reactions: el caller re-declara las que quiera observar + experimentales
			// (misma API de Tema A apuntando al shadow). No se clonan automaticamente
			// en S1.
			cfg.ConfigureReactions?.Invoke(shadowActor);

			return shadow;
		}

		// Accessor del Actor que envuelve a este handler. Usado por la fachada
		// actor.Shadow(cfg) para construir el objeto Shadow sobre el actor shadow
		// recien creado por CreateShadow.
		internal Actor ShadowActor => actor;

		// Shadow Replay — S1. SyncUntil(toEntryId): replay del journal del primary
		// desde GENESIS (EntryId 0) hasta toEntryId inclusive, aplicado al storage
		// PROPIO del shadow. Es TECHO, no piso — el replay SIEMPRE arranca en genesis
		// porque el estado en toEntryId depende de toda la historia previa. Tras
		// SyncUntil el shadow queda FORKEADO: acepta PerformCmd local en su propio
		// storage (linea de tiempo divergente). Mirror continuo (StartShadowing) es
		// S2 y es mutuamente excluyente con el fork — no se implementa aqui.
		//
		// Mecanismo V1+V2 (firmado: S1 sirve ambas familias): se COPIAN los records
		// crudos del primary al storage del shadow via la API estructurada de escritura
		// (WriteScriptEntry para V1; WriteDefineEntry + WriteInvocationEntry para V2),
		// preservando EntryId / OccurredAt / ExposeData y los datos V2
		// (DefineStatementText + Arguments que el MaterializationRecord ya transporta).
		// Luego se rehidrata el estado in-memory del shadow desde su propio storage via
		// CatchUpFromJournal — la rehidratacion estandar maneja V1 (Script) y V2
		// (Define -> actionCommands, Invocation) de forma uniforme. NO se re-ejecuta
		// como comando nuevo: copiar+rehidratar es cross-backend, preserva los EntryIds
		// exactos del primary, y reusa la maquinaria de replay ya probada (la misma de
		// red-black / CatchUpFromJournal).
		internal void SyncUntil(long toEntryId)
		{
			if (!IsShadow) throw new LanguageException("SyncUntil is only valid on a shadow ActorHandler. Build one via actor.Shadow(cfg) / ActorHandler.CreateShadow(cfg).");
			if (toEntryId < 0) throw new ArgumentException("toEntryId must be non-negative", nameof(toEntryId));
			if (shadowReplaySource == null) throw new LanguageException("Shadow replay source is not configured. This shadow was not produced by CreateShadow.");
			if (dairy == null) throw new LanguageException("Shadow storage is not configured. Call EventSourcingStorage on the shadow before SyncUntil.");
			if (shadowingActive) throw new LanguageException("Cannot SyncUntil while continuous shadowing is active. Continuous mirror and point-in-time fork are mutually exclusive — call StopShadowing first.");

			// Copia records crudos del primary desde GENESIS (techo = toEntryId) al
			// storage propio del shadow, luego rehidrata. Ver CopyPrimaryRecordsToShadow.
			long lastWritten = CopyPrimaryRecordsToShadow(0, toEntryId);
			if (lastWritten > 0)
				CatchUpFromJournal(lastWritten);
		}

		// Shadow Replay. Copia records crudos del primary (afterEntryId exclusivo;
		// toEntryIdCap inclusivo, 0 => sin tope) al storage PROPIO del shadow via la API
		// estructurada — V1 (Script) y V2 (Define + Invocation) uniforme, preservando
		// EntryId / OccurredAt / ExposeData y los datos V2 (DefineStatementText +
		// Arguments que el MaterializationRecord ya transporta). Unregistered reader: NO
		// pasa por Materialization.Register, asi que no participa del watermark del primary
		// ni bloquea su Distill. Retorna el ultimo EntryId escrito (0 si nada). NO
		// rehidrata — eso lo hace el caller (CatchUpFromJournal).
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

		// Shadow Replay — S4. Seedea el shadow desde su replay source hasta toEntryId pero
		// con un set de EntryIds marcados como ELIDIDOS antes de rehidratar, de modo que la
		// rehidratacion los salta (RehydrateFromEvent filtra IsEventElided). Usado por el
		// elision-impact diff para construir el "twin elidido". Marca con reactionId
		// sentinel 0 (la rehidratacion solo chequea presencia del EntryId, no el reactionId).
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
					// reactionId sentinel positivo (MarkEventsAsElided exige > 0). El twin
					// no tiene reactions reales, asi que no colisiona; la rehidratacion solo
					// chequea presencia del EntryId, no el reactionId.
					storage.EventElisionStorage.MarkEventsAsElided(elideEntryIds, 1, DateTime.UtcNow);
			}

			if (lastWritten > 0)
				CatchUpFromJournal(lastWritten);
		}

		internal bool IsShadowingActive => shadowingActive;

		// Shadow Replay — S2. StartShadowing(): continuous mirror — un loop en background
		// que sigue el head del primary en near-real-time (pull incremental de records
		// nuevos + rehidratacion). Mutuamente excluyente con el fork de SyncUntil. Lossy
		// by design (B.2): si una iteracion falla, se reintenta en la proxima. Guarded
		// contra doble-arranque. Parar via StopShadowing() (lo llama el Dispose del Shadow).
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
					// Lossy by design: una falla transitoria no detiene el mirror;
					// la proxima iteracion reintenta desde this.EntryId.
				}

				if (shadowingActive)
					Thread.Sleep(SHADOW_POLL_MILLIS);
			}
		}

		// Shadow Replay — S1. TTL kill-all del storage del shadow. Solo aplica a un
		// handler shadow (IsShadow). Para el backend InMemory limpia la lista de
		// eventos compartida del actor shadow (su nombre es unico, no toca al primary).
		// Para FileSystem/SQL el borrado fisico del schema/PVC es responsabilidad del
		// host/operador (S6 K8s) — aqui es un no-op silencioso (no se borra una DB de
		// produccion por accidente). Idempotente.
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

		internal void EventSourcingStorage(DatabaseType dbType, string connection)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connection);

			dairy = new Diary(dbType, connection, eventJournalClient: this);

			Console.WriteLine($"Starting {this.GetType()}'s Actor");

			EventSourcingStorage(dairy);

			reactions.SetDairyStorage(dairy.Storage);

			if (this.OnAfterRecovering != null) this.OnAfterRecovering(dbType, connection, this.Name, this.EntryId);

		}

		// Configure storage SIN rehidratacion. Util para IActorIntrospection: el CLI
		// quiere leer entries crudos de un journal cualquiera sin necesitar las
		// LibraryAssemblies del dominio (que pueden no estar disponibles en el
		// binario puppeteer-cli generico). Solo habilita los paths de lectura raw
		// (ReadRecordsAfter); invocacion / rehidratacion / reactions quedan
		// inactivos. Si se intenta usar Perform / Tell / Reactions sobre un actor
		// configurado por aqui, fallaran porque el symbol table esta vacio y las
		// libraries no fueron cargadas.
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
			// Validacion defensiva: una NRE silenciosa originada aqui es muy dificil de
			// diagnosticar (sin mensaje, stack solo apunta a este metodo). Si en algun
			// path llegamos con dairy o un colaborador requerido en null, queremos un
			// LanguageException explicito que diga que pieza falto. Reportado por
			// LiquidityAPI sobre 2.0.1-beta.9553 (BUG_EventSourcingStorage_NRE_9553_LiquidityAPI.md
			// §9.2).
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

				// Pipeline de rehidratacion (3 etapas): RehydrateFromEvent -> eventsQueue ->
				// parser -> parsedQueue -> resolver -> preparedQueue -> exec.
				// Cada task envuelve su foreach en try/finally para garantizar CompleteAdding
				// sobre la queue de output (y la de entry) incluso si lanza excepcion.
				// Sin esto, una excepcion en cualquier etapa dejaria los workers downstream bloqueados
				// indefinidamente en GetConsumingEnumerable() y Task.WaitAll nunca retornaria.
				//
				// Antes habia una etapa extra (preparer + collector) donde el parse corria en un
				// Task.Run que se await-eaba de inmediato. Como GenerateAndRentProgram usa un unico
				// parserForRecovering, eso NO paralelizaba el parse: solo agregaba una asignacion de
				// Task + dos saltos de thread-pool + un handoff de cola (programTaskQueue) por entry.
				// Se fusiono en una sola etapa de parseo sincrona; el solapamiento parse||resolve||exec
				// lo siguen dando las queues y los Task.Run de cada etapa.
				//
				// Rehidratacion permisiva (firmado 2026-05-19, post-mortem reporte 9553):
				// si un record individual falla en cualquier stage (parser, resolver, executor),
				// el error se loguea via IPuppeteerLogger.Error con entryId + script + exception
				// y la rehidratacion SIGUE con el siguiente record. Este es el contrato del
				// Puppeteer viejo de Exchange Engine (Dairy.cs:508-535) que se perdio en el
				// refactor del pipeline multi-stage; el consumidor que se migra esperaba este
				// comportamiento. Si el host quiere ser estricto (abortar al primer error),
				// inyecta un IPuppeteerLogger custom que throw-ee en Error -- los catches lo
				// dejan escapar como faulted Task y Task.WaitAll lo propaga.
				bool stageTiming = LabInstrumentation.StageTimingEnabled;
				// Snapshot de ticks acumulados ANTES de esta rehidratacion: los acumuladores de
				// LabInstrumentation son estaticos/globales (suman todos los actores), asi que el
				// print del final reporta el DELTA de esta corrida, no el acumulado del proceso.
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
								// GenerateAndRentProgram retorna null para Invocations huerfanas
								// (cache miss del actionId), camino inalcanzable post-Fase-4 por
								// construccion; se omite en silencio.
								if (rentedProgram != null)
								{
									parsedQueue.Add(rentedProgram);
								}
							}
							catch (Exception ex)
							{
								// El tipo + mensaje del inner exception se intercala en el
								// message porque algunos hosts (p.ej. ASP.NET con loggers
								// que no encadenan ex.ToString()) descartan la segunda linea
								// que el ConsoleLogger.Error escribe via WriteLine(exception).
								// Sin esto el consumidor solo ve "Rehydration parser failed"
								// sin pista de que fallo realmente.
								Logger.Error(
									$"Rehydration parser failed. EntryId={parserEntryId}, OccurredAt={parserOccurredAt:O}, Cause={ex.GetType().FullName}: {ex.Message}, Script:\n{parserScript ?? "<action invocation>"}",
									ex);
							}
							finally
							{
								// El EventData ya no se necesita: GenerateAndRentProgram copio
								// EntryId/OccurredAt/Arguments al Program y este no retiene el
								// EventData. Se devuelve al pool exactamente una vez, en exito o
								// error (antes lo hacia el collector tras el await).
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
									// Rehidratacion = replay de eventos que YA fueron validados estaticamente
									// cuando se ejecutaron en vivo (el path de escritura valida). Re-validarlos
									// aqui es redundante por definicion — solo encontraria errores que ya se
									// habrian encontrado al escribirlos — y agrega CPU que contiende con la
									// etapa de ejecucion (la rehidratacion es CPU-bound). El Puppeteer viejo
									// 100% interpretado NO validaba en replay (resolvia lazy en ejecucion) y
									// rehidrataba mas rapido. Se mantiene SolveIdReferences (el binding, que la
									// ejecucion necesita y la version vieja tambien hacia).
									//
									// withStaticValidation se ata a HasEval: ValidateStatically solo tiene un
									// efecto necesario (no-validacion) en su rama eval, donde propaga tipos de
									// globals para resolucion cross-entry. Sin evals (todo el journal) se salta
									// la validacion completa; con evals (raro) se conserva esa propagacion.
									rentedProgram.SolveReferences(rentedProgram.Parameters, withStaticValidation: rentedProgram.HasEval);
								if (stageTiming) LabInstrumentation.AddResolveTicks(System.Diagnostics.Stopwatch.GetTimestamp() - resolveT0);

								preparedQueue.Add(rentedProgram);
							}
							catch (Exception ex)
							{
								// Inner exception intercalado en el message — ver
								// comentario equivalente en el preparerTask. Pedido
								// explicito de LiquidityAPI en el reporte
								// BUG_RehydrationStaticValidation_LiquidityUpgrader: la
								// segunda linea que escribe ConsoleLogger.Error
								// (WriteLine(exception)) no llega al stdout del host en
								// algunos consumidores y el equipo queda sin pista del
								// LanguageException que ValidateStatically esta lanzando.
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
								// Inner exception intercalado — misma razon que en
								// preparerTask/resolverTask: el consumidor pierde la
								// segunda linea de ConsoleLogger.Error en hosts sin
								// captura de WriteLine(exception).
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
								// Solo contamos events ejecutados correctamente — un error
								// permisivo NO cuenta como "applied".
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

				// Desglose de tiempo por etapa de ESTA rehidratacion. El pipeline corre las 3
				// etapas concurrentes, asi que el wall-clock ~= max(etapa) + fill/drain: la etapa
				// con mas ms es el cuello de botella y decide donde rinde la siguiente mejora
				// (parse -> parallel parse; resolve -> coleccion-durante-parse; exec -> piso serial,
				// la palanca es el cache estructural o abaratar el interprete). Se imprime solo con
				// StageTimingEnabled (o PUPPETEER_STAGE_TIMING=1).
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
						// B.3.1: rehydration also observes the promotion candidate —
						// replaying 30k scripts in a legacy journal decrements the
						// counter the same way live writes do, so post-replay the
						// system arrives in the exact state it would have reached
						// running the same writes live. B.3.3 returns a
						// PromotionResult here only if the counter tipped AND the
						// candidate is not yet in the index — we DISCARD it during
						// rehydration because per Alvaro's signed clarification
						// "no cambia nada: solo a partir de la siguiente
						// PerformCommand que se escriba aparece el Define". Replay
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

				// Now es un parametro de SISTEMA excluido del journal; en rehidratacion se
				// re-inyecta desde el OccurredAt journaleado (determinista, no wall-clock).
				// Aplica a Script (V1, re-parseado) y Action (V2, reconstruida del define
				// que ya excluye Now). Se inyecta ANTES de SolveReferences/Perform: en el
				// primer Perform, ExecuteExpression llama SolveReferences(program.Parameters)
				// y asi @Now resuelve como parametro.
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

					// Simetria con PrepareCommandProgram (ActorHandler.cs:1209): el path
					// vivo siempre llama SetContextInfo() despues de parsear para que
					// cada Statement tenga su Program backref. Lo replicamos aqui despues
					// de Rehydrate() para que el program rehidratado sea estructural-
					// mente identico al recien parseado y los visitors que se apoyen en
					// statement.Program (hoy EvalStatement) no caigan silenciosos durante
					// la rehidratacion. Reportado en
					// BUG_RehydrationStaticValidation_LiquidityUpgrader_LiquidityAPI §4.bis.a.
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

			// Now es un parametro de SISTEMA excluido del journal; se re-inyecta desde el
			// OccurredAt journaleado para Script (V1) y Action (V2). Viaja en program.Parameters
			// hasta la etapa de ejecucion del pipeline de rehidratacion, donde el primer
			// Perform corre ExecuteExpression -> SolveReferences(program.Parameters) y @Now
			// resuelve como parametro.
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


		// Playbill final refactor: V1 entry path. La firma (ip, user) sobrevive por compat
		// con StageHook.PerformCmd(script) pero los valores se ignoran — ip/user dejaron de
		// viajar como parametros del script. La inyeccion de Now (V1 backward compat) la hace
		// el overload PerformCmd(script, parameters, now) tras PrepareCommandProgram para no
		// alterar la decision IsScript/IsNewAction.
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

		// Fase 4.5 + Playbill final refactor: EMPTY_PARAMETERS ya no precarga ip/user
		// (eliminados del journal) ni Now (ya no es system param — solo V1 entry path
		// lo inyecta via indexer; V2 declara Now explicito si el script lo necesita).
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

			string parametersDeclarationText = Parameters.CanonicalDeclarationsToParametersString(defineStmt.ParametersText);
			if (!string.IsNullOrEmpty(parametersDeclarationText))
			{
				// Se pasan las DomainLibraries para que un @parametro tipado como enum del
				// dominio (journalizado por nombre de tipo) se resuelva al reconstruir los
				// Parameters desde texto; sin esto el parser interno solo aceptaria primitivos.
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

				// HasAnyUserParameter excluye el Now de sistema: la clasificacion solo
				// depende de los parametros de usuario (ver Parameters.HasAnyUserParameter).
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
					// Con parameters de user: modo compilado, se cachea con ActionId.
					// Se serializa como Action (ActionId + arguments) en el journal.
					commandPrepared.Entry = JournalEntry.IsNewAction;
					var nextActionId = this.TakeAndIncrementActionId();
					commandPrepared.Program.AdjustCompilationMode(useInterpretedMode: false, this.actor.CompiledModePolicy);
					commandPrepared.CacheEntry = actionCommands.Add(nextActionId, script, commandPrepared.Program);
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
		// Flujo: LoadArguments -> SolveReferences/SolveParameters -> Perform -> persistir al journal.
		// writeNewEntry es false durante rehidratacion (RecoveringState) para no re-persistir eventos.
		private string ExecuteCommandWithWriteLock(CommandPrepared commandPrepared, Parameters parameters, DateTime now, string Ip, string User)
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

				// Eval determinism: el snapshot del dairy se tomo en PrepareCommand
				// ANTES de Perform, cuando EvalStatement.forDairy aun era null — por
				// eso renderizaba la forma LITERAL `Eval(<expr>);` (no deterministica
				// en replay porque re-evalua una expresion que depende de estado de
				// runtime). Re-renderizamos aqui, ya ejecutado el programa: cada Eval
				// ejecutado tiene su forDairy poblado con la asignacion EVALUADA (e.g.
				// `available = 5;`), asi el journal queda deterministico. Un Eval que
				// no se ejecuto (condicional/rama no tomada) conserva forDairy==null y
				// sigue renderizando el literal, que es lo correcto: el replay lo
				// re-evalua en su mismo contexto. Gated en HasEval para no pagar el
				// ConvertToString doble en scripts sin Eval.
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
						// El script se persiste INTEGRO en el journal (sin tag de error). La
						// informacion del fallo vive en el sink de IPuppeteerLogger.Error y
						// puede llegar al host via inyeccion de logger custom (Serilog/MEL/
						// NLog/bridge-a-email). Asi el journal es un registro fidedigno de los
						// comandos intentados, no un canal de transporte de metadata de errores.
						Logger.Error(
							$"Script execution failed at write-time. EntryId={nextEntryId}, OccurredAt={now:O}, Script:\n{commandPrepared.FormatedScriptForDairy}",
							executionEx);
						dairy.WriteScriptEntry(nextEntryId, commandPrepared.FormatedScriptForDairy, now, null);
					}
				}
				if (executionError) throw;
				// executionError == false: la ejecucion EN MEMORIA ya tuvo exito y el fallo
				// ocurrio journalizando el comando (p.ej. serializar argumentos/firma). La
				// memoria avanzo pero el diario no -> en replay el comando desaparece (perdida
				// silenciosa). Un comando ejecutado-pero-no-persistido es estado corrupto: se
				// propaga, nunca se traga. (No hay backup util aqui: el script ya se ejecuto y
				// el WriteScriptEntry de respaldo solo aplica a fallos de EJECUCION.)
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

				string Ip = "";
				string User = "";

				rwLock.EnterWriteLock();

				try
				{
					PrepareCommandProgram(script, parameters, _reusableCommandPrepared);

					// Now es un parametro de SISTEMA inyectado por el framework (V1 y V2) con
					// el valor del comando. Es per-call (thread-safe) y visible al pattern
					// matching como id.IsParameter, pero queda EXCLUIDO de la firma del Action
					// y del blob de argumentos (Parameters.IsSystemNow). Se inyecta DESPUES de
					// PrepareCommandProgram para que la decision IsScript/IsNewAction observe
					// solo los parametros de usuario, no el Now de sistema.
					// Lever 1: solo se inyecta si el programa referencia @Now (ReferencesNow);
					// los comandos que no usan el reloj no pagan box ni set. OccurredAt sale del
					// 'now' local, no del parametro. Lever 3: SetNow tipado (sin busqueda+ImplicitCast).
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

			// Playbill final refactor: timeStamp ya no se lee de parameters["Now"] (V2 puede no
			// declararlo). El `now` que llega como argumento es la fuente autoritativa.
			timeStamp = now;

			return result;
		}

		// Playbill final refactor: V1 entry path async. ip/user se ignoran. La inyeccion de
		// Now (V1 backward compat) la hace el overload PerformCmdAsync(script, parameters)
		// tras el branch IsScript/IsNewAction para no alterar la decision de persistencia.
		internal async Task<string> PerformCmdAsync(string script, string ip, string user)
		{
			Parameters parameters = ParametersPool.Rent();

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

					// HasAnyUserParameter excluye el Now de sistema (ver el path sync).
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
					// CACHE HIT: Program ya compilado, referencias ya resueltas.
					// Solo rebindear parameters (SolveParameters) si es modo interpretado.
					entry = JournalEntry.IsExistingAction;
					program = cacheDeComandosEntry.Program;
					needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
					needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}

				commandLineError = "";

				Ip = "";
				User = "";

				now = DateTime.Now;

				// Now es un parametro de SISTEMA inyectado por el framework (V1 y V2),
				// excluido de la firma/args del journal (Parameters.IsSystemNow). La
				// decision IsScript/IsNewAction ya se tomo arriba sobre los parametros de
				// usuario, asi que inyectar Now aqui no la altera.
				// Lever 1: solo si el programa referencia @Now. Lever 3: SetNow tipado.
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

					// Eval determinism: re-render del snapshot del dairy post-Perform
					// (mirror del path sync en ExecuteCommandWithWriteLock). El snapshot
					// se tomo antes de Perform con EvalStatement.forDairy==null, por eso
					// emitia el `Eval(<expr>);` literal no deterministico. Ya ejecutado,
					// cada Eval ejecutado journaliza su forma EVALUADA.
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
						// Script integro al journal; el error viaja por IPuppeteerLogger.Error
						// con el contexto completo (entryId, occurredAt, script, exception).
						// Ver bloque equivalente en ExecuteCommandWithWriteLock para rationale.
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
				// sentence "tell salió" is factual; delivery is the transport's problem.
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

			// Now ya no se garantiza en parameters (puede ser EMPTY_PARAMETERS). El `now`
			// capturado al inicio del comando es la fuente autoritativa del timestamp.
			timeStamp = now;

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

				if (parameters == EMPTY_PARAMETERS || parameters.HasAnyParameter())
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

			// Now es un parametro de SISTEMA per-call (V1 y V2): cada query lleva su propio
			// Now en su Parameters rentado, por lo que es thread-safe bajo el read lock
			// (no es estado global compartido). Excluido de la firma/args del journal — las
			// queries no se persisten, pero la simetria de Parameters lo mantiene coherente.
			DateTime nowForQry = DateTime.Now;
			// Lever 1: solo si la query referencia @Now. Lever 3: SetNow tipado.
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

			// Playbill final refactor: timeStamp ya no se lee de parameters["Now"] (V2 puede no
			// declararlo). El nowForQry local es la fuente autoritativa.
			timeStamp = nowForQry;

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

				if (parameters == EMPTY_PARAMETERS || parameters.HasAnyParameter())
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

			// Now es un parametro de SISTEMA per-call (V1 y V2), thread-safe bajo el read
			// lock (vive en el Parameters de esta llamada, no en estado global). Excluido
			// de la firma/args del journal via Parameters.IsSystemNow.
			DateTime nowForEmit = DateTime.Now;
			// Lever 1: solo si el emit referencia @Now. Lever 3: SetNow tipado.
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

			// Playbill final refactor: timeStamp ya no se lee de parameters["Now"] (V2 puede no
			// declararlo).
			timeStamp = nowForEmit;
		}

		// PerformChk: ejecuta un check read-only contra el actor. No persiste al journal.
		// Retorna null/empty si el check pasa, o un mensaje de error si falla.
		// Concurrencia: READ LOCK — misma semantica que PerformQry.
		// Parser: isCheck:true — produce un Program que ejecuta via ExecuteCheck() en lugar de Perform().
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
				// CACHE MISS: parsear con isCheck:true (produce Program para ExecuteCheck).
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
				// Cache miss: resolver estructura completa (LValues, RValues, globales, parameters)
				needsToSolveReferences = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}
			else
			{
				// CACHE HIT: solo rebindear parameters. Ver comentario del method sobre la diferencia con PerformQry.
				needsToSolveParameters = !program.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
			}

			// Now es un parametro de SISTEMA per-call (V1 y V2), thread-safe bajo el read
			// lock (vive en el Parameters de esta llamada, no en estado global). Excluido
			// de la firma/args del journal via Parameters.IsSystemNow.
			DateTime nowForChk = DateTime.Now;
			// Lever 1: solo si el check referencia @Now. Lever 3: SetNow tipado.
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

			// Playbill final refactor: timeStamp ya no se lee de parameters["Now"] (V2 puede no
			// declararlo).
			timeStamp = nowForChk;

			return result;
		}

		// PerformCheckThenCmd: ejecuta un check sin bloqueo (via PerformChk con read lock),
		// y si pasa, toma write lock y re-ejecuta el check + source atomicamente.
		//
		// Flujo de dos fases:
		// 1. PerformChk(scriptForChk) — read lock, sin bloqueo. Si falla, retorna inmediatamente.
		// 2. Bajo write lock: re-ejecuta el check (ExecuteCheck) para verificar que sigue valido
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

				// Fase 4.5 refactor Playbill: ip/user dejaron de viajar como parametros del script.
				string Ip = "";
				string User = "";

				PrepareCommandProgram(scriptForCmd, parameters, _reusableCommandPrepared);

				if (!QuerysEnCache.TryGetValue(scriptForChk, out programChk))
				{
					// CACHE MISS del check script: parsear con isCheck:true.
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
					// Cache miss: resolver estructura completa
					needsToSolveReferencesChk = !programChk.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}
				else
				{
					// CACHE HIT: solo rebindear parameters. Ver documentacion en PerformChk.
					needsToSolveParametersChk = !programChk.IsCompiledMode || this.actor.CompiledModePolicy == CompilationModePolicy.AlwaysInterpreted;
				}

				// Now es un parametro de SISTEMA inyectado por el framework (V1 y V2),
				// excluido de la firma/args del journal (Parameters.IsSystemNow). Se inyecta
				// DESPUES de PrepareCommandProgram para no alterar la decision IsScript/
				// IsNewAction (que debe observar solo los parametros de usuario).
				// Lever 1: solo si el command O el check referencian @Now (la inyeccion se
				// movio aqui, tras resolver programChk, para poder consultar ambos programas);
				// el mismo Parameters alimenta el re-check (ExecuteCheck) y el command, asi que
				// basta un set. Lever 3: SetNow tipado.
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
						// Re-check bajo write lock: verificar que el estado no cambio desde fase 1
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

			// Playbill final refactor: timeStamp ya no se lee de parameters["Now"] (V2 puede no
			// declararlo). El `now` que llega como argumento es la fuente autoritativa.
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

					// Now es un parametro de SISTEMA excluido del journal; se re-inyecta desde
					// el OccurredAt journaleado para Script (V1) y Action (V2), antes de
					// SolveReferences/Perform (loop sincrono del replay red-black).
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

		// ================================================================
		// IActorIntrospection — superficie read-only de inspeccion (CLI / IA / MCP).
		// Separada del DSL del dominio por construccion: estos verbos los tiene
		// CUALQUIER actor por ser Puppeteer, no por ser Banco / Tetris / etc.
		// Hoy un solo verbo: ShowEntry. Range / Find / Describe llegan en pasos
		// siguientes del CLI IA-native (handoff 2026-05-31).
		// ================================================================

		public string ShowEntry(long entryId)
		{
			if (entryId <= 0)
				throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			if (dairy == null)
				throw new LanguageException($"Actor '{Name}' has no EventSourcingStorage configured; nothing to introspect.");

			// afterEntryId es exclusivo en ReadRecordsAfter — pedimos desde
			// entryId-1 para incluir entryId. Para journals grandes, paso siguiente
			// (Range / Find) requiere un ReadRecord(entryId) directo en DiaryStorage;
			// hoy alcanza con el path existente porque hola-mundo trabaja sobre
			// journals pequenos.
			var records = new List<MaterializationRecord>();
			dairy.Storage.ReadRecordsAfter(entryId - 1, records);

			foreach (var record in records)
			{
				if (record.EntryId == entryId)
					return FormatEntryAsToon(record);
				if (record.EntryId > entryId)
					break; // records vienen sorted ascending
			}

			throw new LanguageException($"Entry {entryId} not found in actor '{Name}'.");
		}

		public string ShowAction(int actionId)
		{
			if (actionId <= 0)
				throw new LanguageException($"ActionId {actionId} must be greater than zero.");
			if (dairy == null)
				throw new LanguageException($"Actor '{Name}' has no EventSourcingStorage configured; nothing to introspect.");

			// Scan completo: filtramos Define entries con ActionId match. En caso
			// de redefiniciones, el mayor EntryId gana (politica firmada). Esto
			// es O(n) sobre el journal — aceptable para hola-mundo. Una capa
			// siguiente puede indexar Define-by-actionId en DiaryStorage si la
			// performance lo amerita.
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
			shapes.Sort((a, b) => b.HighWater.CompareTo(a.HighWater)); // mas concurridas primero

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

			// Interfaces implementadas (incluye transitivas heredadas de la cadena de
			// bases). El DSL las observa via casting + assignability checks; la IA las
			// usa para saber que abstracciones la clase satisface.
			formatter.BeginCollection();
			foreach (var iface in found.GetInterfaces())
			{
				formatter.BeginCollectionItem();
				formatter.Field("name", FormatTypeName(iface));
				formatter.EndCollectionItem();
			}
			formatter.EndCollection("interfaces");

			// Fields: misma regla de visibilidad (public + internal + protected-internal).
			// Excluye compiler-generated backing fields de auto-properties.
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

			// Properties: include si AL MENOS UN accessor (get o set) es callable
			// desde DSL. El signature emite solo los accessors callable —
			// 'Name : String { get; }' para una con setter privado.
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

			// Metodos: incluye PUBLIC e INTERNAL (Assembly) + protected-internal,
			// alineado con ParserValidation.cs del interprete. Excluye private y
			// protected (el DSL no es subclase del dominio, no accede a esos).
			// Inherited entran via reflection sin DeclaredOnly. El campo declaredOn
			// hace explicito de donde viene cada metodo — la IA distingue herencia.
			formatter.BeginCollection();
			foreach (var method in found.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
			{
				if (method.IsSpecialName) continue;                       // Drop get_/set_ + operator overloads
				if (method.DeclaringType == typeof(object)) continue;     // Drop ToString/Equals/GetHashCode/GetType de object
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

		// Accesibilidad alineada con el DSL: public e internal son siempre callable;
		// protected-internal tambien (mezcla; en la practica funciona como internal
		// porque la DSL hospeda en el mismo assembly que la clase). private y pure
		// protected no — el DSL no es subclase ni codigo dentro de la clase.
		private static bool IsCallableFromDsl(MethodBase m)
		{
			if (m.IsPublic) return true;
			if (m.IsAssembly) return true;             // internal
			if (m.IsFamilyOrAssembly) return true;     // protected internal
			return false;                               // private, protected (Family), private protected
		}

		// Misma regla para fields. FieldInfo no hereda de MethodBase asi que necesita
		// su propio chequeo, pero los flags significan lo mismo.
		private static bool IsCallableFromDsl(System.Reflection.FieldInfo f)
		{
			if (f.IsPublic) return true;
			if (f.IsAssembly) return true;
			if (f.IsFamilyOrAssembly) return true;
			return false;
		}

		// Auto-properties get-only generan backing fields con nombres tipo
		// '<PropertyName>k__BackingField' marcados con [CompilerGenerated]. Filtrarlos
		// de la lista 'fields' — el shape del usuario solo declara properties; los
		// backing fields son detalle del compilador.
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

		// Formato legible de tipo. Maneja generics: List`1[T] -> "List<T>".
		// Anidados se resuelven recursivos: Dictionary`2[K, IEnumerable`1[V]] ->
		// "Dictionary<K, IEnumerable<V>>". El sufijo "`N" del CLR queda invisible.
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
		// Read-only view sobre la maquinaria de Follower/Reactions: listing,
		// detalle, dry-match. Construido sobre los accessors existentes:
		// counters Phase A (MatchCount, SeekEntered, SeekMatched), checkpoint
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

			// Seeks con onMatch literal por nivel — ShowReactions ya escupe los
			// counters; aqui anadimos el OnMatch text que define la correlacion.
			formatter.BeginCollection();
			int seekLevel = 0;
			foreach (var engine in found.ReactionEnginesOrEmpty)
			{
				formatter.BeginCollectionItem();
				formatter.Field("name", engine.PatternDescription);
				formatter.Field("isFinal", engine.IsFinalSeek);

				// OnMatch patterns — uno o mas por Seek. Patron texto literal
				// como lo escribio el desarrollador, asi la IA puede copy/paste
				// para iterar sobre el patron sin re-deducirlo.
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

			// LastMatches ring (hasta 32). Vacio si la reaction nunca matcheo o
			// si ResetCounters fue llamado. Bindings se filtran por construccion
			// (RecordCompleteMatch excluye Now/User/Ip).
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
			// reactions.DiaryStorage es property que lanza si no esta seteado —
			// la wireamos por las dudas (el path EventSourcingStorage normal ya lo
			// hace, pero ConfigureStorageForIntrospection no). Idempotente: si ya
			// estaba seteado, SetDairyStorage lo sobreescribe con el mismo storage
			// (mismo Diary.Storage que el path normal arma).
			reactions.SetDairyStorage(dairy.Storage);

			// Reaction temporal: NO se anade al registry de Reactions (se construye
			// directo via constructor internal). Side effect minimo: la primera
			// invocacion con este patron crea (formattedReaction -> reactionId) en
			// DiaryStorage.ReactionRegistry; re-invocaciones del mismo patron reusan
			// el id. Nombre interno deterministico (hash del patron) para que el
			// registry rebote la misma fila en lugar de crecer.
			string ephemeralName = "__find_pattern_" + StableHash(patternDsl);
			var ephemeral = new Follower.Reaction(reactions, ephemeralName, Follower.ReactionMode.Job, Follower.ReactionActivation.Company);
			ephemeral.WithSharedHydration().Seek("FindMatch").OnMatch(patternDsl);
			// Sin Action plane — ExecuteAction trata ReactionActionType.None como
			// no-op (case explicito en el switch), pero MatchTree todavia llama
			// RecordCompleteMatch antes de invocar la accion, asi que el ring
			// LastMatches se llena tal cual.
			ephemeral.Execute();

			// Idempotencia: FindPattern es una query, no una reaction persistente. El
			// motor de Reactions guarda checkpoints per-seek tras matchear (necesario
			// para batches incrementales en reactions reales); para FindPattern eso
			// rompe la propiedad "mismo patron + mismo journal -> mismo resultado":
			// la segunda invocacion arrancaria DESPUES del ultimo match y devolveria
			// vacio. Reset al checkpoint del reactionId/seek a (0, 0) para que la
			// proxima query re-arranque desde genesis. Single-seek por construccion
			// del FindPattern (.Seek("FindMatch")), asi que solo nivel 0.
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

		// Helper compartido entre ShowReactions y ShowReaction: emite la coleccion
		// seeks: con name + entered + matched + detected + confirmed por nivel.
		// ShowReaction sobreescribe esto con una version mas detallada (incluye
		// onMatch y isFinal); ShowReactions usa este shape compacto.
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

		// Reaction nunca ejecutada -> reactionId == long.MinValue -> no hay checkpoint
		// row. GetReactionCheckpoint rechaza ids <= 0, asi que cortamos en seco aqui
		// y devolvemos (0, 0) — semantica equivalente al "checkpoint vacio" que el
		// storage retornaria si supiera del id.
		//
		// Eleccion de storage: la Reaction usa el storage configurado en
		// reactions.SetDairyStorage(...) — que puede divergir de actor.Handler.dairy
		// en tests que inyectan un storage independiente. Preferimos consultar a
		// reactions.DiaryStorage si esta wired; fallback a dairy.Storage cuando
		// solo el path EventSourcingStorage(...) corrio (que ata ambos al mismo
		// Diary). Si ninguno esta disponible devolvemos zeros.
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

		// hydration: formato compacto en una sola linea — el modo + opcionalmente
		// el untilSeek entre parentesis. Sin untilSeek queda solo "Shared" /
		// "Independent"; sirve a la IA para reconocer la estrategia BFS/DFS de un
		// vistazo sin tener que decodificar dos campos separados.
		private static string FormatHydration(Follower.Reaction reaction)
		{
			string mode = reaction.HydrationMode == Follower.HydrationMode.Shared ? "Shared" : "Independent";
			if (string.IsNullOrWhiteSpace(reaction.HydrationUntilSeek)) return mode;
			return $"{mode}(untilSeek: '{reaction.HydrationUntilSeek}')";
		}

		// action terminator: plane + verbo. Metadata.Materialize agrega el destination
		// entre comillas para que la IA vea el target sin pedir otro show. None es
		// legal en tiempo de ejecucion (case explicito en ExecuteAction) — lo
		// reportamos tal cual para reactions a medio construir o de uso solo-
		// observacional.
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
				default:
					return "None";
			}
		}

		// Hash estable del patron DSL para nombrar la reaction efimera de FindPattern.
		// El proposito NO es seguridad sino determinismo: el MISMO patron debe colapsar
		// al MISMO reactionId en el registry del DiaryStorage, asi re-invocaciones no
		// inflan el registry. SHA-256 truncado a 8 bytes hex (16 chars) — colisiones
		// son astronomicamente improbables para el rango de patrones que un actor ve.
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

			// Pooling POR FORMA (shape-keyed). Cada clave de forma (el script de la
			// operacion V2 cacheada) tiene su propia pila. La forma de parametros es
			// invariante por Query/Command (mismo script => mismo tipo/orden/cantidad),
			// asi que el instance rentado conserva sus slots (Parameter + VariableSymbol)
			// y el configure del caller solo sobreescribe valores via SetParameter, sin
			// re-asignar. A diferencia del pool keyless, NO se purga en Rent.
			//
			// Politica de sizing (firmada): crecimiento LIBRE hasta el pico de
			// concurrencia de la firma (sin cap). El high-water = pico real de
			// concurrencia simultanea de esa firma. Capar a ciegas convertiria el
			// warm-up unico en churn recurrente bajo pico, justo en el hot path (las
			// lecturas escalan N*K sin writeLock).
			private readonly ConcurrentDictionary<string, ShapePool> _byShape
				= new ConcurrentDictionary<string, ShapePool>(StringComparer.Ordinal);

			// Estado por forma. Idle = instancias ociosas reutilizables. Live = cuantas
			// estan rentadas (fuera) ahora. PeakLiveSinceTrim = pico de Live en la ventana
			// de decaimiento actual (se resetea en cada Trim). HighWaterEver = pico de Live
			// historico (observabilidad: pico de concurrencia que vivio la firma).
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
				// Decaimiento (politica #2) auto-dirigido por presion de memoria: cada GC
				// Gen2 invoca Trim(). Referencia debil => no impide la recoleccion del pool
				// junto con su ActorHandler.
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
				// Playbill final refactor: el pool ya no pre-seedea Now.
				// V1 (PerformCmd(string,string,string), PerformCmdAsync(string,string,string))
				// inyecta Now via indexer en su entry path antes de bajar a la maquinaria
				// interna. V2 fluent (.WithParameters(...)) declara Now explicito.
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
					// Reuso de slots: NO se purga. El configure del caller sobreescribe
					// los valores sobre los Parameter/VariableSymbol ya formados.
					return item;
				}
				// Primer Rent de esta forma (o pila vacia por concurrencia): instancia
				// nueva vacia; el configure la forma y el Return la archiva bajo la clave.
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
				sp.Idle.Push(item); // crecimiento libre: sin cap por forma (politica #1)
			}

			// Decaimiento (politica #2): un paso de trim sobre todas las formas. Mantiene
			// idle hasta cubrir el pico de concurrencia de la ventana anterior
			// (keep = peakAnterior - live); descarta el sobrante de un burst pasado. Tras
			// ~2 ventanas sin carga, idle decae a 0 y la forma se saca del pool (gate de
			// la eviccion: pool->0 => fuera del diccionario). El GATILLO real de la
			// eviccion de la OPERACION (recencia del cache de Query/Command, horizonte mas
			// largo) es pieza aparte que vive en ese cache; aqui solo el pool retira su
			// propia entrada cuando llega a 0. Sin reloj de pared: se invoca desde un tick
			// de mantenimiento (p.ej. callback de GC Gen2) o explicitamente.
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
					// Gate: forma totalmente fria (nadie fuera, sin ociosas) => sale del pool.
					if (Volatile.Read(ref sp.Live) == 0 && sp.Idle.IsEmpty)
					{
						_byShape.TryRemove(new KeyValuePair<string, ShapePool>(kv.Key, sp));
					}
				}
			}

			// Observabilidad (diseño firmado): instancias actualmente ociosas para una
			// forma. Tambien permite a los tests verificar el reuso por forma.
			internal int IdleCount(string shapeKey)
			{
				ArgumentNullException.ThrowIfNull(shapeKey);
				return _byShape.TryGetValue(shapeKey, out var sp) ? sp.Idle.Count : 0;
			}

			// Observabilidad: pico de concurrencia historico de la firma. NO es una cota
			// de memoria; es la señal que apunta al tuning de la logica de negocio cuando
			// un endpoint acumula concurrencia no acotada.
			internal int HighWaterMark(string shapeKey)
			{
				ArgumentNullException.ThrowIfNull(shapeKey);
				return _byShape.TryGetValue(shapeKey, out var sp) ? Volatile.Read(ref sp.HighWaterEver) : 0;
			}

			// Observabilidad: numero de formas distintas con pool vivo ahora.
			internal int ShapeCount => _byShape.Count;

			// Snapshot de todas las formas vivas con sus contadores, para la superficie
			// de introspeccion. HighWater (pico de concurrencia historico) es la señal de
			// diagnostico que apunta al tuning de la logica de negocio.
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
