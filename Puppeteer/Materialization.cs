using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.DB;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Phase 0 (signed D1 2026-05-13). Administrative
	// sub-namespace of the actor for registering destinations that will receive
	// state transfer. Once a destination is registered, the actor assumes the
	// Materialize-then-Distill contract: Phase 1 makes Distill fail if the
	// destination has not expressly confirmed.
	//
	// Sub-namespace naming chosen for DSL learnability — avoids verb-soup in
	// autocomplete when Confirm(...) / AsProgramMirror(...) are added in later
	// phases. Parallel to the actor.Reactions pattern.
	public class Materialization
	{
		private readonly ActorHandler handler;
		private MaterializationCheckpointStorage testStorageOverride;

		internal Materialization(ActorHandler handler)
		{
			ArgumentNullException.ThrowIfNull(handler);
			this.handler = handler;
		}

		// Test seam parallel to actor.Reactions.SetDairyStorage(...) — lets the
		// API be wired to an in-memory storage without going through EventSourcingStorage.
		// Production does not call this method; the ActorHandler calls it internally
		// when EventSourcingStorage configures the Diary.
		internal void SetCheckpointStorage(MaterializationCheckpointStorage storage)
		{
			ArgumentNullException.ThrowIfNull(storage);
			this.testStorageOverride = storage;
		}

		private MaterializationCheckpointStorage Storage
		{
			get
			{
				if (testStorageOverride != null) return testStorageOverride;
				MaterializationCheckpointStorage storage = handler.TryGetMaterializationCheckpointStorage();
				if (storage == null)
				{
					throw new LanguageException("Materialization requires EventSourcingStorage to be configured on the actor before registering destinations.");
				}
				return storage;
			}
		}

		// Registers a destination with initial watermark = the actor's current head
		// (decision D1 #12: a new registration = head at the moment, not genesis).
		// Idempotent: if the destination is already registered, returns false and
		// preserves the existing watermark (signed decision). The caller must do an
		// explicit Deregister + Register to reset.
		public bool Register(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			long head = handler.EntryId;
			return Storage.Register(destination, head, DateTime.Now);
		}

		// Unilateral (decision D1 #11): does not fail if the destination does not
		// exist. The other side (destination process) tolerates the orphaned state —
		// bidirectional handshakes hang on real networks.
		public bool Deregister(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			return Storage.Deregister(destination);
		}

		// Phase 1 — wire verb (b) ConfirmoUntil (decision D1 #14). Called by the
		// destination process (push from destination to actor, decision D1 #10) to
		// declare that it received events up to entryId inclusive. Max-monotonic: if
		// the watermark was already at N or more, no-op (returns false). If it advances,
		// returns true and enables Distill().Until(entryId) up to that point.
		//
		// Throws LanguageException if the destination is not registered — registration
		// is a prerequisite (decisions D1 #11/#12 on forward-fidelity from registration
		// time, not genesis).
		public bool ConfirmUntil(string destination, long entryId)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (entryId < 0) throw new LanguageException($"entryId {entryId} must be zero or greater.");
			return Storage.ConfirmUntil(destination, entryId, DateTime.Now);
		}

		// Immutable snapshot of the registry. Stable alphabetical order by
		// destination for determinism in tests.
		public IReadOnlyList<MaterializationCheckpointRow> List()
		{
			List<MaterializationCheckpointRow> result = new List<MaterializationCheckpointRow>();
			Storage.List(result);
			return result;
		}

		// Phase 2 — wire verb (a) EnviameDesde (Layer 1). Called by the destination
		// process via HTTP (in production) or directly (in-process tests) to request
		// the journal records from fromEntryId (exclusive) up to the current head.
		// Layer 1 = records only, raw (without filtering Skip). The destination decides
		// whether to combine with (c)+(d) in Phase 3 for Layer 2 (derived state).
		//
		// Validations:
		// - Destination must be registered (LanguageException if not).
		// - fromEntryId must be >= the destination's RegisteredAtEntryId (decision D1
		//   #12 forward-fidelity: a destination registered at EntryId R cannot request
		//   records earlier than R — that history is not the actor's responsibility
		//   toward it).
		//
		// Edge case: if primary distilled between registration and read (e.g. with
		// .Forced()), the distilled records will be physically absent and the result
		// will have a silent "hole". Responsibility of the operator who used .Forced()
		// (decision D1 #7).
		public IReadOnlyList<MaterializationRecord> ReadRecordsAfter(string destination, long fromEntryId)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (fromEntryId < 0) throw new LanguageException($"fromEntryId {fromEntryId} must be zero or greater.");

			MaterializationCheckpointStorage checkpointStorage = Storage;

			List<MaterializationCheckpointRow> registry = new List<MaterializationCheckpointRow>();
			checkpointStorage.List(registry);

			MaterializationCheckpointRow? row = null;
			foreach (var r in registry)
			{
				if (string.Equals(r.Destination, destination, StringComparison.Ordinal))
				{
					row = r;
					break;
				}
			}

			if (row == null)
			{
				throw new LanguageException($"Destination '{destination}' is not registered. Register it before calling ReadRecordsAfter.");
			}

			if (fromEntryId < row.Value.RegisteredAtEntryId)
			{
				throw new LanguageException(
					$"Destination '{destination}' is forward-fidelity from EntryId {row.Value.RegisteredAtEntryId}; cannot read records before its registration point (requested fromEntryId={fromEntryId}).");
			}

			DiaryStorage diaryStorage = handler.TryGetDiaryStorage();
			if (diaryStorage == null)
			{
				throw new LanguageException("Materialization requires EventSourcingStorage to be configured on the actor before calling ReadRecordsAfter.");
			}

			List<MaterializationRecord> result = new List<MaterializationRecord>();
			diaryStorage.ReadRecordsAfter(fromEntryId, result);
			return result;
		}

		// Phase 3 — wire verb (c) DameCheckpointsHasta (decision D1 #13). Atomic
		// snapshot of the reaction registry + checkpoints. AS-IS, without filtering by
		// watermark (the matcher on the destination side controls it via GetMinimum +
		// IsCheckpointGreater). Validation: destination registered.
		public MaterializationReactionsSnapshot ReadReactions(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			RequireRegistered(destination);

			DiaryStorage diaryStorage = handler.TryGetDiaryStorage();
			if (diaryStorage == null)
			{
				throw new LanguageException("Materialization requires EventSourcingStorage to be configured on the actor before calling ReadReactions.");
			}

			List<MaterializationReactionDefinition> reactions = new List<MaterializationReactionDefinition>();
			List<MaterializationReactionCheckpoint> checkpoints = new List<MaterializationReactionCheckpoint>();
			diaryStorage.ReadReactionRegistry(reactions);
			diaryStorage.ReadReactionCheckpoints(checkpoints);

			return new MaterializationReactionsSnapshot(reactions, checkpoints);
		}

		// Phase 3 — wire verb (d) DameElidedRange. Reads elision markers in the range
		// [fromEntryId, toEntryId] inclusive, ordered by (Timestamp, DiaryId).
		// Layer 2 = derived state — combined with (c) ReadReactions and (a) ReadRecordsAfter
		// it lets the destination rebuild its local EventElision without replaying the
		// pattern matcher.
		//
		// Validations:
		// - Destination registered.
		// - fromEntryId >= RegisteredAtEntryId (forward-fidelity D1 #12).
		// - fromEntryId <= toEntryId.
		public IReadOnlyList<MaterializationElisionMarker> ReadElidedRange(string destination, long fromEntryId, long toEntryId)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (fromEntryId <= 0) throw new LanguageException($"fromEntryId {fromEntryId} must be greater than zero.");
			if (toEntryId <= 0) throw new LanguageException($"toEntryId {toEntryId} must be greater than zero.");
			if (fromEntryId > toEntryId) throw new LanguageException($"fromEntryId {fromEntryId} must be less than or equal to toEntryId {toEntryId}.");

			MaterializationCheckpointRow row = RequireRegistered(destination);

			if (fromEntryId < row.RegisteredAtEntryId)
			{
				throw new LanguageException(
					$"Destination '{destination}' is forward-fidelity from EntryId {row.RegisteredAtEntryId}; cannot read elision markers before its registration point (requested fromEntryId={fromEntryId}).");
			}

			DiaryStorage diaryStorage = handler.TryGetDiaryStorage();
			if (diaryStorage == null)
			{
				throw new LanguageException("Materialization requires EventSourcingStorage to be configured on the actor before calling ReadElidedRange.");
			}

			List<MaterializationElisionMarker> result = new List<MaterializationElisionMarker>();
			diaryStorage.EventElisionStorage.ReadElisionMarkersInRange(fromEntryId, toEntryId, result);
			return result;
		}

		// Helper shared by ReadReactions/ReadElidedRange to verify that the
		// destination is registered. Returns the registry row so the caller can use
		// RegisteredAtEntryId if needed.
		private MaterializationCheckpointRow RequireRegistered(string destination)
		{
			MaterializationCheckpointStorage checkpointStorage = Storage;
			List<MaterializationCheckpointRow> registry = new List<MaterializationCheckpointRow>();
			checkpointStorage.List(registry);

			foreach (var r in registry)
			{
				if (string.Equals(r.Destination, destination, StringComparison.Ordinal))
				{
					return r;
				}
			}

			throw new LanguageException($"Destination '{destination}' is not registered.");
		}
	}
}
