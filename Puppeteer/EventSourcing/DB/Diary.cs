using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Puppeteer.EventSourcing.DB.FileSystem;

namespace Puppeteer.EventSourcing.DB
{
	internal class Diary : IDisposable
	{

		private readonly DiaryStorage diaryStorage;
		internal DiaryStorage Storage => diaryStorage;
		// Pre-2026-05-19: the script of a PerformCmd that failed at runtime was
		// journaled with a "//EXECUTION ERROR WAS DETECTED ON THIS COMMAND" prefix
		// so that later rehydration could identify it. Removed: the script
		// is persisted intact and the failure information travels through IPuppeteerLogger.
		// The journal is a faithful record of attempted commands, not a channel
		// for error metadata.
		private readonly DatabaseType dbType;

		// Buffering: local WAL + asynchronous replication to the remote storage.
		private DiaryStorageFileSystem localBuffer;
		private ReplicationAgent replicationAgent;
		private ReplicationProgress replicationProgress;
		private ManualResetEventSlim diskFullGate;
		private Timer diskSpaceMonitor;
		private readonly string localBufferPath;
		private bool IsBuffered => localBuffer != null;

		// Record-written fan-out. Subscribers live in a FLAT immutable array published
		// with CAS (registration is lock-free and invocation is a plain loop — no
		// nested-closure chain, so the call depth stays constant no matter how many
		// observers a host adds). Notifications are ENQUEUED at write time and the
		// queue is drained OUTSIDE the writer's exclusive section: a journal record
		// that is already durable does not need the lock that protected writing it,
		// so subscriber work (projection push signals, replication hooks) must not be
		// charged to the actor's single-writer critical path. The deferral gate is
		// installed by the journal's owner and answers "is the calling thread still
		// inside its exclusive write section?"; when it says no (or nobody installed
		// one) the dispatch drains inline, which preserves the historical synchronous
		// delivery on the writer's own thread.
		private static readonly Action<long, byte[]>[] NO_RECORD_SUBSCRIBERS = new Action<long, byte[]>[0];
		private Action<long, byte[]>[] recordWrittenSubscribers = NO_RECORD_SUBSCRIBERS;
		private readonly ConcurrentQueue<(long entryId, byte[] record)> pendingRecordNotifications = new ConcurrentQueue<(long entryId, byte[] record)>();
		private int recordNotificationDrainInProgress;
		internal Func<bool> RecordNotificationDeferralGate { get; set; } = () => false;

		// paper05-lab5: harness-facing observers — make the buffered-vs-direct
		// distinction visible so the lab can characterize partition/catch-up.
		internal bool IsBufferedExternal => IsBuffered;
		internal long LastReplicatedEntryId => IsBuffered ? replicationAgent.LastReplicatedEntryId : -1L;
		internal int PendingReplicationCount => IsBuffered ? replicationAgent.PendingCount : 0;
		internal long LocalBufferLastWrittenEntryId => IsBuffered ? localBuffer.LastWrittenEntryId : -1L;
		internal long ReplicationFailureCount => IsBuffered ? replicationAgent.ReplicationFailureCount : 0L;
		internal string LastReplicationError => IsBuffered ? replicationAgent.LastReplicationError : null;

		internal Diary(DatabaseType dbType, string connectionString, IActorEventJournalClient eventJournalClient)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connectionString);
			ArgumentNullException.ThrowIfNull(eventJournalClient);

			this.dbType = dbType;

			(string backendConnectionString, string parsedLocalBufferPath) =
				StorageConnectionString.Extract(connectionString);

			// IN_MEMORY silently ignores the key (buffering to memory makes no sense:
			// the canonical storage is already the fastest possible medium). The other dbTypes
			// honor the presence/absence of the key as an on/off switch for the buffer.
			if (dbType == DatabaseType.IN_MEMORY)
				parsedLocalBufferPath = null;

			ValidateEagerPaths(dbType, backendConnectionString, parsedLocalBufferPath);

			this.localBufferPath = parsedLocalBufferPath;

