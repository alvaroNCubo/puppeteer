using System;
using System.Collections.Generic;
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
		// Pre-2026-05-19: el script de un PerformCmd que fallaba en runtime se
		// journalizaba con un prefijo "//EXECUTION ERROR WAS DETECTED ON THIS COMMAND"
		// para que la rehidratacion posterior lo identificara. Eliminado: el script
		// se persiste integro y la informacion del fallo viaja por IPuppeteerLogger.
		// El journal es un registro fidedigno de comandos intentados, no un canal
		// para metadata de errores.
		private readonly DatabaseType dbType;

		// Buffering: local WAL + asynchronous replication to the remote storage.
		private DiaryStorageFileSystem localBuffer;
		private ReplicationAgent replicationAgent;
		private ReplicationProgress replicationProgress;
		private ManualResetEventSlim diskFullGate;
		private Timer diskSpaceMonitor;
		private readonly string localBufferPath;
		private Action<long, byte[]> externalOnRecordWritten;
		private bool IsBuffered => localBuffer != null;

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

			// IN_MEMORY ignora silenciosamente la key (buffer a memoria no tiene sentido:
			// el storage canonico ya es el medio mas rapido posible). Resto de dbTypes
			// honran la presencia/ausencia de la key como switch on/off del buffer.
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
				externalOnRecordWritten?.Invoke(entryId, record);
			};
			// Phase 5 of the Action refactor: dropped buffer.OnNewActionDefined wiring.
			// Replication of actions now flows entirely through OnRecordWritten — the
			// Define entry is a journal record like any other and replicates as
			// CueEvent. The legacy ActionDefinition message + EnqueueActionDefinition
			// path is gone (firmado: cross-stage atomicity is unnecessary).

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
			set
			{
				if (IsBuffered)
				{
					externalOnRecordWritten = value;
				}
				else
				{
					// OnRecordWritten lives on DiaryStorage (abstract base) — all
					// backends (FS / SQL / InMemory) inherit it. FS produces wire
					// bytes naturally; SQL / InMemory synthesize via BinaryEventCodec.
					diaryStorage.OnRecordWritten = value;
				}
			}
		}

		internal void AddRecordWrittenCallback(Action<long, byte[]> callback)
		{
			if (callback == null) throw new ArgumentNullException(nameof(callback));

			// Lab 3 fix: two Cued reactions race on the same OnRecordWritten
			// field (each one wraps `previous` around its own callback). Without
			// CAS-style synchronisation the late-starting wrapper can read
			// `previous = null` and overwrite the earlier reaction's callback,
			// silently dropping the push subscription for one of the reactions.
			// Use Interlocked.CompareExchange so each wrapper deterministically
			// builds on the latest chain.
			if (IsBuffered)
			{
				while (true)
				{
					var previous = externalOnRecordWritten;
					Action<long, byte[]> next = (entryId, record) =>
					{
						previous?.Invoke(entryId, record);
						callback(entryId, record);
					};
					if (System.Threading.Interlocked.CompareExchange(ref externalOnRecordWritten, next, previous) == previous)
						return;
				}
			}
			else
			{
				// OnRecordWritten lives on DiaryStorage (abstract base) — every
				// backend has it. CAS over the base-class field generalises the
				// previous DiaryStorageFileSystem-only branch.
				while (true)
				{
					var previous = diaryStorage.OnRecordWritten;
					Action<long, byte[]> next = (entryId, record) =>
					{
						previous?.Invoke(entryId, record);
						callback(entryId, record);
					};
					if (System.Threading.Interlocked.CompareExchange(ref diaryStorage.OnRecordWritten, next, previous) == previous)
						return;
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
				// Drenar la cola hacia el storage remoto antes del Distill para
				// asegurar que el filtrado opera sobre el estado completo.
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
		// split-model firmado: Define + Invocation are TWO separate journal rows on
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
