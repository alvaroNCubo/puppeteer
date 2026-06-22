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

		// Per-actor shortcut for the error logging call sites in the backends
		// and in wrappers that receive a DiaryStorage (e.g. ReplicationAgent).
		// The sink arrives via Actor.Logger -> ActorHandler.Logger -> IActorEventJournalClient.Logger.
		// Replaces the old Loggers.GetInstance().Db (process-wide singleton) in F4
		// of the logger refactor.
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

		// Paper 5 / claim 4: storage for Materialize markers. Per-actor-by-construction
		// (cross-ref project_actor_per_db_principle.md). Each backend instantiates it in its
		// constructor — parallel to the EventElisionStorage pattern.
		protected EventMaterializationStorage eventMaterializationStorage;
		internal EventMaterializationStorage EventMaterializationStorage => eventMaterializationStorage;

		// Materialize v2 / Phase 0 (signed D1 2026-05-13). One-row-per-destination registry
		// that enables the Materialize-then-Distill invariant of Phase 1. It lives in the same
		// DB as the actor (per-actor-by-construction). The v1 EventMaterializationStorage
		// above stays alive as a queue of markers for push delivery; this storage adds
		// the presence-contract layer with a monotonic per-destination watermark.
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
		// Phase 4 SPLIT MODEL (signed after review 2026-05-09):
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
		// can adopt the new path incrementally (signed: InMemory → FileSystem →
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
		// (signed after review 2026-05-09).
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
		//     same writeLock acquire. Honest limit (signed Q-fs-atomicity =
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

		// DEPRECATED: Use GetReactionCheckpoint instead (returns a tuple)
		protected internal abstract long GetReactionLastProcessedEntryId(long reactionId, int pattern);
		// DEPRECATED: MarkEventsAsElidedWithCheckpoint now saves Detected, use SaveReactionConfirmedCheckpoint for Confirmed
		protected internal abstract void SaveReactionLastProcessedEntryId(long reactionId, int pattern, long entryId);

		// MarkEventsAsElidedWithCheckpoint now saves ONLY Detected (not Confirmed)
		// Confirmed is saved afterwards with SaveReactionConfirmedCheckpoint after executing PerformCommand
		protected internal abstract bool MarkEventsAsElidedWithCheckpoint(Follower.CheckpointCommit commit);

		// Journal-outbox emit (.Outbox.Emit). The diary's outbox side table. Like
		// EventElisionStorage / EventMaterializationStorage it is owned per-actor by
		// the concrete storage; null on backends that have not adopted the outbox.
		protected OutboxStorage outboxStorage;
		internal OutboxStorage OutboxStorage => outboxStorage;

		// Record an outgoing message AND advance the reaction cursor in ONE store
		// write — the exactly-once-recording primitive. Mirrors
		// MarkEventsAsElidedWithCheckpoint: monotonic-compare the commit's vector
		// against the persisted detected cursor; if not greater, no-op (another pod
		// already recorded this match) and return false; otherwise insert the
		// outbox row (idempotent on the key) and advance the cursor, atomically.
		// virtual (not abstract) so backends adopt it incrementally — the default
		// signals non-adoption rather than silently dropping the message.
		protected internal virtual bool RecordOutboxWithCheckpoint(Follower.OutboxCommit commit)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted RecordOutboxWithCheckpoint yet (journal-outbox emit prototype, IN_MEMORY only).");
		}

		// ===== RESUME OPTIMIZATION: two global cursors per reaction (checkpoint redesign,
		// step 2). Detail + matrix: notes/reactions-checkpoint-policy.md. =====
		//
		// For coverage reactions (ForEach) the per-seek scalar checkpoint is discarded: the
		// concurrent multi-anchor matches close out of order and have no total order. The
		// resume is governed by two monotonic cursors per reaction:
		//   - high-water    = max entryId scanned by the reaction.
		//   - closedFrontier = greatest entryId below which EVERY coverage anchor closed.
		// On the next Execute it re-reads from closedFrontier instead of genesis.
		//
		// Default (0,0) => "no known frontier" => resume from genesis (correct, not
		// optimized). A backend that does not adopt this degrades to the previous behavior. virtual
		// (not abstract) so as not to break backends that do not implement it yet.
		protected internal virtual (long highWater, long closedFrontier) GetReactionFrontier(long reactionId)
		{
			return (0, 0);
		}

		protected internal virtual void SaveReactionFrontier(long reactionId, long highWater, long closedFrontier)
		{
		}

		// ===== RESUME OPTIMIZATION: snapshot of open matches (step 4) =====
		// Cold-start of a pure replication consumer (Svix does not rewind): on restart the
		// open coverage matches are restored from the snapshot and resumed at the front,
		// without re-reading the journal. Opaque blob serialized by CoverageSnapshotCodec. Default null
		// => no persisted snapshot => the caller falls back to the re-read from closedFrontier.
		protected internal virtual string GetReactionMatchSnapshot(long reactionId)
		{
			return null;
		}

		protected internal virtual void SaveReactionMatchSnapshot(long reactionId, string snapshot)
		{
		}


		// Paper 5 / Materialize v2 — Phase 2. Wire verb (a) EnviameDesde(afterEntryId).
		// Reads RAW records from the journal from afterEntryId (exclusive) up to the current
		// head, in ascending EntryId order. Without filtering by Skip column or
		// EventElision — Layer 1 of the wire (records alone). The destination side decides
		// whether to combine it with (c)+(d) to obtain Layer 2 (derived state). Each record is
		// projected to MaterializationRecord (immutable public struct).
		//
		// Snapshot semantics: reads up to the head at the moment of invocation. Reads
		// concurrent with writes in progress only see up to the last entryId
		// committed at read-start (journal append-only by construction).
		protected internal virtual void ReadRecordsAfter(long afterEntryId, List<MaterializationRecord> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadRecordsAfter yet (Materialize v2 Fase 2).");
		}

		protected internal virtual Task ReadRecordsAfterAsync(long afterEntryId, List<MaterializationRecord> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadRecordsAfterAsync yet (Materialize v2 Fase 2).");
		}

		// Paper 5 / Materialize v2 — Phase 3. Wire verb (c) DameCheckpointsHasta:
		// atomic snapshot of the reaction registry. Each entry is (reactionId,
		// formattedReaction) — the destination uses this to map its local
		// reactions to the same reactionId as the primary.
		protected internal virtual void ReadReactionRegistry(List<MaterializationReactionDefinition> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadReactionRegistry yet (Materialize v2 Fase 3).");
		}

		// Wire verb (c) DameCheckpointsHasta: atomic snapshot of the checkpoints
		// of all reactions. Ship AS-IS (decision D1 signed 2026-05-13) —
		// without clipping or filtering by watermark, the matcher in the destination
		// controls via GetMinimum + IsCheckpointGreater.
		protected internal virtual void ReadReactionCheckpoints(List<MaterializationReactionCheckpoint> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadReactionCheckpoints yet (Materialize v2 Fase 3).");
		}

		protected internal abstract MemoryStream Archive(DateTime fechaInicio, DateTime fechaFin);
		protected internal abstract IEnumerable<string> ListActorNames(string name);
		protected internal abstract void Trim(DateTime trimmedDown);

		// Distill: physically materializes the journal elisions (the records marked
		// as skip by reactions with MarkAsSkip). Replaces the old PerformTrim of Actor V1
		// with new semantics: it works over the logical elision, not by date. Trim(DateTime)
		// still exists for date-based preservation; both coexist because they serve a
		// distinct purpose.
		//
		// Invariant: the record with the greatest EntryId (the "last record" at the moment of
		// the final sweep) is NEVER physically deleted, even if its logical elision marks it.
		// The elision is deferred until a later Distill, after new events arrive,
		// finds it as non-last. This protects the traceability of the
		// LastWrittenEntryId implicit in the journal.
		//
		// Stage 1 (operational, synchronous): takes the writeLock at the start, does everything under the lock,
		// releases at the end. Producers are briefly blocked. Stage 3 introduces
		// hot-trim with tail-chasing so that producers are not blocked.
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