			if (dbType == DatabaseType.MySQL)
			{
				diaryStorage = new DiaryStorageMySQL(eventJournalClient, backendConnectionString);
			}
			else if (dbType == DatabaseType.SQLServer)
			{
				diaryStorage = new DiaryStorageSQLServer(eventJournalClient, backendConnectionString);
			}
			else if (dbType == DatabaseType.IN_MEMORY)
			{
				diaryStorage = new DiaryStorageInMemory(eventJournalClient);
			}
			else if (dbType == DatabaseType.FileSystem)
			{
				diaryStorage = new DiaryStorageFileSystem(eventJournalClient, backendConnectionString);
			}
			else if (dbType == DatabaseType.PlainText)
			{
				diaryStorage = new DiaryStorageTxt(eventJournalClient, backendConnectionString);
			}
			else
			{
				throw new Exception($"Unknown database type '{dbType}'.");
			}

			if (!string.IsNullOrWhiteSpace(parsedLocalBufferPath))
			{
				InitializeBuffering(eventJournalClient);
			}
		}

		private static void ValidateEagerPaths(DatabaseType dbType, string backendConnectionString, string localBufferPath)
		{
			if (dbType == DatabaseType.FileSystem)
			{
				var fsCs = new Puppeteer.EventSourcing.DB.FileSystem.FileSystemConnectionString(backendConnectionString);
				StoragePathValidator.EnsureFileSystemPathIsUsable(fsCs.Path);

				if (!string.IsNullOrWhiteSpace(localBufferPath))
					StoragePathValidator.EnsureBufferAndCanonicalAreDistinct(fsCs.Path, localBufferPath);
			}

			if (!string.IsNullOrWhiteSpace(localBufferPath))
				StoragePathValidator.EnsureLocalBufferPathIsUsable(localBufferPath);
		}

		private void InitializeBuffering(IActorEventJournalClient eventJournalClient)
		{
			string localConnectionString = $"path={localBufferPath}";
			var buffer = new DiaryStorageFileSystem(eventJournalClient, localConnectionString);

			string actorBasePath = Path.Combine(localBufferPath, eventJournalClient.ActorName);
			var atomicOp = AtomicFileOperationFactory.Create();
			var progress = new ReplicationProgress(
				Path.Combine(actorBasePath, "replication_progress.bin"), atomicOp);
			progress.Load();

			var agent = new ReplicationAgent(diaryStorage, progress);

			if (progress.LastReplicatedEntryId < buffer.LastWrittenEntryId)
				agent.ReplayUnreplicated(buffer);

			buffer.OnRecordWritten = (entryId, record) =>
			{
				agent.EnqueueRecord(entryId, record);
				DispatchRecordWritten(entryId, record);
			};
			// Phase 5 of the Action refactor: dropped buffer.OnNewActionDefined wiring.
			// Replication of actions now flows entirely through OnRecordWritten — the
			// Define entry is a journal record like any other and replicates as
			// CueEvent. The legacy ActionDefinition message + EnqueueActionDefinition
			// path is gone (signed: cross-stage atomicity is unnecessary).

			// Outbox durability for backends that have no local row table of their
			// own (SQL, plain-text): reuse the local-buffer's persistent FS queue as
			// the default OutboxStorage — the same local-buffer strategy used for
			// perform-command writes. The FileSystem / in-memory backends self-wire
			// their own outbox in their constructor, so leave those untouched.
			if (!(diaryStorage is DiaryStorageFileSystem) && buffer.OutboxStorage != null)
				diaryStorage.SetOutboxStorage(buffer.OutboxStorage);

			this.localBuffer = buffer;
			this.replicationProgress = progress;
			this.replicationAgent = agent;
			this.diskFullGate = new ManualResetEventSlim(initialState: true);
			this.diskSpaceMonitor = new Timer(CheckDiskSpace, null,
				TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

			agent.Start();
		}

		private void CheckDiskSpace(object state)
		{
			try
			{
				string root = Path.GetPathRoot(localBufferPath);
				if (string.IsNullOrEmpty(root)) return;

				var drive = new DriveInfo(root);
				if (drive.AvailableFreeSpace < 10 * 1024 * 1024)
					diskFullGate.Reset();
				else
					diskFullGate.Set();
			}
			catch
			{
				// If we can't verify free space, don't block.
			}
		}

		private void WaitIfDiskFull()
		{
			diskFullGate?.Wait();
		}

		internal Action<long, byte[]> OnRecordWritten
		{
			// Assignment REPLACES the whole subscriber set (the historical contract of
			// this seam); AddRecordWrittenCallback APPENDS to it. Both publish a fresh
			// immutable array, so an in-flight dispatch observes either the old set or
			// the new one, never a torn mix.
			set
			{
				Interlocked.Exchange(ref recordWrittenSubscribers,
					value == null ? NO_RECORD_SUBSCRIBERS : new Action<long, byte[]>[] { value });
				SyncStorageRecordSink();
			}
		}

		internal void AddRecordWrittenCallback(Action<long, byte[]> callback)
		{
			if (callback == null) throw new ArgumentNullException(nameof(callback));

			// Lab 3 fix, restated over the flat array: two concurrent registrations
			// must not lose each other. CAS over the published array keeps the append
			// atomic; unlike the previous nested-closure chain, invocation cost per
			// subscriber stays flat and the call depth is constant.
			while (true)
			{
				var previous = Volatile.Read(ref recordWrittenSubscribers);
				var next = new Action<long, byte[]>[previous.Length + 1];
				Array.Copy(previous, next, previous.Length);
				next[previous.Length] = callback;
				if (Interlocked.CompareExchange(ref recordWrittenSubscribers, next, previous) == previous)
					break;
			}
			SyncStorageRecordSink();
		}

		// The storage-side sink is installed only while somebody is listening:
		// backends gate the wire-bytes synthesis on `OnRecordWritten != null`, so a
		// journal with zero subscribers must keep paying zero encode cost. In
		// buffered mode the local buffer's write tail is wired once (replication
		// agent + dispatch) and DispatchRecordWritten early-returns when nobody
		// subscribed.
		private void SyncStorageRecordSink()
		{
			if (IsBuffered) return;
			diaryStorage.OnRecordWritten =
				Volatile.Read(ref recordWrittenSubscribers).Length == 0 ? null : DispatchRecordWritten;
		}

		private void DispatchRecordWritten(long entryId, byte[] record)
		{
			if (Volatile.Read(ref recordWrittenSubscribers).Length == 0) return;

			// FIFO by construction: records are enqueued in the order the store
			// completed them (writes are serialized by the journal owner's exclusion),
			// and the single-flight drain below preserves that order end to end.
			pendingRecordNotifications.Enqueue((entryId, record));

			if (!RecordNotificationDeferralGate())
				DrainPendingRecordNotifications();
		}

		// Single-flight drain: whoever wins the flag delivers everything queued, in
		// order; a loser returns immediately knowing the winner will pick up the
		// records it just enqueued. The outer loop closes the race where an enqueue
		// lands between the winner's last dequeue and the flag release. A subscriber
		// failure is logged and does not stop the drain: the record is already
		// durable, so a broken observer must poison neither the remaining observers
		// nor the writer that happens to be draining.
		internal void DrainPendingRecordNotifications()
		{
			while (!pendingRecordNotifications.IsEmpty)
			{
				if (Interlocked.CompareExchange(ref recordNotificationDrainInProgress, 1, 0) != 0)
					return;

				try
				{
					while (pendingRecordNotifications.TryDequeue(out (long entryId, byte[] record) notification))
					{
						var subscribers = Volatile.Read(ref recordWrittenSubscribers);
						for (int i = 0; i < subscribers.Length; i++)
						{
							try
							{
								subscribers[i](notification.entryId, notification.record);
							}
							catch (Exception ex)
							{
								Debug.WriteLine($"[Diary.OnRecordWritten] subscriber failed for EntryId {notification.entryId}: {ex.GetType().Name}: {ex.Message}");
							}
						}
					}
				}
				finally
				{
					Volatile.Write(ref recordNotificationDrainInProgress, 0);
				}
			}
		}

		// Phase 5 of the Action refactor: dropped OnNewActionDefined property.
		// Replication of actions flows through OnRecordWritten — Define entries are
		// journal records and replicate as CueEvent.

		internal void WriteRawRecord(byte[] record, long entryId)
		{
			if (IsBuffered)
			{
				WaitIfDiskFull();
				localBuffer.WriteRawRecord(record, entryId);
				replicationAgent.EnqueueRecord(entryId, record);
			}
			else if (diaryStorage is DiaryStorageFileSystem fs)
			{
				fs.WriteRawRecord(record, entryId);
			}
			else
			{
				throw new NotSupportedException("WriteRawRecord is only supported for FileSystem storage");
			}
		}

		// Phase 5 of the Action refactor: dropped WriteRawActionDefinition.
		// Followers receiving Define records via CueEvent apply them with
		// WriteRawRecord (the byte[] is the encoded Define record, decoded
		// during the follower's own RehydrateFromEvent / ApplyReplicatedEvent).

		internal long RehydrateFromEvent(long afterEntryId = 0, bool includeExposeData = false)
		{
			if (diaryStorage == null) throw new Exception("The Actor cannot persist or recover its last state because no database connection was configured.");

			if (IsBuffered)
			{
				replicationAgent.DrainAndWait();
				return diaryStorage.RehydrateFromEvent(afterEntryId, includeExposeData);
			}

			return diaryStorage.RehydrateFromEvent(afterEntryId, includeExposeData);
		}

		internal async Task<long> RehydrateFromEventAsync(long afterEntryId = 0, bool includeExposeData = false)
		{
			if (diaryStorage == null) throw new Exception("The Actor cannot persist or recover its last state because no database connection was configured.");

			if (IsBuffered)
			{
				replicationAgent.DrainAndWait();
				return await diaryStorage.RehydrateFromEventAsync(afterEntryId, includeExposeData);
			}

			return await diaryStorage.RehydrateFromEventAsync(afterEntryId, includeExposeData);
		}

		internal MemoryStream Archive(DateTime startDate, DateTime endDate)
		{
			return diaryStorage.Archive(startDate, endDate);
		}

		internal void Trim(DateTime trimmedDown)
		{
			diaryStorage.Trim(trimmedDown);
		}

		internal void Distill()
		{
			if (diaryStorage == null) throw new Exception("The Actor cannot distill because no database connection was configured.");

			if (IsBuffered)
			{
				// Drain the queue toward the remote storage before the Distill to
				// ensure the filtering operates over the complete state.
				replicationAgent.DrainAndWait();
				localBuffer.Distill();
				diaryStorage.Distill();
			}
			else
			{
				diaryStorage.Distill();
			}
		}

		internal static IEnumerable<string> ListActorsToLoad(string dbType, string connectionString, double minimumContributionPercent)
		{
			if (minimumContributionPercent < 0 && minimumContributionPercent > 100) throw new ArgumentException(nameof(minimumContributionPercent));

			IEnumerable<string> actorsToLoad = new List<string>();

			if (dbType == DatabaseType.SQLServer.ToString())
			{

				actorsToLoad = DiaryStorageSQLServer.GetActorsToLoad(connectionString, minimumContributionPercent);
			}
			else if (dbType == DatabaseType.MySQL.ToString())
			{
				actorsToLoad = DiaryStorageMySQL.GetActorsToLoad(connectionString, minimumContributionPercent);
			}
			else if (dbType == DatabaseType.FileSystem.ToString())
			{
				actorsToLoad = DiaryStorageFileSystem.GetActorsToLoad(connectionString, minimumContributionPercent);
			}
			else
			{
				throw new LanguageException($"Cannot list the actors to load because the database type '{dbType}' is not recognized.");
			}

			return actorsToLoad;
		}

		// Phase 6 of the Action refactor: dropped Diary.WriteActionEntry +
		// WriteNewActionEntry (sync + async). Use WriteInvocationEntry /
		// WriteDefineEntry / WriteDefineWithFirstInvocation instead.

		internal void WriteScriptEntry(long entryId, string script, DateTime now, string exposeData = null)
		{
			if (IsBuffered)
			{
				WaitIfDiskFull();
				localBuffer.WriteScriptEntry(entryId, script, now, exposeData);
			}
			else
			{
				diaryStorage.DateOfLastActivity = DateTime.Now;
				diaryStorage.WriteScriptEntry(entryId, script, now, exposeData);
			}
		}

		internal async Task WriteScriptEntryAsync(long entryId, string script, DateTime now, string exposeData = null)
		{
			if (IsBuffered)
			{
				WaitIfDiskFull();
				await Task.Run(() => localBuffer.WriteScriptEntry(entryId, script, now, exposeData));
			}
			else
			{
				diaryStorage.DateOfLastActivity = DateTime.Now;
				await diaryStorage.WriteScriptEntryAsync(entryId, script, now, exposeData);
			}
		}

		// Phase 4 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// façade wrappers paired with the abstract DiaryStorage methods. Phase 4
		// split-model signed: Define + Invocation are TWO separate journal rows on
		// the first invocation, so MarkAsSkip on a first invocation cannot
		// collaterally erase the Define declaration.
		internal void WriteDefineEntry(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			if (IsBuffered)
			{
				WaitIfDiskFull();
				localBuffer.WriteDefineEntry(actionId, defineStatementText, entryId, now, exposeData);
			}
			else
			{
				diaryStorage.DateOfLastActivity = DateTime.Now;
				diaryStorage.WriteDefineEntry(actionId, defineStatementText, entryId, now, exposeData);
			}
		}

		internal async Task WriteDefineEntryAsync(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			if (IsBuffered)
			{
				WaitIfDiskFull();
				await Task.Run(() => localBuffer.WriteDefineEntry(actionId, defineStatementText, entryId, now, exposeData));
			}
			else
			{
				diaryStorage.DateOfLastActivity = DateTime.Now;
				await diaryStorage.WriteDefineEntryAsync(actionId, defineStatementText, entryId, now, exposeData);
			}
		}

		internal void WriteInvocationEntry(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			if (IsBuffered)
			{
				WaitIfDiskFull();
				localBuffer.WriteInvocationEntry(actionId, entryId, now, arguments, exposeData);
			}
			else
			{
				diaryStorage.DateOfLastActivity = DateTime.Now;
				diaryStorage.WriteInvocationEntry(actionId, entryId, now, arguments, exposeData);
			}
		}

		internal async Task WriteInvocationEntryAsync(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			if (IsBuffered)
			{
				WaitIfDiskFull();
				await Task.Run(() => localBuffer.WriteInvocationEntry(actionId, entryId, now, arguments, exposeData));
			}
			else
			{
				diaryStorage.DateOfLastActivity = DateTime.Now;
				await diaryStorage.WriteInvocationEntryAsync(actionId, entryId, now, arguments, exposeData);
			}
		}

		// Phase 4 atomic write — see DiaryStorage.cs for the contract. Used by the
		// ActorHandler cutover on cache miss with parameters: emits the Define +
		// first Invocation as TWO separate journal rows in a single transactional
		// unit per backend.
		internal void WriteDefineWithFirstInvocation(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			if (IsBuffered)
			{
				WaitIfDiskFull();
				localBuffer.WriteDefineWithFirstInvocation(actionId, defineStatementText, defineEntryId, invocationEntryId, now, arguments, exposeData);
			}
			else
			{
				diaryStorage.DateOfLastActivity = DateTime.Now;
				diaryStorage.WriteDefineWithFirstInvocation(actionId, defineStatementText, defineEntryId, invocationEntryId, now, arguments, exposeData);
			}
		}

		internal async Task WriteDefineWithFirstInvocationAsync(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			if (IsBuffered)
			{
				WaitIfDiskFull();
				await Task.Run(() => localBuffer.WriteDefineWithFirstInvocation(actionId, defineStatementText, defineEntryId, invocationEntryId, now, arguments, exposeData));
			}
			else
			{
				diaryStorage.DateOfLastActivity = DateTime.Now;
				await diaryStorage.WriteDefineWithFirstInvocationAsync(actionId, defineStatementText, defineEntryId, invocationEntryId, now, arguments, exposeData);
			}
		}

		internal long GetLastProcessedEntryId(int followerId)
		{
			if (followerId <= 0) throw new LanguageException($"Follower id '{followerId}' must be greater than zero");
			return diaryStorage.GetLastProcessedEntryId(followerId);
		}


		internal void SaveLastProcessedEntryId(int followerId, long entryId)
		{
			if (followerId <= 0) throw new LanguageException($"Follower id '{followerId}' must be greater than zero");
			if (entryId <= 0) throw new LanguageException($"Last processed entry id '{entryId}' must be greater than zero");

			if (IsBuffered)
				localBuffer.SaveLastProcessedEntryId(followerId, entryId);

			diaryStorage.SaveLastProcessedEntryId(followerId, entryId);
		}

		internal DatabaseType DatabaseType
		{
			get
			{
				return dbType;
			}
		}

		internal DateTime DateOfLastActivity
		{
			get
			{
				return diaryStorage.DateOfLastActivity;
			}
		}

		internal void ChangePrimaryKey()
		{
			diaryStorage.ChangePrimaryKey();
		}

		public void Dispose()
		{
			if (IsBuffered)
			{
				replicationAgent?.Stop();
				try { replicationAgent?.DrainAndWait(TimeSpan.FromSeconds(30)); } catch (TimeoutException) { }
				replicationAgent?.Dispose();
				localBuffer?.Dispose();
				diskSpaceMonitor?.Dispose();
				diskFullGate?.Dispose();
			}
			if (diaryStorage is IDisposable disposable)
				disposable.Dispose();
		}
	}
}
