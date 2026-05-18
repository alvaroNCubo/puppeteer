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

			localStore.ForEachRawRecord(lastReplicated, (entryId, rawRecord) =>
			{
				ReplicateRecord(entryId, rawRecord);
				progress.LastReplicatedEntryId = entryId;
			});

			progress.Save();
		}

		private void ReplicationLoop()
		{
			while (!stopping)
			{
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
						if (++batch % PERSIST_PROGRESS_INTERVAL == 0) progress.Save();
					}
				}
				catch (Exception ex)
				{
					Loggers.GetIntance().Db.Error($"ReplicationAgent error: {ex.Message}", ex);
					System.Threading.Interlocked.Increment(ref _replicationFailureCount);
					_lastReplicationError = ex.Message;
					Thread.Sleep(RETRY_DELAY_MS);
					continue;
				}

				if (batch > 0) progress.Save();

				if (pendingRecords.IsEmpty)
					drainComplete.Set();

				signal.Reset();
				signal.Wait(RETRY_DELAY_MS);
			}

			FlushRemaining();
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
					out _, out DateTime defineOccurredAt, out string defineIp, out string defineUser,
					out int defineActionId, out string defineStatementText, out _,
					PayloadCompression.None, EncryptionMode.None, null);

				if (okDef)
				{
					remoteStore.WriteDefineEntry(defineActionId, defineStatementText, entryId, defineIp, defineUser, defineOccurredAt);
				}
				return;
			}

			bool decoded = BinaryEventCodec.TryDecode(body, bodyLength,
				out EventRecordType eventType, out long decodedEntryId,
				out DateTime occurredAt, out string ip, out string user,
				out string scriptOrArguments, out int actionId,
				PayloadCompression.None, EncryptionMode.None, null);

			if (!decoded) return;

			if (eventType == EventRecordType.Script)
			{
				remoteStore.WriteScriptEntry(entryId, scriptOrArguments, ip, user, occurredAt);
			}
			else
			{
				remoteStore.WriteInvocationEntry(actionId, entryId, ip, user, occurredAt, scriptOrArguments);
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
				progress.Save();
			}
			catch (Exception ex)
			{
				Loggers.GetIntance().Db.Error($"ReplicationAgent flush error: {ex.Message}", ex);
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
