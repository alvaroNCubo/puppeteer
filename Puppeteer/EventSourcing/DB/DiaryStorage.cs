using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	// A journal record paired with its wire-equivalent bytes, as produced by
	// BinaryEventCodec.Encode* — the same encoding OnRecordWritten hands to a
	// subscriber on the live write path. Public because the replication catch-up
	// runs in the hosting assembly (Choreography), which has no visibility of the
	// codec and must receive the bytes already encoded.
	//
	// Exists so a replication catch-up can source the gap it owes a peer from the
	// JOURNAL. The alternative — a per-process buffer fed by the live write and
	// live-cue paths — cannot answer for entries written in an earlier process
	// life, so it under-serves precisely the late-join it exists to support.
	public readonly struct JournalWireRecord
	{
		public long EntryId { get; }
		public byte[] Record { get; }

		internal JournalWireRecord(long entryId, byte[] record)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(record);
			this.EntryId = entryId;
			this.Record = record;
		}
	}

	internal abstract class DiaryStorage
	{
		protected readonly string ConnectionString;
		protected readonly string Name;

		// The store's OWN client — the actor it was built for. Readonly on purpose:
		// a replay's destination is state of the call that opened it, never state of
		// the store, so nothing may retarget the store even briefly. Readers that
		// deliver somewhere else (a Reaction catching up through its ActorReactions
		// wrapper) pass their client down the read path instead.
		protected readonly IActorEventJournalClient EventJournalClient;
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
		//
		// Encoded in the store's own wire encoding rather than unconditionally in the
		// clear: these bytes are what leaves the store, so a store that protects its
		// payloads must not hand out a plainer copy through this door than the one it
		// keeps. For the typed-column backends the properties are the defaults and the
		// result is byte-identical to what they produced before.
		protected byte[] EncodeScriptRecord(long entryId, string script, DateTime now, string exposeData)
		{
			return FileSystem.BinaryEventCodec.EncodeScriptEvent(entryId, now, script,
				WireCompression, WireEncryption, WireEncryptionKey, exposeData);
		}

		protected byte[] EncodeInvocationRecord(int actionId, long entryId, DateTime now, string arguments, string exposeData)
		{
			return FileSystem.BinaryEventCodec.EncodeActionEvent(entryId, now, actionId, arguments,
				WireCompression, WireEncryption, WireEncryptionKey, exposeData);
		}

		protected byte[] EncodeDefineRecord(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData)
		{
			return FileSystem.BinaryEventCodec.EncodeDefineEvent(entryId, now, actionId, defineStatementText,
				WireCompression, WireEncryption, WireEncryptionKey, exposeData);
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

		// Structural identity for the "a relocation target must differ from the actor's
		// own store" guard (Reactions.RelocatedTo / RelocatedInMemory). Two stores are
		// "the same" when they are the same backend for the same actor pointed at the
		// same connection — i.e. they would read/write the same physical journal +
		// checkpoints. Best-effort on the connection string (compared verbatim); it
		// catches the accidental "relocate onto myself" case without pretending to
		// canonicalize every backend's connection-string dialect.
		internal bool IsSameStoreAs(DiaryStorage other)
		{
			if (other == null) return false;
			if (ReferenceEquals(this, other)) return true;

			return GetType() == other.GetType()
				&& string.Equals(Name, other.Name, StringComparison.Ordinal)
				&& string.Equals(ConnectionString, other.ConnectionString, StringComparison.Ordinal);
		}

		// Forward-only read of the journal, delivering every record to `client`.
		//
		// The client is a PARAMETER rather than the store's field because a store is a
		// journal reader, not a routing table: one actor owns one store, but N readers
		// can walk that one append-only journal at the same time (the actor's own
		// rehydration, plus a catch-up per Reaction). Each backend must dispatch every
		// row to the client it was handed, so overlapping readers never observe each
		// other's destination.
		protected internal abstract long RehydrateFromEvent(IActorEventJournalClient client, long afterEntryId, bool includeExposeData = false);
		protected internal abstract Task<long> RehydrateFromEventAsync(long afterEntryId, bool includeExposeData = false);

		// Read on behalf of the actor that owns this store — the default destination.
		protected internal long RehydrateFromEvent(long afterEntryId, bool includeExposeData = false)
		{
			return RehydrateFromEvent(EventJournalClient, afterEntryId, includeExposeData);
		}

		// The payload encoding this journal's records are written in, and the key that
		// opens them. A record that leaves this store — a replication cue, a catch-up —
		// is encoded exactly this way, so what travels matches what the actor's own
		// replay expects and no path silently downgrades a payload the store chose to
		// protect. Backends that keep typed columns rather than encoded payloads answer
		// with the defaults, which is what they already synthesized.
		protected internal virtual FileSystem.PayloadCompression WireCompression => FileSystem.PayloadCompression.None;
		protected internal virtual FileSystem.EncryptionMode WireEncryption => FileSystem.EncryptionMode.None;
		protected internal virtual byte[] WireEncryptionKey => null;

		// Greatest entry id this journal holds, 0 when it holds nothing.
		//
		// The replication of a buffered journal needs it: the remote write commits BEFORE
		// the replication watermark reaches disk, so the watermark is a hint that can
		// legitimately lag behind what the journal already contains, while the journal's
		// own head cannot. Abstract rather than defaulted on purpose — a backend that
		// answered a conservative 0 would make the replay re-send committed records, which
		// is fatal where the entry id is a primary key and silent divergence where it is not.
		protected internal abstract long LastJournaledEntryId();

		// Reactions replay against a journal client of their own (the ActorReactions
		// wrapper) instead of the storage's. That client now travels as an argument of
		// the read above; there is no store state to swap and therefore nothing to
		// restore, which is what lets two Reactions catch up on one actor at once.
		// Forward-only: the append-only journal has a single natural reading order.


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

		// Journal-outbox emit (.Outbox.Emit). The diary's outbox row table. Like
		// EventElisionStorage / EventMaterializationStorage it is owned per-actor.
		// The framework's default durable outbox is a persistent local queue
		// (OutboxStorageFileSystem) — the same local-buffer strategy the diary uses
		// for perform-command writes — so the outbox works on ANY diary backend, not
		// just in-memory. Each backend/wrapper assigns it: the FileSystem and
		// in-memory backends self-wire it in their constructor; for a SQL / plain-text
		// backend the Diary facade injects the local-buffer queue when a
		// localBufferPath is configured (SetOutboxStorage). An author whose
		// architecture cannot host that queue can inject their own OutboxStorage
		// through the same seam.
		protected OutboxStorage outboxStorage;
		internal OutboxStorage OutboxStorage => outboxStorage;

		// Serializes the guard+record+advance critical section of the default
		// RecordOutboxWithCheckpoint within this process. Cross-process safety
		// (red-black takeover) does not rely on it: the monotonic detected-cursor
		// guard plus the idempotency-key uniqueness make a concurrent re-detection on
		// another pod a no-op regardless of this lock.
		private readonly object outboxCheckpointLock = new object();

		// Injection seam for the outbox row store. Used by the Diary facade to wire
		// the persistent local-buffer queue into a SQL / plain-text backend, and by
		// an author supplying their own OutboxStorage.
		internal void SetOutboxStorage(OutboxStorage storage)
		{
			ArgumentNullException.ThrowIfNull(storage);
			this.outboxStorage = storage;
		}

		// Record an outgoing message AND advance the reaction cursor — the
		// exactly-once-recording primitive. Backend-agnostic default: it drives the
		// per-backend checkpoint store through the abstract GetReactionCheckpoint /
		// SaveReactionLastProcessedEntryId primitives and inserts the row into the
		// pluggable OutboxStorage, so every backend gets a working outbox without a
		// per-backend override. Mirrors MarkEventsAsElidedWithCheckpoint: monotonic-
		// compare the commit's vector against the persisted detected cursor; if not
		// greater, no-op (another pod already recorded this match) and return false.
		//
		// The row is inserted BEFORE the cursor advances. When the row store and the
		// checkpoint store are the same medium (in-memory) this is a single atomic
		// write; when they are distinct media (e.g. a SQL checkpoint + a local-queue
		// row) the two writes are not one transaction, but the ORDER makes a crash
		// between them safe: a recorded-but-not-yet-advanced row is re-detected on
		// replay, TryInsert is an idempotent no-op on the key, and the cursor then
		// advances. The reverse order (advance then insert) could lose the message;
		// this order cannot. Backends that CAN make the two writes one transaction
		// (in-memory) override this for the stronger single-write guarantee.
		//
		// virtual so a backend can substitute a truly atomic implementation.
		protected internal virtual bool RecordOutboxWithCheckpoint(Follower.OutboxCommit commit)
		{
			ArgumentNullException.ThrowIfNull(commit);
			if (outboxStorage == null)
				throw new LanguageException(
					$"{GetType().Name} has no OutboxStorage configured, so `.Outbox.Emit(...)` cannot record. " +
					"The framework's default durable outbox is the persistent local queue: use the FileSystem or " +
					"IN_MEMORY backend, add 'localBufferPath=<path>' to the connection string to enable the local " +
					"buffer, or inject your own OutboxStorage. See notes/reactions-outbox-emit.md.");

			long reactionId = commit.ReactionId;
			Follower.CheckpointVector newCheckpoint = commit.CheckpointVector;

			lock (outboxCheckpointLock)
			{
				// Monotonic detected-cursor guard (identical to the elide path): the
				// first seek level where new != current decides. Not greater => a peer
				// pod already recorded this match; no-op.
				bool isGreater = false;
				for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
				{
					long newDetected = newCheckpoint.Get(seekLevel);
					var (currentDetected, _) = GetReactionCheckpoint(reactionId, seekLevel);

					if (newDetected > currentDetected) { isGreater = true; break; }
					if (newDetected < currentDetected) { isGreater = false; break; }
				}

				if (!isGreater)
					return false;

				var record = new OutboxRecord(
					reactionId: commit.ReactionId,
					anchorEntryId: commit.AnchorEntryId,
					destination: commit.Destination,
					payload: commit.Payload,
					idempotencyKey: commit.IdempotencyKey,
					recordedAt: commit.Timestamp);

				// Insert BEFORE advancing the cursor (see the crash-safety note above).
				// Idempotent on the key: a re-detected match does not create a second row.
				outboxStorage.TryInsert(record);

				// Advance BOTH cursors (detected and confirmed) to newDetected — the
				// recording IS the action, so there is no later SaveReactionConfirmedCheckpoint
				// (unlike the elide path). SaveReactionLastProcessedEntryId sets both.
				for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
				{
					long newDetected = newCheckpoint.Get(seekLevel);
					if (newDetected > 0)
						SaveReactionLastProcessedEntryId(reactionId, seekLevel, newDetected);
				}

				return true;
			}
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

		// Same range as ReadRecordsAfter (afterEntryId exclusive, ascending, no Skip /
		// EventElision filtering), projected to the wire encoding a replication cue
		// carries. Backend-agnostic by construction: it composes the per-backend
		// ReadRecordsAfter with the same Encode* helpers the typed-column backends
		// already use to synthesize OnRecordWritten's bytes, so every backend that can
		// answer ReadRecordsAfter can serve a catch-up without its own override.
		//
		// Encoded in the store's own wire encoding, so a catch-up delivers a record in
		// the same shape the live write callback does. The receiving side therefore
		// needs whatever the sending store needed — for an encrypted journal, the same
		// key — and no path here turns a protected payload into a plain one on its way
		// out. A record physically removed by Distill is absent here, as it is from the
		// journal itself; the caller decides what an absent entry in the range means.
		internal void ReadWireRecordsAfter(long afterEntryId, List<JournalWireRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");

			result.Clear();

			var records = new List<MaterializationRecord>();
			ReadRecordsAfter(afterEntryId, records);

			foreach (MaterializationRecord record in records)
			{
				byte[] wire;
				switch (record.Kind)
				{
					case MaterializationRecordKind.Script:
						wire = EncodeScriptRecord(record.EntryId, record.Script, record.OccurredAt, record.ExposeData);
						break;
					case MaterializationRecordKind.Invocation:
						wire = EncodeInvocationRecord(record.ActionId, record.EntryId, record.OccurredAt, record.Arguments, record.ExposeData);
						break;
					case MaterializationRecordKind.Define:
						wire = EncodeDefineRecord(record.ActionId, record.DefineStatementText, record.EntryId, record.OccurredAt, record.ExposeData);
						break;
					default:
						throw new LanguageException($"Journal record kind {record.Kind} has no wire encoding.");
				}

				result.Add(new JournalWireRecord(record.EntryId, wire));
			}
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

		protected internal abstract MemoryStream Archive(DateTime startDate, DateTime endDate);
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

		private const double DAYS_WITH_MINIMUM_CONTRIBUTION = 3;


		protected static int CalculateMaxActorsToLoad(IEnumerable<int> accumulatedPerDay, double minimumContributionPercent)
		{
			if (accumulatedPerDay == null) throw new ArgumentException(nameof(accumulatedPerDay));
			if (minimumContributionPercent < 0 && minimumContributionPercent > 100) throw new ArgumentException(nameof(minimumContributionPercent));

			var accumulatedActors = 0;
			var daysWithMinimumContribution = 0;
			double dayTotalPercent = 0;


			foreach (var currentDayAccumulated in accumulatedPerDay)
			{
				accumulatedActors += currentDayAccumulated;
				dayTotalPercent = ((double)currentDayAccumulated / accumulatedActors) * 100;

				/*
				daysWithMinimumContribution = (dayTotalPercent < minimumContributionPercent) ? daysWithMinimumContribution + 1 : 0
				*/

				if (dayTotalPercent < minimumContributionPercent)
				{
					daysWithMinimumContribution++;
				}
				else
				{
					daysWithMinimumContribution = 0;
				}

				if (daysWithMinimumContribution >= DAYS_WITH_MINIMUM_CONTRIBUTION)
				{
					break;
				}
			}
			return accumulatedActors;
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
