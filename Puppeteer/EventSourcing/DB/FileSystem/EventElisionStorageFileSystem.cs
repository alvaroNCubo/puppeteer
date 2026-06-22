using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	// Cross-process safe elision store.
	//
	// The original bug: two processes sharing the FS (a local actor with Cue() and
	// a remote actor with Job()) performed read-modify-AtomicReplace over the same
	// elision.bin/skips.bin and the second AtomicReplace silently lost the
	// contributions of the first (classic lost-update).
	//
	// The fix: each MarkEventsAsElided writes a unique .batch (with a guid) into
	// pending/. Workers do not compete — each one writes to a distinct file.
	// Coordination happens only at consolidation (OS-level witness lock via
	// FileShare.None), which merges pending/*.batch into the canonical elision.bin
	// and skips.bin. The transactional commit is the rename .tmp -> .batch.
	internal sealed class EventElisionStorageFileSystem : EventElisionStorage
	{
		// Format of the consolidated elision.bin file.
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'E', (byte)'L' };
		private const ushort FORMAT_VERSION = 1;
		private const int HEADER_SIZE = 10;
		private const int RECORD_SIZE = 12;

		// Format of the .batch files in pending/.
		private static readonly byte[] BATCH_MAGIC = new byte[] { (byte)'P', (byte)'B', (byte)'A', (byte)'T' };
		private const ushort BATCH_FORMAT_VERSION = 1;
		private const int BATCH_HEADER_SIZE = 22; // 4 magic + 2 version + 4 reactionId + 8 ticks + 4 entryCount

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;
		private readonly SkipStore skipStore;
		private readonly PendingBatchDirectory pendingDir;
		private readonly ReaderWriterLockSlim elisionLock = new ReaderWriterLockSlim();

		// Unified in-memory view (consolidated + already-merged batches).
		// Refreshed lazily from disk when other processes published changes.
		private readonly Dictionary<int, HashSet<long>> eventsByReaction = new();
		private DateTime consolidatedMtimeUtc;
		private readonly HashSet<string> mergedBatchNames = new();

		internal EventElisionStorageFileSystem(
			IActorEventJournalClient eventJournalClient,
			string connectionString,
			string filePath,
			IAtomicFileOperation atomicOp,
			SkipStore skipStore,
			PendingBatchDirectory pendingDir)
			: base(eventJournalClient, connectionString)
		{
			if (filePath == null) throw new ArgumentNullException(nameof(filePath));
			if (atomicOp == null) throw new ArgumentNullException(nameof(atomicOp));
			if (skipStore == null) throw new ArgumentNullException(nameof(skipStore));
			if (pendingDir == null) throw new ArgumentNullException(nameof(pendingDir));

			this.filePath = filePath;
			this.atomicOp = atomicOp;
			this.skipStore = skipStore;
			this.pendingDir = pendingDir;

			LoadConsolidated();
			MergeAllPendingIntoMemory();
		}

		private void LoadConsolidated()
		{
			atomicOp.RecoverFromIncompleteOperation(filePath);

			eventsByReaction.Clear();
			mergedBatchNames.Clear();

			if (!File.Exists(filePath))
			{
				consolidatedMtimeUtc = DateTime.MinValue;
				return;
			}

			byte[] data = File.ReadAllBytes(filePath);
			consolidatedMtimeUtc = File.GetLastWriteTimeUtc(filePath);

			if (data.Length < HEADER_SIZE) return;

			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;

			for (int i = 0; i < count && offset + RECORD_SIZE <= data.Length; i++)
			{
				int reactionId = BitConverter.ToInt32(data, offset); offset += 4;
				long entryId = BitConverter.ToInt64(data, offset); offset += 8;

				if (!eventsByReaction.ContainsKey(reactionId))
					eventsByReaction[reactionId] = new HashSet<long>();
				eventsByReaction[reactionId].Add(entryId);
			}
		}

		// Merges ALL pending batches from disk into the in-memory state.
		// Called at startup and by RefreshIfStale when new files appear.
		private void MergeAllPendingIntoMemory()
		{
			foreach (var batchPath in pendingDir.ListBatches())
			{
				string name = Path.GetFileName(batchPath);
				if (mergedBatchNames.Contains(name)) continue;

				if (TryDecodeBatch(batchPath, out int rid, out _, out long[] ids))
				{
					if (!eventsByReaction.ContainsKey(rid))
						eventsByReaction[rid] = new HashSet<long>();
					foreach (var id in ids)
						eventsByReaction[rid].Add(id);
					mergedBatchNames.Add(name);
				}
			}
		}

		// Refreshes the in-memory view if the consolidated file on disk changed or
		// if new batches from other processes appeared. Cheap operation
		// (mtime + dir listing) when there are no changes.
		private void RefreshIfStale()
		{
			DateTime currentMtime = File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue;

			elisionLock.EnterUpgradeableReadLock();
			try
			{
				if (currentMtime != consolidatedMtimeUtc)
				{
					elisionLock.EnterWriteLock();
					try { LoadConsolidated(); }
					finally { elisionLock.ExitWriteLock(); }
				}

				bool needsBatchMerge = false;
				foreach (var batchPath in pendingDir.ListBatches())
				{
					if (!mergedBatchNames.Contains(Path.GetFileName(batchPath))) { needsBatchMerge = true; break; }
				}

				if (needsBatchMerge)
				{
					elisionLock.EnterWriteLock();
					try { MergeAllPendingIntoMemory(); }
					finally { elisionLock.ExitWriteLock(); }
				}
			}
			finally { elisionLock.ExitUpgradeableReadLock(); }
		}

		private static byte[] EncodeBatch(int reactionId, DateTime timestamp, long[] entryIds)
		{
			byte[] data = new byte[BATCH_HEADER_SIZE + entryIds.Length * 8];
			int offset = 0;

			Buffer.BlockCopy(BATCH_MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), BATCH_FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), reactionId); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 8), timestamp.Ticks); offset += 8;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), entryIds.Length); offset += 4;

			foreach (long id in entryIds)
			{
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), id);
				offset += 8;
			}

			return data;
		}

		private static bool TryDecodeBatch(string batchPath, out int reactionId, out DateTime timestamp, out long[] entryIds)
		{
			reactionId = 0;
			timestamp = DateTime.MinValue;
			entryIds = Array.Empty<long>();

			byte[] data;
			try { data = File.ReadAllBytes(batchPath); }
			catch (IOException) { return false; }
			catch (UnauthorizedAccessException) { return false; }

			if (data.Length < BATCH_HEADER_SIZE) return false;
			if (data[0] != BATCH_MAGIC[0] || data[1] != BATCH_MAGIC[1] || data[2] != BATCH_MAGIC[2] || data[3] != BATCH_MAGIC[3])
				return false;

			int offset = 4;
			ushort version = BitConverter.ToUInt16(data, offset); offset += 2;
			if (version != BATCH_FORMAT_VERSION) return false;

			reactionId = BitConverter.ToInt32(data, offset); offset += 4;
			long ticks = BitConverter.ToInt64(data, offset); offset += 8;
			timestamp = new DateTime(ticks, DateTimeKind.Utc);
			int entryCount = BitConverter.ToInt32(data, offset); offset += 4;

			if (entryCount < 0 || offset + entryCount * 8 > data.Length) return false;

			entryIds = new long[entryCount];
			for (int i = 0; i < entryCount; i++)
			{
				entryIds[i] = BitConverter.ToInt64(data, offset);
				offset += 8;
			}
			return true;
		}

		// Persists the eventsByReaction state to the canonical elision.bin via AtomicReplace.
		// Called only from Consolidate / RemoveElidedIds under elisionLock(write).
		private void SaveConsolidated()
		{
			int totalRecords = 0;
			foreach (var set in eventsByReaction.Values)
				totalRecords += set.Count;

			byte[] data = new byte[HEADER_SIZE + totalRecords * RECORD_SIZE];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), totalRecords); offset += 4;

			foreach (var kvp in eventsByReaction)
			{
				int reactionId = kvp.Key;
				foreach (long entryId in kvp.Value)
				{
					BitConverter.TryWriteBytes(data.AsSpan(offset, 4), reactionId); offset += 4;
					BitConverter.TryWriteBytes(data.AsSpan(offset, 8), entryId); offset += 8;
				}
			}

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);

			consolidatedMtimeUtc = File.GetLastWriteTimeUtc(filePath);
		}

		// Forces a drain of pending/ into the consolidated files, used by Distill
		// so the physicalization pass sees a stable snapshot. If another process is
		// already consolidating, we wait for it to finish by re-reading from disk
		// — *we* don't need to be the one who consolidates.
		internal void ForceConsolidate()
		{
			TryConsolidate(blockUntilDone: true);
		}

		// Attempts to consolidate pending/ -> elision.bin + skips.bin. Best-effort:
		// if another process holds the witness, it simply returns and another round
		// will pick up our batch (the .batch files we left behind remain durable).
		private void TryConsolidate(bool blockUntilDone)
		{
			using (var lease = pendingDir.TryAcquireWitness())
			{
				if (lease == null)
				{
					// Another process is consolidating now. If we are going to depend on the
					// result (Distill), we wait a bit and reload from disk. The actual
					// loading of our .batch files will be done by them.
					if (blockUntilDone) WaitForConsolidationToProgress();
					return;
				}

				// We are alone. Snapshot of batches to process.
				List<string> batches = pendingDir.ListBatches();
				if (batches.Count == 0) return;

				// We re-read consolidated.bin from disk — we don't trust in-memory
				// because other processes may have published changes between our
				// last refresh and this moment.
				var canonical = LoadConsolidatedFromDisk();
				var skipSet = new SortedSet<long>(skipStore.Load());

				foreach (var batchPath in batches)
				{
					if (!TryDecodeBatch(batchPath, out int rid, out _, out long[] ids))
						continue;

					if (!canonical.ContainsKey(rid))
						canonical[rid] = new HashSet<long>();
					foreach (var id in ids)
					{
						canonical[rid].Add(id);
						skipSet.Add(id);
					}
				}

				// Write order: skips.bin first, then elision.bin, then we delete the
				// consumed batches. If we crash in the middle:
				//   - before skips.bin: nothing changed, batches survive.
				//   - after skips.bin before elision.bin: skips has the new
				//     info, elision does not yet, batches survive -> the next
				//     consolidation reapplies them (idempotent).
				//   - after elision.bin before the delete: both consolidated,
				//     batches survive -> the next consolidation reapplies them
				//     (idempotent, same final state).
				skipStore.Save(skipSet);

				elisionLock.EnterWriteLock();
				try
				{
					eventsByReaction.Clear();
					foreach (var kvp in canonical)
						eventsByReaction[kvp.Key] = new HashSet<long>(kvp.Value);
					SaveConsolidated();
					mergedBatchNames.Clear();
				}
				finally { elisionLock.ExitWriteLock(); }

				pendingDir.DeleteBatches(batches);
			}
		}

		private Dictionary<int, HashSet<long>> LoadConsolidatedFromDisk()
		{
			var result = new Dictionary<int, HashSet<long>>();

			atomicOp.RecoverFromIncompleteOperation(filePath);
			if (!File.Exists(filePath)) return result;

			byte[] data = File.ReadAllBytes(filePath);
			if (data.Length < HEADER_SIZE) return result;
			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return result;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;
			for (int i = 0; i < count && offset + RECORD_SIZE <= data.Length; i++)
			{
				int reactionId = BitConverter.ToInt32(data, offset); offset += 4;
				long entryId = BitConverter.ToInt64(data, offset); offset += 8;

				if (!result.ContainsKey(reactionId))
					result[reactionId] = new HashSet<long>();
				result[reactionId].Add(entryId);
			}
			return result;
		}

		// Passive wait for an external consolidator. Used only from ForceConsolidate.
		// If our own batch is still in pending/ after the wait, we consolidate it
		// ourselves (on the next iteration the lease will already be free).
		private void WaitForConsolidationToProgress()
		{
			const int maxSpins = 20;
			const int spinMs = 50;
			for (int i = 0; i < maxSpins; i++)
			{
				Thread.Sleep(spinMs);
				if (pendingDir.CountBatches() == 0) return;

				// Retry acquiring the witness — if the other consolidator finished,
				// there may be new batches we wrote during its work.
				using (var lease = pendingDir.TryAcquireWitness())
				{
					if (lease != null)
					{
						// We release it and let TryConsolidate(false) run normally
						// — this avoids re-acquiring it inside the using.
					}
					else continue;
				}
				TryConsolidate(blockUntilDone: false);
				return;
			}
		}

		protected internal override bool IsEventElided(long dairyId)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");

			RefreshIfStale();
			var skipSet = skipStore.Load();
			if (skipSet.Contains(dairyId)) return true;

			// Also check pending batches that have not yet been merged into the skipStore.
			// This operation is necessary because skipStore.Load() only reads the
			// consolidated skips.bin, not the .batch files.
			elisionLock.EnterReadLock();
			try
			{
				foreach (var set in eventsByReaction.Values)
					if (set.Contains(dairyId)) return true;
			}
			finally { elisionLock.ExitReadLock(); }

			return false;
		}

		protected internal override Task<bool> IsEventElidedAsync(long dairyId)
		{
			return Task.FromResult(IsEventElided(dairyId));
		}

		protected internal override void MarkEventsAsElided(long[] dairyIds, int reactionId, DateTime timestamp)
		{
			if (dairyIds == null) throw new ArgumentNullException(nameof(dairyIds));
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			if (dairyIds.Length == 0) return;

			// Phase 1 (transactional commit): write a durable batch to pending/.
			// The rename .tmp -> .batch is the commit point — before it nothing is visible,
			// after it the batch survives any crash.
			byte[] payload = EncodeBatch(reactionId, timestamp, dairyIds);
			pendingDir.WriteBatch(payload);

			// Phase 2 (in-memory): this process sees the new markers immediately.
			// Other processes will see them when they consolidate or refresh their view.
			elisionLock.EnterWriteLock();
			try
			{
				if (!eventsByReaction.ContainsKey(reactionId))
					eventsByReaction[reactionId] = new HashSet<long>();
				foreach (long id in dairyIds)
					eventsByReaction[reactionId].Add(id);
			}
			finally { elisionLock.ExitWriteLock(); }

			// Phase 3 (opportunistic consolidation): try to drain pending/. If another
			// process already holds the witness, we do not wait — our .batch is
			// durable on disk and another round will pick it up. This avoids coupling
			// the Mark latency to consolidation.
			TryConsolidate(blockUntilDone: false);
		}

		protected internal override Task MarkEventsAsElidedAsync(long[] dairyIds, int reactionId, DateTime timestamp)
		{
			MarkEventsAsElided(dairyIds, reactionId, timestamp);
			return Task.CompletedTask;
		}

		protected internal override void GetElidedEventsByReaction(int reactionId, List<long> result)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			if (result == null) throw new ArgumentNullException(nameof(result));

			result.Clear();
			RefreshIfStale();

			elisionLock.EnterReadLock();
			try
			{
				if (eventsByReaction.TryGetValue(reactionId, out var set))
				{
					result.AddRange(set);
					result.Sort();
				}
			}
			finally { elisionLock.ExitReadLock(); }
		}

		protected internal override Task GetElidedEventsByReactionAsync(int reactionId, List<long> result)
		{
			GetElidedEventsByReaction(reactionId, result);
			return Task.CompletedTask;
		}

		protected internal override void GetElidedEventsInRange(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			if (result == null) throw new ArgumentNullException(nameof(result));
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();
			RefreshIfStale();

			var skipSet = skipStore.Load();
			foreach (long id in skipSet)
			{
				if (id >= fromDairyId && id <= toDairyId)
					result.Add(id);
			}

			// Also include what is in pending and has not yet reached skips.bin.
			elisionLock.EnterReadLock();
			try
			{
				foreach (var set in eventsByReaction.Values)
				{
					foreach (long id in set)
					{
						if (id >= fromDairyId && id <= toDairyId)
							result.Add(id);
					}
				}
			}
			finally { elisionLock.ExitReadLock(); }
		}

		protected internal override Task GetElidedEventsInRangeAsync(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			GetElidedEventsInRange(fromDairyId, toDairyId, result);
			return Task.CompletedTask;
		}

		// Materialize v2 / Phase 3 — wire verb (d) DameElidedRange.
		// HONEST GAP: the current binary format of the consolidated file (RECORD_SIZE = 12 =
		// reactionId + entryId) does NOT preserve a per-marker timestamp. We return
		// DateTime.MinValue as a sentinel. The marking order reduces to EntryId.
		// SQL backends preserve Timestamp via the EventElision.Timestamp column and
		// return the real value.
		protected internal override void ReadElisionMarkersInRange(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();
			RefreshIfStale();

			elisionLock.EnterReadLock();
			try
			{
				foreach (var kvp in eventsByReaction)
				{
					int reactionId = kvp.Key;
					foreach (long entryId in kvp.Value)
					{
						if (entryId >= fromDairyId && entryId <= toDairyId)
						{
							result.Add(new MaterializationElisionMarker(entryId, reactionId, DateTime.MinValue));
						}
					}
				}
			}
			finally { elisionLock.ExitReadLock(); }

			result.Sort((a, b) =>
			{
				int cmp = a.Timestamp.CompareTo(b.Timestamp);
				if (cmp != 0) return cmp;
				return a.EntryId.CompareTo(b.EntryId);
			});
		}

		protected internal override Task ReadElisionMarkersInRangeAsync(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			ReadElisionMarkersInRange(fromDairyId, toDairyId, result);
			return Task.CompletedTask;
		}

		// Count of genuinely new IDs relative to the current in-memory state.
		// Approximation: it does not force a refresh nor look at other processes'
		// pending files. It is sufficient because metadata.TotalNonSkippedCount is
		// recomputed every startup from skipStore.LoadCount() (DiaryStorageFileSystem.cs:101-108).
		internal int GetNewSkipCount(long[] dairyIds)
		{
			if (dairyIds == null) throw new ArgumentNullException(nameof(dairyIds));

			RefreshIfStale();
			var skipSet = skipStore.Load();

			elisionLock.EnterReadLock();
			try
			{
				int count = 0;
				foreach (long id in dairyIds)
				{
					if (skipSet.Contains(id)) continue;
					bool inMemory = false;
					foreach (var set in eventsByReaction.Values)
					{
						if (set.Contains(id)) { inMemory = true; break; }
					}
					if (!inMemory) count++;
				}
				return count;
			}
			finally { elisionLock.ExitReadLock(); }
		}

		// Used by Distill: after physically materializing the elisions, the removed
		// EntryIds stop being "logically elided" and become "absent". The caller
		// is DiaryStorageFileSystem.Distill, which already forced ForceConsolidate
		// beforehand so the operation sees a stable snapshot.
		internal void RemoveElidedIds(IEnumerable<long> dairyIds)
		{
			if (dairyIds == null) throw new ArgumentNullException(nameof(dairyIds));

			elisionLock.EnterWriteLock();
			try
			{
				foreach (var id in dairyIds)
				{
					foreach (var set in eventsByReaction.Values)
					{
						set.Remove(id);
					}
				}
				SaveConsolidated();
			}
			finally { elisionLock.ExitWriteLock(); }
		}

		// Test seam: number of pending batches. Used by concurrency tests
		// to verify that consolidation drains the directory.
		internal int CountPendingBatches() => pendingDir.CountBatches();
	}
}
