using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal abstract class DiaryStorage
	{
		protected readonly string ConnectionString;
		protected readonly string Name;

		protected IActorEventJournalClient EventJournalClient;
		protected readonly EventDataPool EventDataPool;

		// Atajo per-actor para los call sites de error logging en los backends
		// y en wrappers que reciben un DiaryStorage (p.ej. ReplicationAgent).
		// El sink llega via Actor.Logger -> ActorHandler.Logger -> IActorEventJournalClient.Logger.
		// Reemplaza el viejo Loggers.GetInstance().Db (singleton process-wide) en F4
		// del refactor de logger.
		internal IPuppeteerLogger Logger => EventJournalClient.Logger;

		protected static StreamWriter swDairyPeriodRangeToExport;
		protected static MemoryStream msDairyPeriodRangeToExport;

		internal DateTime DateOfLastActivity = DateTime.Now;

		// Wire-equivalent record bytes are produced (via BinaryEventCodec.Encode*) by
		// each backend after a successful write and passed to this callback. Stage
		// (Choreography) subscribes to fire CueEvents to Cast pods for cross-pod
		// replication. FS backend's bytes come for free (they are the bytes just written
		// to disk); SQL/InMemory backends synthesize equivalent bytes via the codec.
		internal Action<long, byte[]> OnRecordWritten;

		// Synthetic encoding for backends that store typed columns (not bytes).
		// FS backend produces these bytes as part of its write path; SQL/InMemory
		// synthesize them only when OnRecordWritten has subscribers (lazy).
		protected byte[] EncodeScriptRecord(long entryId, string script, DateTime now, string exposeData)
		{
			return FileSystem.BinaryEventCodec.EncodeScriptEvent(entryId, now, script,
				FileSystem.PayloadCompression.None, FileSystem.EncryptionMode.None, null, exposeData);
		}

		protected byte[] EncodeInvocationRecord(int actionId, long entryId, DateTime now, string arguments, string exposeData)
		{
			return FileSystem.BinaryEventCodec.EncodeActionEvent(entryId, now, actionId, arguments,
				FileSystem.PayloadCompression.None, FileSystem.EncryptionMode.None, null, exposeData);
		}

		protected byte[] EncodeDefineRecord(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData)
		{
			return FileSystem.BinaryEventCodec.EncodeDefineEvent(entryId, now, actionId, defineStatementText,
				FileSystem.PayloadCompression.None, FileSystem.EncryptionMode.None, null, exposeData);
		}


		protected EventElisionStorage eventElisionStorage;
		internal EventElisionStorage EventElisionStorage => eventElisionStorage;

		// Paper 5 / claim 4: storage para markers de Materialize. Por-actor-por-construccion
		// (cross-ref project_actor_per_db_principle.md). Cada backend lo instancia en su
		// constructor — paralelo al patron de EventElisionStorage.
		protected EventMaterializationStorage eventMaterializationStorage;
		internal EventMaterializationStorage EventMaterializationStorage => eventMaterializationStorage;

		// Materialize v2 / Fase 0 (firmado D1 2026-05-13). Registry una-row-por-destination
		// que habilita el invariante Materialize-then-Distill de Fase 1. Vive en la misma
		// DB que el actor (por-actor-por-construccion). v1 EventMaterializationStorage de
		// arriba sigue vivo como cola de markers para delivery push; este storage agrega
		// la capa de contrato de presencia con watermark monotonico por destination.
		protected MaterializationCheckpointStorage materializationCheckpointStorage;
		internal MaterializationCheckpointStorage MaterializationCheckpointStorage => materializationCheckpointStorage;


		protected DiaryStorage(IActorEventJournalClient eventJournalClient, string connectionString)
		{
			ArgumentNullException.ThrowIfNull(connectionString);

			ArgumentNullException.ThrowIfNull(eventJournalClient);
			if (String.IsNullOrWhiteSpace(eventJournalClient.ActorName)) throw new LanguageException("Actor name can not be empty.");

			this.Name = eventJournalClient.ActorName;

			this.ConnectionString = connectionString;

			this.EventJournalClient = eventJournalClient;
			this.EventDataPool = new EventDataPool();
		}

		protected internal abstract long RehydrateFromEvent(long afterEntryId, bool includeExposeData = false);
		protected internal abstract Task<long> RehydrateFromEventAsync(long afterEntryId, bool includeExposeData = false);

		// Reactions replay against a temporary journal client (the ActorReactions
		// wrapper) instead of the storage's own. Forward-only: the append-only
		// journal has a single natural reading order.
		internal long RehydrateFromEvent(IActorEventJournalClient temporaryClient, long afterEntryId, bool includeExposeData = false)
		{
			ArgumentNullException.ThrowIfNull(temporaryClient);

			var originalClient = this.EventJournalClient;

			try
			{
				this.EventJournalClient = temporaryClient;
				return RehydrateFromEvent(afterEntryId, includeExposeData);
			}
			finally
			{
				this.EventJournalClient = originalClient;
			}
		}


		// Phase 6 of the Action refactor: dropped WriteActionEntry +
		// WriteNewActionEntry (and their async siblings). Phase 4 cutover already
		// stopped invoking them; Phase 5 drained the lateral _ACTION reads. Phase 6
		// deletes the methods themselves. The post-refactor write API is
		// WriteScriptEntry / WriteDefineEntry / WriteInvocationEntry /
		// WriteDefineWithFirstInvocation.

		protected internal abstract void WriteScriptEntry(long entryId, string script, DateTime now, string exposeData = null);
		protected internal abstract Task WriteScriptEntryAsync(long entryId, string script, DateTime now, string exposeData = null);

		// ============================================================
		// Phase 2 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// new write APIs that replace the WriteNewActionEntry / WriteActionEntry pair
		// once Phase 4 cuts the live path over. Cohabitation with the legacy methods
		// is intentional — Phase 3 implements these per backend, Phase 4 flips the
		// caller in ActorHandler, Phase 6 deletes the legacy methods.
		//
		// Discriminator on the journal row (post-cutover):
		//   - script != NULL ∧ action IS NULL  → Script entry  (WriteScriptEntry)
		//   - script != NULL ∧ action != NULL  → Define entry  (WriteDefineEntry)        — NEW
		//   - script IS NULL ∧ action != NULL  → Invocation     (WriteInvocationEntry)   — NEW
		//
		// Phase 4 SPLIT MODEL (firmado tras observación de Alvaro 2026-05-09):
		// Define entries are *pure declarations* — they do NOT carry first-invocation
		// arguments. The first invocation is a separate Invocation entry written
		// immediately after the Define. Rationale: a Reaction MarkAsSkip on a first
		// invocation must elide the invocation effect WITHOUT erasing the actor's
		// vocabulary. Combining declaration + first invocation in a single row
		// would couple them, and a MarkAsSkip on that row would discard the
		// declaration too — catastrophic. Splitting keeps Define independent of any
		// invocation; MarkAsSkip on the Invocation entry (or any later invocation)
		// elides only the invocation, leaving the Define intact.
		//
		// Default implementation is `throw new NotImplementedException(...)` so backends
		// can adopt the new path incrementally (firmado: InMemory → FileSystem →
		// SQLServer → MySQL). Backends that have not yet adopted them stay buildable
		// but will fail loudly if a caller routes to them prematurely. Phase 4 only
		// flips the caller after every backend has overridden the new methods.
		//
		// `defineStatementText` is the canonical DSL sentence
		//   `define action <id> (params) as <body> end;`
		// that Phase 1's parser round-trips. Splitting actionId out of the text is
		// not duplication: the backend writes it verbatim into the `action` column to
		// keep the legacy invocation lookup path simple, while `script` carries the
		// full sentence for replay re-parsing.
		// ============================================================
		protected internal virtual void WriteDefineEntry(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			throw new NotImplementedException($"{this.GetType().Name} has not adopted WriteDefineEntry yet (Phase 3 of the Action refactor). Either adopt it in this backend or keep the caller on the legacy WriteNewActionEntry path until cutover.");
		}

		protected internal virtual Task WriteDefineEntryAsync(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			throw new NotImplementedException($"{this.GetType().Name} has not adopted WriteDefineEntryAsync yet (Phase 3 of the Action refactor). Either adopt it in this backend or keep the caller on the legacy WriteNewActionEntryAsync path until cutover.");
		}

		protected internal virtual void WriteInvocationEntry(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			throw new NotImplementedException($"{this.GetType().Name} has not adopted WriteInvocationEntry yet (Phase 3 of the Action refactor). Either adopt it in this backend or keep the caller on the legacy WriteActionEntry path until cutover.");
		}

		protected internal virtual Task WriteInvocationEntryAsync(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			throw new NotImplementedException($"{this.GetType().Name} has not adopted WriteInvocationEntryAsync yet (Phase 3 of the Action refactor). Either adopt it in this backend or keep the caller on the legacy WriteActionEntryAsync path until cutover.");
		}

		// ============================================================
		// Phase 4 atomic write — WriteDefineWithFirstInvocation
		// (firmado por Alvaro tras observación 2026-05-09).
		//
		// Writes the Define declaration AND the first Invocation as TWO separate
		// journal rows, atomically. The split into two rows preserves
		// MarkAsSkip-safety (Reactions can elide the Invocation without erasing
		// the Define declaration); the atomic write preserves the legacy
		// "first invocation is all-or-nothing" guarantee — there is never a
		// state where the Define exists in the journal but the matching first
		// Invocation does not (or vice versa).
		//
		// Per backend:
		//   - SQL Server: a single SqlCommand with two INSERT statements
		//     separated by `;` — atomic by single-execution.
		//   - MySQL: BEGIN; INSERT define; INSERT invocation; COMMIT;
		//   - InMemory: two events.Add calls under the same storage lock —
		//     trivially atomic.
		//   - FileSystem: two journalWriter.AppendRecord calls under the
		//     same writeLock acquire. Honest limit (firmado Q-fs-atomicity =
		//     α): a crash between the first and second flush leaves a Define
		//     orphan, same atomicity level as the legacy WriteNewActionEntry
		//     (which also did two writes — _ACTION lateral + journal). If
		//     production surfaces a real problem here, a pair-marker on the
		//     Define record header can mitigate it; out-of-scope for Phase 4.
		//
		// Subsequent invocations (cache hit on the actor) keep using
		// WriteInvocationEntry directly. The standalone WriteDefineEntry
		// stays for replication paths where a follower applies a Define
		// record received separately from its first Invocation.
		// ============================================================
		protected internal virtual void WriteDefineWithFirstInvocation(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			throw new NotImplementedException($"{this.GetType().Name} has not adopted WriteDefineWithFirstInvocation yet (Phase 4 of the Action refactor). Either adopt it in this backend or keep the caller on the legacy WriteNewActionEntry path until cutover.");
		}

		protected internal virtual Task WriteDefineWithFirstInvocationAsync(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			throw new NotImplementedException($"{this.GetType().Name} has not adopted WriteDefineWithFirstInvocationAsync yet (Phase 4 of the Action refactor). Either adopt it in this backend or keep the caller on the legacy WriteNewActionEntryAsync path until cutover.");
		}


		protected internal abstract long GetLastProcessedEntryId(int followerId);
		protected internal abstract void SaveLastProcessedEntryId(int followerId, long entryId);

		// Methods for Reactions.
		protected internal abstract long GetOrCreateReactionId(string formattedReaction);

		// Returns the (detected, confirmed) tuple in a single round-trip to the DB to minimize latency.
		// detected: match detected and saved in a transactional commit (elision + checkpoint).
		// confirmed: action executed successfully (PerformCommand completed).
		protected internal abstract (long detected, long confirmed) GetReactionCheckpoint(long reactionId, int seekLevel);

		// Save only the Confirmed checkpoint after PerformCommand executes successfully.
		// Detected was already persisted during MarkEventsAsElidedWithCheckpoint.
		protected internal abstract void SaveReactionConfirmedCheckpoint(long reactionId, int seekLevel, long entryId);

		// DEPRECATED: Usar GetReactionCheckpoint en su lugar (retorna tupla)
		protected internal abstract long GetReactionLastProcessedEntryId(long reactionId, int pattern);
		// DEPRECATED: MarkEventsAsElidedWithCheckpoint ahora guarda Detected, usar SaveReactionConfirmedCheckpoint para Confirmed
		protected internal abstract void SaveReactionLastProcessedEntryId(long reactionId, int pattern, long entryId);

		// MarkEventsAsElidedWithCheckpoint ahora guarda SOLO Detected (no Confirmed)
		// Confirmed se guarda posteriormente con SaveReactionConfirmedCheckpoint tras ejecutar PerformCommand
		protected internal abstract bool MarkEventsAsElidedWithCheckpoint(Follower.CheckpointCommit commit);

		// ===== RESUME OPTIMIZATION: dos cursores globales por reaction (rediseño de checkpoint,
		// paso 2). Detalle + matriz: notes/reactions-checkpoint-policy.md. =====
		//
		// Para reactions de cobertura (ForEach) el checkpoint escalar per-seek se descarta: los
		// matches concurrentes multi-ancla cierran fuera de orden y no tienen orden total. El
		// resume se gobierna por dos cursores monotonos por reaction:
		//   - high-water    = max entryId escaneado por la reaction.
		//   - closedFrontier = mayor entryId por debajo del cual TODA ancla de cobertura cerro.
		// En el proximo Execute se re-lee desde closedFrontier en vez de génesis.
		//
		// Default (0,0) => "sin frontera conocida" => resume desde génesis (correcto, sin
		// optimizar). Un backend que no adopte esto degrada al comportamiento previo. virtual
		// (no abstract) para no romper backends que aun no lo implementan.
		protected internal virtual (long highWater, long closedFrontier) GetReactionFrontier(long reactionId)
		{
			return (0, 0);
		}

		protected internal virtual void SaveReactionFrontier(long reactionId, long highWater, long closedFrontier)
		{
		}

		// ===== RESUME OPTIMIZATION: snapshot de matches abiertos (paso 4) =====
		// Cold-start de un consumidor-puro de replicacion (Svix no rebobina): en restart se
		// restauran los matches de cobertura abiertos desde el snapshot y se resume en el frente,
		// sin re-leer el journal. Blob opaco serializado por CoverageSnapshotCodec. Default null
		// => sin snapshot persistido => el caller cae al re-read desde closedFrontier.
		protected internal virtual string GetReactionMatchSnapshot(long reactionId)
		{
			return null;
		}

		protected internal virtual void SaveReactionMatchSnapshot(long reactionId, string snapshot)
		{
		}


		// Paper 5 / Materialize v2 — Fase 2. Wire verb (a) EnviameDesde(afterEntryId).
		// Lee records RAW del journal desde afterEntryId (exclusivo) hasta el head
		// actual, en orden ascendente por EntryId. Sin filtrar por Skip column ni
		// EventElision — Capa 1 del wire (records solos). El destination side decide
		// si combina con (c)+(d) para obtener Capa 2 (derived state). Cada record se
		// proyecta a MaterializationRecord (struct publico inmutable).
		//
		// Snapshot semantics: lee hasta el head al momento de invocacion. Reads
		// concurrentes con writes en progreso solo ven hasta el ultimo entryId
		// committed at read-start (journal append-only por construccion).
		protected internal virtual void ReadRecordsAfter(long afterEntryId, List<MaterializationRecord> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadRecordsAfter yet (Materialize v2 Fase 2).");
		}

		protected internal virtual Task ReadRecordsAfterAsync(long afterEntryId, List<MaterializationRecord> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadRecordsAfterAsync yet (Materialize v2 Fase 2).");
		}

		// Paper 5 / Materialize v2 — Fase 3. Wire verb (c) DameCheckpointsHasta:
		// snapshot atomic del reaction registry. Cada entry es (reactionId,
		// formattedReaction) — el destination usa esto para mapear sus reactions
		// locales al mismo reactionId que el primary.
		protected internal virtual void ReadReactionRegistry(List<MaterializationReactionDefinition> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadReactionRegistry yet (Materialize v2 Fase 3).");
		}

		// Wire verb (c) DameCheckpointsHasta: snapshot atomic de los checkpoints
		// de todas las reactions. Ship AS-IS (decision D1 firmada 2026-05-13) —
		// sin clipping ni filtering por watermark, el matcher en el destination
		// controla via GetMinimum + IsCheckpointGreater.
		protected internal virtual void ReadReactionCheckpoints(List<MaterializationReactionCheckpoint> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadReactionCheckpoints yet (Materialize v2 Fase 3).");
		}

		protected internal abstract MemoryStream Archive(DateTime fechaInicio, DateTime fechaFin);
		protected internal abstract IEnumerable<string> ListActorNames(string name);
		protected internal abstract void Trim(DateTime trimmedDown);

		// Distill: materializa fisicamente las elisiones del journal (los records marcados
		// como skip por reactions con MarkAsSkip). Reemplaza al viejo PerformTrim de Actor V1
		// con semantica nueva: trabaja sobre la elision logica, no por fecha. Trim(DateTime)
		// sigue existiendo para preservacion por fecha; ambos coexisten porque cumplen
		// proposito distinto.
		//
		// Invariante: el record con la mayor EntryId (el "ultimo registro" al momento del
		// barrido final) NUNCA se elimina fisicamente, aunque su elision logica lo marque.
		// La elision queda postergada hasta que un Distill posterior, tras llegar nuevos
		// eventos, lo encuentre como no-ultimo. Esto protege la trazabilidad del
		// LastWrittenEntryId implicito en el journal.
		//
		// Etapa 1 (operacional, sincrono): toma writeLock al inicio, hace todo bajo el lock,
		// libera al final. Productores quedan brevemente bloqueados. Etapa 3 introduce
		// hot-trim con tail-chasing para que productores no se bloqueen.
		protected internal virtual void Distill()
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted Distill yet.");
		}

		internal abstract void ChangePrimaryKey();

		private const double CANTIDAD_DE_DIAS_CON_APORTE_MINIMO = 3;


		protected static int CalcularMaximoDeActoresACargar(IEnumerable<int> acumuladoPorDia, double minimumContributionPercent)
		{
			if (acumuladoPorDia == null) throw new ArgumentException(nameof(acumuladoPorDia));
			if (minimumContributionPercent < 0 && minimumContributionPercent > 100) throw new ArgumentException(nameof(minimumContributionPercent));

			var actoresAcumulados = 0;
			var acumuladoDeDiasConAporteMinimo = 0;
			double porcentajeTotalDelDia = 0;


			foreach (var acumuladoDiaActual in acumuladoPorDia)
			{
				actoresAcumulados += acumuladoDiaActual;
				porcentajeTotalDelDia = ((double)acumuladoDiaActual / actoresAcumulados) * 100;

				/*
				acumuladoDeDiasConAporteMinimo = (porcentajeTotalDelDia < PORCENTAJE_MINIMO_DE_APORTE) ? acumuladoDeDiasConAporteMinimo + 1 : 0
				*/

				if (porcentajeTotalDelDia < minimumContributionPercent)
				{
					acumuladoDeDiasConAporteMinimo++;
				}
				else
				{
					acumuladoDeDiasConAporteMinimo = 0;
				}

				if (acumuladoDeDiasConAporteMinimo >= CANTIDAD_DE_DIAS_CON_APORTE_MINIMO)
				{
					break;
				}
			}
			return actoresAcumulados;
		}

		protected void SaveTempFileToZip(ZipArchive archive, string fileName)
		{
			try
			{
				ZipArchiveEntry entry = archive.CreateEntry(fileName);
				using (Stream stream = new MemoryStream(msDairyPeriodRangeToExport.GetBuffer()))
				{
					using (Stream entryStream = entry.Open())
					{
						stream.CopyTo(entryStream);
					}
				}
			}
			catch (Exception)
			{
			}
		}

	}

}
