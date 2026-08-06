using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class ReplicationAgent : IDisposable
	{
		private const int PERSIST_PROGRESS_INTERVAL = 100;
		private const int RETRY_DELAY_MS = 1000;

		private readonly DiaryStorage remoteStore;
		private readonly ReplicationProgress progress;
		private readonly ConcurrentQueue<(long entryId, byte[] record)> pendingRecords = new();
		// Phase 5 of the Action refactor: dropped pendingActions / replicatedActionIds /
		// pendingActionDefinitions. Define entries are journal records and replicate
		// as regular CueEvents per record (no separate ActionDefinition message).
		private readonly ManualResetEventSlim signal = new(false);
		private readonly ManualResetEventSlim drainComplete = new(true);

		private Thread replicationThread;
		private volatile bool stopping;
		private bool disposed;

		internal ReplicationAgent(DiaryStorage remoteStore, ReplicationProgress progress)
		{
			if (remoteStore == null) throw new ArgumentNullException(nameof(remoteStore));
			if (progress == null) throw new ArgumentNullException(nameof(progress));

			this.remoteStore = remoteStore;
			this.progress = progress;
		}

		internal void EnqueueRecord(long entryId, byte[] record)
		{
			if (record == null) throw new ArgumentNullException(nameof(record));

			drainComplete.Reset();
			pendingRecords.Enqueue((entryId, record));
			signal.Set();
		}

		// Phase 5 of the Action refactor: dropped EnqueueActionDefinition. Define
		// entries are journal records and flow through EnqueueRecord like any other.

		// paper05-lab5: catch-up phase observer — last entry the agent has confirmed
		// to the canonical storage. Monotonic. Read by the harness during partition
		// and reconnect phases to characterize drain progress.
		internal long LastReplicatedEntryId => progress.LastReplicatedEntryId;

		// paper05-lab5: backlog observer for partition phase telemetry.
		internal int PendingCount => pendingRecords.Count;

		internal void Start()
		{
			if (replicationThread != null) throw new InvalidOperationException("ReplicationAgent already started.");

			stopping = false;
			replicationThread = new Thread(ReplicationLoop)
			{
				IsBackground = true,
				Name = "ReplicationAgent"
			};
			replicationThread.Start();
		}

		internal void Stop()
		{
			stopping = true;
			signal.Set();
		}

		internal void DrainAndWait(TimeSpan? timeout = null)
		{
			TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);

			signal.Set();

			if (!drainComplete.Wait(effectiveTimeout))
				throw new TimeoutException("ReplicationAgent drain timed out.");
		}

		internal void ReplayUnreplicated(DiaryStorageFileSystem localStore)
		{
			if (localStore == null) throw new ArgumentNullException(nameof(localStore));

			long lastReplicated = progress.LastReplicatedEntryId;

			// Phase 5 of the Action refactor: dropped the ForEachActionDefinition
			// pre-pass. Define entries are journal records and replicate via
			// ForEachRawRecord like any other entry — the ReplicateRecord dispatch
			// below decodes them and routes to WriteDefineEntry on the remote.

			// The watermark is a hint, not the authority on what the canonical store holds:
			// the remote write commits BEFORE the watermark reaches disk, so every
			// interruption in that window leaves the watermark behind records already
			// committed remotely. Replaying below the canonical store's own head re-sends
			// them, which is fatal where the entry id is a primary key (the throw escapes
			// the Diary constructor, so the actor never rehydrates) and silent divergence
			// where it is not (the duplicates are accepted and the replay applies the same
			// journaled invocations twice). Heal the watermark up to the head first, so a
			// lagging watermark costs nothing on this boot and none of the following ones.
			//
			// Scope of the rule: it recognizes the canonical store as holding a PREFIX of
			// the local journal, which is what in-order replication produces. A canonical
			// journal mutated out of band (rows removed below its head) is not reconstructed
			// by the replay — recovering that is an operator decision, not a replay one.
			long canonicalHead = remoteStore.LastJournaledEntryId();
			if (lastReplicated < canonicalHead)
			{
				lastReplicated = canonicalHead;
				progress.LastReplicatedEntryId = canonicalHead;
				progress.Save();
			}

			int replayed = 0;

			localStore.ForEachRawRecord(lastReplicated, (entryId, rawRecord) =>
			{
				try
				{
					ReplicateRecord(entryId, rawRecord);
				}
				catch
				{
					// Persist the records the canonical store did accept before letting the
					// failure out. Without this the throw discards the progress of the whole
					// prefix, the next start replays it from the same watermark and fails
					// identically: the failure suppresses exactly the progress that would
					// avoid it, turning a transient rejection into an actor that never boots.
					// Best effort on purpose: a watermark that cannot be written must not
					// replace the rejection the caller needs to see.
					SaveProgressBestEffort();
					throw;
				}

				progress.LastReplicatedEntryId = entryId;
				if (++replayed % PERSIST_PROGRESS_INTERVAL == 0) progress.Save();
			});

			progress.Save();
		}

		// Nothing may escape this method: it is the body of a thread, and an unhandled exception
		// on a thread terminates the process. Losing replication is a degradation the design
		// already covers — the local journal holds every record and the next start replays what
		// did not reach the canonical store — while taking the host down with it is not. The
		// path that made this concrete was persisting the watermark: replacing that file can
		// fail for reasons that belong to the filesystem, not to the journal (another handle on
		// it, no space, no permission), and it did abort the process.
		private void ReplicationLoop()
		{
			try
			{
				DrainUntilStopped();
			}
			catch (Exception ex)
			{
				remoteStore.Logger.Error($"ReplicationAgent loop ended on an unexpected error: {ex.Message}", ex);
				System.Threading.Interlocked.Increment(ref _replicationFailureCount);
				_lastReplicationError = ex.Message;
			}

			try
			{
				FlushRemaining();
			}
			catch (Exception ex)
			{
				remoteStore.Logger.Error($"ReplicationAgent flush ended on an unexpected error: {ex.Message}", ex);
			}
		}

		private void DrainUntilStopped()
		{
			while (!stopping)
			{
				// Arm the signal BEFORE draining. Resetting it after the drain discarded an
				// EnqueueRecord that arrived while the drain was running, so that record sat
				// in the queue for a whole retry delay although it had been signalled — and
				// the watermark stayed that far behind for the same span.
				signal.Reset();

				int batch = 0;
				try
				{
					// paper05-lab5 found this loop dropped items on failure: the
					// pre-fix code TryDequeued first, then called ReplicateRecord;
					// an exception jumped to the outer catch with the item already
					// gone from the queue. Items were only recoverable via
					// ReplayUnreplicated on the next actor startup. Peek-then-
					// dequeue-on-success keeps the head intact for retry while the
					// remote is unreachable, so live catch-up after reconnect is
					// possible without restarting the actor.
					while (pendingRecords.TryPeek(out var item))
					{
						ReplicateRecord(item.entryId, item.record);
						pendingRecords.TryDequeue(out _);
						progress.LastReplicatedEntryId = item.entryId;
						if (++batch % PERSIST_PROGRESS_INTERVAL == 0) SaveProgressBestEffort();
					}
				}
				catch (Exception ex)
				{
					// `continue` skips the Save below, so persist here what this pass did
					// deliver: the records the canonical store accepted before the rejection
					// must not be replayed by the next start. Best effort — a watermark that
					// cannot be written must not take the retry loop down with it.
					if (batch > 0) SaveProgressBestEffort();

					remoteStore.Logger.Error($"ReplicationAgent error: {ex.Message}", ex);
					System.Threading.Interlocked.Increment(ref _replicationFailureCount);
					_lastReplicationError = ex.Message;
					Thread.Sleep(RETRY_DELAY_MS);
					continue;
				}

				if (batch > 0) SaveProgressBestEffort();

				if (pendingRecords.IsEmpty)
					drainComplete.Set();

				signal.Wait(RETRY_DELAY_MS);
			}
		}

		// paper05-lab5 diagnostic: visible counters of replication failures so the
		// harness can distinguish "agent stuck" vs "agent retrying but always failing".
		private long _replicationFailureCount;
		private string _lastReplicationError;
		internal long ReplicationFailureCount => System.Threading.Interlocked.Read(ref _replicationFailureCount);
		internal string LastReplicationError => _lastReplicationError;

		// Phase 5 of the Action refactor: ReplicateRecord now routes Define records
		// to WriteDefineEntry and Invocation records to WriteInvocationEntry, matching
		// the post-cutover write APIs. Script records keep flowing through
		// WriteScriptEntry. Pre-Fase-5 the agent maintained a side-table of action
		// definitions so it could call WriteNewActionEntry on the remote — that
		// machinery is gone (the journal is the catalog now).
		private void ReplicateRecord(long entryId, byte[] fullRecord)
		{
			if (fullRecord.Length < 5) return;

			int bodyLength = fullRecord.Length - 4;
			byte[] body = new byte[bodyLength];
			Buffer.BlockCopy(fullRecord, 4, body, 0, bodyLength);

			EventRecordType peekedType = BinaryEventCodec.PeekRecordType(body);

			if (peekedType == EventRecordType.Define)
			{
				bool okDef = BinaryEventCodec.TryDecodeDefine(body, bodyLength,
					out _, out DateTime defineOccurredAt,
					out int defineActionId, out string defineStatementText, out _,
					PayloadCompression.None, EncryptionMode.None, null);

				if (okDef)
				{
					remoteStore.WriteDefineEntry(defineActionId, defineStatementText, entryId, defineOccurredAt);
				}
				return;
			}

			bool decoded = BinaryEventCodec.TryDecode(body, bodyLength,
				out EventRecordType eventType, out long decodedEntryId,
				out DateTime occurredAt,
				out string scriptOrArguments, out int actionId,
				PayloadCompression.None, EncryptionMode.None, null);

			if (!decoded) return;

			if (eventType == EventRecordType.Script)
			{
				remoteStore.WriteScriptEntry(entryId, scriptOrArguments, occurredAt);
			}
			else
			{
				remoteStore.WriteInvocationEntry(actionId, entryId, occurredAt, scriptOrArguments);
			}
		}

		private void FlushRemaining()
		{
			try
			{
				// Same peek-then-dequeue-on-success pattern as ReplicationLoop:
				// keep unreplicated items in the queue when the remote rejects,
				// so the next startup's ReplayUnreplicated can recover them.
				while (pendingRecords.TryPeek(out var item))
				{
					ReplicateRecord(item.entryId, item.record);
					pendingRecords.TryDequeue(out _);
					progress.LastReplicatedEntryId = item.entryId;
				}
			}
			catch (Exception ex)
			{
				remoteStore.Logger.Error($"ReplicationAgent flush error: {ex.Message}", ex);
			}

			// Reached on both outcomes, where before it was the last statement inside the
			// try: a rejection half-way through the flush skipped the Save entirely, so the
			// records the canonical store had already accepted were replayed by the next start.
			SaveProgressBestEffort();
		}

		// Every Save that happens on the replication thread, and the one that keeps the
		// progress of a failed replay, goes through here: persisting the watermark must never
		// become the failure itself. The caller is either propagating another exception that
		// must reach it intact, or running on a thread whose escape would take the process down.
		// Losing the watermark only costs re-walking the local journal on the next start, which
		// is now free of consequence because the replay is bounded by the canonical head.
		private void SaveProgressBestEffort()
		{
			try
			{
				progress.Save();
			}
			catch (Exception ex)
			{
				remoteStore.Logger.Error($"ReplicationAgent could not persist the replication watermark: {ex.Message}", ex);
			}
		}

		public void Dispose()
		{
			if (!disposed)
			{
				disposed = true;
				Stop();
				replicationThread?.Join(TimeSpan.FromSeconds(5));
				signal.Dispose();
				drainComplete.Dispose();
			}
		}
	}
}
