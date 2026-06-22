using Puppeteer.EventSourcing.DB.FileSystem;
using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class DiaryStorageFileSystem : DiaryStorage, IDisposable
	{
		private readonly string basePath;
		private readonly string journalDir;
		private readonly IAtomicFileOperation atomicOp;
		private readonly MetadataStore metadata;
		private readonly SparseIndex sparseIndex;
		private readonly JournalWriter journalWriter;
		// Phase 6 of the Action refactor: dropped the ActionStore field. The
		// lateral actions.bin file format is gone — the journal is the catalog.
		private readonly SkipStore skipStore;
		private readonly FollowerStore followerStore;
		private readonly ReactionStore reactionStore;
		private readonly EventElisionStorageFileSystem elisionStorage;
		private readonly EventMaterializationStorageFileSystem materializationStorage;
		private readonly object writeLock = new();

		// OnRecordWritten is declared on DiaryStorage (abstract base) so all
		// backends — FS / SQL / InMemory — inherit the same hook. FS produces
		// these bytes for free as part of its write path; SQL / InMemory synthesize
		// them via BinaryEventCodec only when there is a subscriber.
		// Phase 5 of the Action refactor: dropped OnNewActionDefined. Replication
		// of actions now flows through OnRecordWritten — Define entries are journal
		// records and replicate as CueEvent.

		internal long LastWrittenEntryId => metadata.LastWrittenEntryId;

		// Test seam: exposes the JournalWriter so the Distill tests can install
		// TestHookBetweenPhase1Files. Production does not need this access.
		internal FileSystem.JournalWriter JournalWriter => journalWriter;

		internal DiaryStorageFileSystem(IActorEventJournalClient eventJournalClient, string connectionString)
			: base(eventJournalClient, connectionString)
		{
			var parsedConnection = new FileSystemConnectionString(connectionString);
			this.basePath = Path.Combine(parsedConnection.Path, Name);
			this.journalDir = Path.Combine(basePath, "journal");
			// Phase 6 of the Action refactor: dropped the actions/ directory and the
			// actions.bin file format that backed it. Action definitions live in the
			// journal as Define records.
			string skipsDir = Path.Combine(basePath, "skips");
			string followersDir = Path.Combine(basePath, "followers");
			string indexDir = Path.Combine(basePath, "index");
			string reactionsDir = Path.Combine(basePath, "reactions");
			string elisionDir = Path.Combine(basePath, "elision");
			string materializationDir = Path.Combine(basePath, "materialization");

			Directory.CreateDirectory(journalDir);
			Directory.CreateDirectory(skipsDir);
			Directory.CreateDirectory(followersDir);
			Directory.CreateDirectory(indexDir);
			Directory.CreateDirectory(reactionsDir);
			Directory.CreateDirectory(elisionDir);
			Directory.CreateDirectory(materializationDir);

			this.atomicOp = AtomicFileOperationFactory.Create();

			this.metadata = new MetadataStore(Path.Combine(basePath, "meta.bin"), atomicOp);
			this.sparseIndex = new SparseIndex(Path.Combine(indexDir, "index.bin"), atomicOp);
			// Phase 6 of the Action refactor: dropped the actionStore initialization.
			this.skipStore = new SkipStore(Path.Combine(skipsDir, "skips.bin"), atomicOp);
			this.followerStore = new FollowerStore(Path.Combine(followersDir, "followers.bin"), atomicOp);
			this.reactionStore = new ReactionStore(
				Path.Combine(reactionsDir, "reactions.bin"),
				Path.Combine(reactionsDir, "checkpoints.bin"),
				atomicOp);

			bool metaLoaded = metadata.Load();
			sparseIndex.Load();
			followerStore.Load();

			if (!metaLoaded)
			{
				metadata.InitializeDefaults(
					parsedConnection.MaxFileSizeBytes,
					(byte)parsedConnection.Compression,
					(byte)parsedConnection.Encryption);
				EventJournalClient.IsNew = true;
			}
			else
			{
				EventJournalClient.IsNew = false;
			}

			this.journalWriter = new JournalWriter(journalDir, metadata, sparseIndex, atomicOp);
			journalWriter.Initialize();

			// Verify and correct potentially stale TotalEventCount and TotalNonSkippedCount.
			// meta.bin is persisted every PERSIST_METADATA_INTERVAL writes; after a crash it can
			// be up to (PERSIST_METADATA_INTERVAL - 1) units behind. ComputeRealTotalEventCount
			// reads only the 32-byte header of each sealed file plus the in-memory activeEventCount
			// — O(N_files * 32 bytes), typically < 100KB even for thousands of files.
			long realEventCount = journalWriter.ComputeRealTotalEventCount();
			if (realEventCount != metadata.TotalEventCount)
			{
				int skipCount = skipStore.LoadCount();
				metadata.TotalEventCount = realEventCount;
				metadata.TotalNonSkippedCount = Math.Max(0, realEventCount - skipCount);
				metadata.Save();
			}

			// Pending batch dir + witness coordinate cross-process writes to elision.bin
			// and skips.bin. Each MarkEventsAsElided writes a unique .batch there (commit via
			// atomic rename) and consolidation merges under an OS-level lock on the
			// witness. Design detail: PendingBatchDirectory.cs.
			string pendingSubDir = Path.Combine(elisionDir, "pending");
			string witnessPath = Path.Combine(elisionDir, "elision.witness");
			var pendingBatchDir = new PendingBatchDirectory(pendingSubDir, witnessPath);

			this.elisionStorage = new EventElisionStorageFileSystem(
				eventJournalClient,
				connectionString,
				Path.Combine(elisionDir, "elision.bin"),
				atomicOp,
				skipStore,
				pendingBatchDir);

			this.eventElisionStorage = elisionStorage;

			this.materializationStorage = new EventMaterializationStorageFileSystem(
				eventJournalClient,
				connectionString,
				Path.Combine(materializationDir, "materialization.bin"),
				atomicOp);

			this.eventMaterializationStorage = materializationStorage;

			this.materializationCheckpointStorage = new MaterializationCheckpointStorageFileSystem(
				eventJournalClient,
				connectionString,
				Path.Combine(materializationDir, "checkpoint.bin"),
				atomicOp);
		}

		protected internal override Task<long> RehydrateFromEventAsync(long afterEntryId, bool includeExposeData = false)
		{
			return Task.Run(() => RehydrateFromEvent(afterEntryId, includeExposeData));
		}

		protected internal override long RehydrateFromEvent(long afterEntryId, bool includeExposeData = false)
		{
			var skipSet = skipStore.Load();

			var reader = new JournalReader(journalDir, sparseIndex, skipSet,
				(PayloadCompression)metadata.CompressionFlag,
				(EncryptionMode)metadata.EncryptionFlag);

			// For a full forward rehydration (afterEntryId == 0), TotalNonSkippedCount in meta.bin
			// is already verified/corrected in the constructor, so use it directly — no scan needed.
			// For partial resume, scan the relevant range.
			long totalNonSkipped = afterEntryId == 0
				? metadata.TotalNonSkippedCount
				: reader.CountNonSkippedEvents(afterEntryId);
			EventJournalClient.BeginJournalReplay(totalNonSkipped);

			// Phase 5 of the Action refactor: legacy actionStore.LoadAll dropped.
			// Define entries in the journal populate the cache via
			// AddKnownActionFromDefine in entry-id order — by construction Define
			// precedes any Invocation that references it. (Phase 6 cleanup deletes
			// the ActionStore class, the actions.bin file format, and
			// WriteNewActionEntry that populated it.)

			long lastEntryId = afterEntryId;
			if (EventJournalClient.CanContinueReplay(lastEntryId))
			{
				lastEntryId = reader.ReadAll(afterEntryId, EventDataPool, EventJournalClient,
					() => EventJournalClient.CanContinueReplay(lastEntryId), includeExposeData);
			}

			EventJournalClient.EndJournalReplay(forcedToEnd: !EventJournalClient.CanContinueReplay(lastEntryId));

			return lastEntryId;
		}

		protected internal override void WriteScriptEntry(long entryId, string script, DateTime now, string exposeData = null)
		{
			if (script == null) throw new ArgumentNullException(nameof(script));

			byte[] record;
			lock (writeLock)
			{
				record = BinaryEventCodec.EncodeScriptEvent(entryId, now, script,
					(PayloadCompression)metadata.CompressionFlag,
					(EncryptionMode)metadata.EncryptionFlag,
					exposeData: exposeData);

				journalWriter.AppendRecord(record, entryId);
				PersistMetadataPeriodically();
			}

			DateOfLastActivity = DateTime.Now;
			OnRecordWritten?.Invoke(entryId, record);
		}

		protected internal override async Task WriteScriptEntryAsync(long entryId, string script, DateTime now, string exposeData = null)
		{
			await Task.Run(() => WriteScriptEntry(entryId, script, now, exposeData));
		}

		// Phase 6 of the Action refactor: dropped WriteActionEntry +
		// WriteNewActionEntry overrides (sync + async). Use WriteInvocationEntry /
		// WriteDefineEntry / WriteDefineWithFirstInvocation. The lateral
		// actionStore.AppendAction call is gone too — the journal is the catalog.

		internal void WriteRawRecord(byte[] record, long entryId)
		{
			if (record == null) throw new ArgumentNullException(nameof(record));

			lock (writeLock)
			{
				journalWriter.AppendRecord(record, entryId);
				PersistMetadataPeriodically();
			}

			DateOfLastActivity = DateTime.Now;

			// Parity with WriteScriptEntry / WriteDefineEntry / WriteInvocationEntry
			// (all of which invoke OnRecordWritten after the journal append). Without
			// this, a follower fed only through WriteRawRecord (Lab 3's transport path
			// and any future symmetric-consumer scenario) never sees push-mode
			// notifications and its Cued reactions sit idle. Inherited path of the
			// IsBuffered branch in Diary.cs:91 already wires the chain via
			// buffer.OnRecordWritten — the direct-FileSystem branch was missing the
			// equivalent invocation.
			OnRecordWritten?.Invoke(entryId, record);
		}

		// Phase 3 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// new write APIs for the post-cutover path. WriteDefineEntry encodes a Define
		// record (actionId + canonical DSL sentence + first-invocation args) into the
		// journal — explicitly bypassing the lateral actionStore.AppendAction call that
		// the legacy WriteNewActionEntry uses. The journal is the catalog now; the
		// lateral _ACTION-equivalent file dies in Phase 6.
		//
		// JournalReader skips Define records silently during replay (TryDecode returns
		// false on Define, see BinaryEventCodec). Phase 5 turns the skip into a real
		// dispatch once the cutover (Phase 4) flips the live caller.
		protected internal override void WriteDefineEntry(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			if (defineStatementText == null) throw new ArgumentNullException(nameof(defineStatementText));

			byte[] record;
			lock (writeLock)
			{
				record = FileSystem.BinaryEventCodec.EncodeDefineEvent(entryId, now, actionId, defineStatementText,
					(FileSystem.PayloadCompression)metadata.CompressionFlag,
					(FileSystem.EncryptionMode)metadata.EncryptionFlag,
					exposeData: exposeData);

				journalWriter.AppendRecord(record, entryId);
				PersistMetadataPeriodically();
			}

			DateOfLastActivity = DateTime.Now;
			OnRecordWritten?.Invoke(entryId, record);
		}

		protected internal override async Task WriteDefineEntryAsync(int actionId, string defineStatementText, long entryId, DateTime now, string exposeData = null)
		{
			await Task.Run(() => WriteDefineEntry(actionId, defineStatementText, entryId, now, exposeData));
		}

		protected internal override void WriteInvocationEntry(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			// Same on-disk shape as WriteActionEntry — the difference is purely semantic
			// (Phase 4+ assumes the actor's catalog lives in the journal as Define entries
			// rather than in the lateral actionStore). On-disk records are indistinguishable
			// from the legacy invocation rows; the meaning shift is in the caller.
			if (arguments == null) throw new ArgumentNullException(nameof(arguments));

			byte[] record;
			lock (writeLock)
			{
				record = FileSystem.BinaryEventCodec.EncodeActionEvent(entryId, now, actionId, arguments,
					(FileSystem.PayloadCompression)metadata.CompressionFlag,
					(FileSystem.EncryptionMode)metadata.EncryptionFlag,
					exposeData: exposeData);

				journalWriter.AppendRecord(record, entryId);
				PersistMetadataPeriodically();
			}

			DateOfLastActivity = DateTime.Now;
			OnRecordWritten?.Invoke(entryId, record);
		}

		protected internal override async Task WriteInvocationEntryAsync(int actionId, long entryId, DateTime now, string arguments, string exposeData = null)
		{
			await Task.Run(() => WriteInvocationEntry(actionId, entryId, now, arguments, exposeData));
		}

		// Phase 4 atomic write — see DiaryStorage.cs for the contract. Honest limit
		// for FileSystem (signed Q-fs-atomicity = α): both AppendRecord calls run
		// under the same writeLock, but a crash between the two flushes can leave
		// the Define orphan (same atomicity level as the legacy WriteNewActionEntry,
		// which also did two writes: _ACTION lateral + journal row).
		protected internal override void WriteDefineWithFirstInvocation(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			if (defineStatementText == null) throw new ArgumentNullException(nameof(defineStatementText));
			if (arguments == null) throw new ArgumentNullException(nameof(arguments));

			byte[] defineRecord;
			byte[] invocationRecord;
			lock (writeLock)
			{
				defineRecord = FileSystem.BinaryEventCodec.EncodeDefineEvent(defineEntryId, now, actionId, defineStatementText,
					(FileSystem.PayloadCompression)metadata.CompressionFlag,
					(FileSystem.EncryptionMode)metadata.EncryptionFlag,
					exposeData: null);

				invocationRecord = FileSystem.BinaryEventCodec.EncodeActionEvent(invocationEntryId, now, actionId, arguments,
					(FileSystem.PayloadCompression)metadata.CompressionFlag,
					(FileSystem.EncryptionMode)metadata.EncryptionFlag,
					exposeData: exposeData);

				journalWriter.AppendRecord(defineRecord, defineEntryId);
				journalWriter.AppendRecord(invocationRecord, invocationEntryId);
				PersistMetadataPeriodically();
			}

			DateOfLastActivity = DateTime.Now;
			OnRecordWritten?.Invoke(defineEntryId, defineRecord);
			OnRecordWritten?.Invoke(invocationEntryId, invocationRecord);
		}

		protected internal override async Task WriteDefineWithFirstInvocationAsync(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, DateTime now, string arguments, string exposeData = null)
		{
			await Task.Run(() => WriteDefineWithFirstInvocation(actionId, defineStatementText, defineEntryId, invocationEntryId, now, arguments, exposeData));
		}

		protected internal override long GetLastProcessedEntryId(int followerId)
		{
			return followerStore.GetLastProcessedEntryId(followerId);
		}

		protected internal override void SaveLastProcessedEntryId(int followerId, long entryId)
		{
			followerStore.SaveLastProcessedEntryId(followerId, entryId);
		}

		protected internal override long GetOrCreateReactionId(string formattedReaction)
		{
			if (formattedReaction == null) throw new ArgumentNullException(nameof(formattedReaction));
			return reactionStore.GetOrCreate(formattedReaction);
		}

		protected internal override (long detected, long confirmed) GetReactionCheckpoint(long reactionId, int seekLevel)
		{
			return reactionStore.GetCheckpoint(reactionId, seekLevel);
		}

		// Resume optimization (step 2): two global cursors per reaction. FileSystem is a
		// "local journal" backend (Job/Cue row of the matrix) -> resume by re-reading [closed,
		// high-water]; it does not use a snapshot (that is the pure-consumer row, without local journal).
		protected internal override (long highWater, long closedFrontier) GetReactionFrontier(long reactionId)
		{
			return reactionStore.GetFrontier(reactionId);
		}

		protected internal override void SaveReactionFrontier(long reactionId, long highWater, long closedFrontier)
		{
			reactionStore.SaveFrontier(reactionId, highWater, closedFrontier);
		}

		protected internal override void SaveReactionConfirmedCheckpoint(long reactionId, int seekLevel, long entryId)
		{
			reactionStore.SaveConfirmed(reactionId, seekLevel, entryId);
		}

		protected internal override long GetReactionLastProcessedEntryId(long reactionId, int pattern)
		{
			var (detected, _) = reactionStore.GetCheckpoint(reactionId, pattern);
			return detected;
		}

		protected internal override void SaveReactionLastProcessedEntryId(long reactionId, int pattern, long entryId)
		{
			reactionStore.SaveBoth(reactionId, pattern, entryId);
		}

		protected internal override bool MarkEventsAsElidedWithCheckpoint(CheckpointCommit commit)
		{
			if (commit == null) throw new ArgumentNullException(nameof(commit));

			long reactionId = commit.ReactionId;
			long[] eventIds = commit.EventIds;
			DateTime timestamp = commit.Timestamp;
			CheckpointVector newCheckpoint = commit.CheckpointVector;

			lock (writeLock)
			{
				// Verify the new checkpoint is greater than the current one (same logic as InMemory)
				bool isGreater = false;
				for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
				{
					long newDetected = newCheckpoint.Get(seekLevel);
					var (currentDetected, _) = reactionStore.GetCheckpoint(reactionId, seekLevel);

					if (newDetected > currentDetected) { isGreater = true; break; }
					if (newDetected < currentDetected) { isGreater = false; break; }
				}

				if (!isGreater) return false;

				int newSkips = elisionStorage.GetNewSkipCount(eventIds);
				elisionStorage.MarkEventsAsElided(eventIds, (int)reactionId, timestamp);

				// Save detected checkpoint for each seek level (confirmed is set later via SaveReactionConfirmedCheckpoint)
				for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
				{
					long newDetected = newCheckpoint.Get(seekLevel);
					reactionStore.SaveDetected(reactionId, seekLevel, newDetected);
				}

				if (newSkips > 0)
				{
					metadata.TotalNonSkippedCount = Math.Max(0, metadata.TotalNonSkippedCount - newSkips);
					metadata.Save();
				}

				return true;
			}
		}

		protected internal override MemoryStream Archive(DateTime fechaInicio, DateTime fechaFin)
		{
			var ms = new MemoryStream();
			using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
			{
				var entry = archive.CreateEntry($"{Name}-{fechaFin:yyyyMMdd}_bak.txt");
				using (var writer = new StreamWriter(entry.Open()))
				{
					writer.WriteLine($"-- Archive of skipped events for {Name}");
					writer.WriteLine($"-- Date range: {fechaInicio:yyyy-MM-dd} to {fechaFin:yyyy-MM-dd}");
					writer.WriteLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

					var skipSet = skipStore.Load();
					writer.WriteLine($"-- Skipped entry count: {skipSet.Count}");

					foreach (long id in skipSet)
					{
						writer.WriteLine($"SKIP EntryId={id}");
					}
				}
			}
			ms.Position = 0;
			return ms;
		}

		protected internal override IEnumerable<string> ListActorNames(string name)
		{
			string searchPath = Path.GetDirectoryName(basePath);
			if (searchPath == null || !Directory.Exists(searchPath))
				return Enumerable.Empty<string>();

			var result = new List<string>();
			foreach (string dir in Directory.GetDirectories(searchPath))
			{
				string journalSubDir = Path.Combine(dir, "journal");
				if (Directory.Exists(journalSubDir))
				{
					result.Add(Path.GetFileName(dir));
				}
			}
			return result;
		}

		protected internal override void Trim(DateTime trimmedDown)
		{
			var skipSet = skipStore.Load();
			skipStore.PurgeFullySkippedFiles(sparseIndex, skipSet, journalDir);

			// Recompute counts after files may have been deleted.
			long realEventCount = journalWriter.ComputeRealTotalEventCount();
			int skipCount = skipStore.LoadCount();
			metadata.TotalEventCount = realEventCount;
			metadata.TotalNonSkippedCount = Math.Max(0, realEventCount - skipCount);

			sparseIndex.Save();
			metadata.Save();
		}

		// Distill: see the contract in DiaryStorage.cs. Stage 3 hot-trim — the outer
		// writeLock is only taken at the end (phase 3) to synchronize cleanup of skipStore /
		// elision / metadata with MarkEventsAsElidedWithCheckpoint. Phases 1 (lock-free
		// sealed prep) and 2 (JournalWriter's internal writeLock only for the active
		// file and commits) run WITHOUT the outer lock, so producers keep calling
		// WriteScriptEntry/WriteRawRecord freely on the active file during the
		// long phase of the Distill.
		protected internal override void Distill()
		{
			// Phase 0: snapshot WITHOUT outer lock. skipSet and preserveEntryId are frozen
			// for this Distill; new elisions that arrive during phases 1/2 are not
			// processed here (the next Distill picks them up).
			var skipSet = skipStore.Load();
			if (skipSet.Count == 0) return;

			long preserveEntryId = metadata.LastWrittenEntryId;

			bool hasRemovableSkips = false;
			foreach (var id in skipSet)
			{
				if (id != preserveEntryId) { hasRemovableSkips = true; break; }
			}
			if (!hasRemovableSkips) return;

			Func<long, bool> shouldKeep = id => id == preserveEntryId || !skipSet.Contains(id);

			// Phases 1 + 2 inside JournalWriter.Distill (phase 1 lock-free, phase 2 under
			// JournalWriter's internal writeLock only to process the active file + commits).
			// The outer writeLock is NOT held here: WriteScriptEntry, WriteRawRecord,
			// MarkEventsAsElidedWithCheckpoint can run freely on the active file
			// during phase 1.
			List<long> removed = journalWriter.Distill(shouldKeep);
			if (removed.Count == 0) return;

			// Phase 3: outer lock to serialize the update of skipStore/elision/metadata
			// with MarkEventsAsElidedWithCheckpoint. Before reading skipStore we force a
			// drain of pending/*.batch into the consolidated file so the snapshot sees ALL
			// that other processes have written (otherwise their markers would stay "alive" in
			// pending/ and be reapplied post-distill referencing entries that no longer
			// exist — benign but undesirable accumulation). Re-read the current skipStore
			// (it may have new entries added during phases 1/2): we only remove
			// the ones WE physically removed; the new entries stay in
			// skipStore for the next Distill.
			lock (writeLock)
			{
				elisionStorage.ForceConsolidate();

				var currentSkipSet = skipStore.Load();
				var newSkipSet = new SortedSet<long>(currentSkipSet);
				foreach (var id in removed) newSkipSet.Remove(id);
				skipStore.Save(newSkipSet);

				elisionStorage.RemoveElidedIds(removed);

				int skipCount = skipStore.LoadCount();
				metadata.TotalNonSkippedCount = Math.Max(0, metadata.TotalEventCount - skipCount);

				sparseIndex.Save();
				metadata.Save();
			}
		}

		internal override void ChangePrimaryKey()
		{
		}

		internal static IEnumerable<string> GetActorsToLoad(string connectionString, double minimumContributionPercent)
		{
			if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

			string basePath = new FileSystemConnectionString(connectionString).Path;
			if (!Directory.Exists(basePath)) return Enumerable.Empty<string>();

			var result = new List<string>();
			foreach (string dir in Directory.GetDirectories(basePath))
			{
				string journalSubDir = Path.Combine(dir, "journal");
				if (Directory.Exists(journalSubDir))
				{
					result.Add(Path.GetFileName(dir));
				}
			}
			return result;
		}

		private int writeCountSinceLastPersist = 0;
		private const int PERSIST_METADATA_INTERVAL = 100;

		private void PersistMetadataPeriodically()
		{
			writeCountSinceLastPersist++;
			if (writeCountSinceLastPersist >= PERSIST_METADATA_INTERVAL)
			{
				metadata.Save();
				sparseIndex.Save();
				writeCountSinceLastPersist = 0;
			}
		}

		// Paper 5 / Materialize v2 — Phase 2: read raw records (without filtering skipSet).
		// Reuses ForEachRawRecord (journal iteration) + BinaryEventCodec.TryDecode
		// / TryDecodeDefine (decoding by type). Does NOT filter elided ones — Layer 1 is
		// raw by contract. The destination side decides whether to combine with (c)+(d) to
		// reconstruct Layer 2.
		protected internal override void ReadRecordsAfter(long afterEntryId, List<MaterializationRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");

			result.Clear();

			var compression = (PayloadCompression)metadata.CompressionFlag;
			var encryption = (EncryptionMode)metadata.EncryptionFlag;

			ForEachRawRecord(afterEntryId, (entryId, fullRecord) =>
			{
				// fullRecord = [4-byte length prefix][body]. TryDecode expects body.
				int bodyLength = fullRecord.Length - 4;
				byte[] body = new byte[bodyLength];
				Buffer.BlockCopy(fullRecord, 4, body, 0, bodyLength);

				EventRecordType peekedType = BinaryEventCodec.PeekRecordType(body);
				if (peekedType == EventRecordType.Define)
				{
					if (BinaryEventCodec.TryDecodeDefine(body, bodyLength,
						out _, out var occurredAt,
						out var actionId, out var defineText, out var expose,
						compression, encryption))
					{
						result.Add(new MaterializationRecord(
							entryId: entryId,
							kind: MaterializationRecordKind.Define,
							occurredAt: occurredAt,
							script: null,
							actionId: actionId,
							arguments: null,
							defineStatementText: defineText,
							exposeData: expose));
					}
					return;
				}

				if (BinaryEventCodec.TryDecode(body, bodyLength,
					out EventRecordType eventType, out _, out var fh,
					out var scriptOrArgs, out var actId, out var exposeData,
					compression, encryption))
				{
					if (eventType == EventRecordType.Script)
					{
						result.Add(new MaterializationRecord(
							entryId: entryId,
							kind: MaterializationRecordKind.Script,
							occurredAt: fh,
							script: scriptOrArgs,
							actionId: 0,
							arguments: null,
							defineStatementText: null,
							exposeData: exposeData));
					}
					else if (eventType == EventRecordType.Action)
					{
						result.Add(new MaterializationRecord(
							entryId: entryId,
							kind: MaterializationRecordKind.Invocation,
							occurredAt: fh,
							script: null,
							actionId: actId,
							arguments: scriptOrArgs,
							defineStatementText: null,
							exposeData: exposeData));
					}
				}
			});
		}

		protected internal override Task ReadRecordsAfterAsync(long afterEntryId, List<MaterializationRecord> result)
		{
			ReadRecordsAfter(afterEntryId, result);
			return Task.CompletedTask;
		}

		// Materialize v2 / Phase 3 — wire verb (c) DameCheckpointsHasta.
		// Delegates to the ReactionStore which already maintains registry + checkpoints
		// snapshot-atomic via its internal storeLock.
		protected internal override void ReadReactionRegistry(List<MaterializationReactionDefinition> result)
		{
			reactionStore.ListRegistry(result);
		}

		protected internal override void ReadReactionCheckpoints(List<MaterializationReactionCheckpoint> result)
		{
			reactionStore.ListCheckpoints(result);
		}

		internal void ForEachRawRecord(long afterEntryId, Action<long, byte[]> onRecord)
		{
			if (onRecord == null) throw new ArgumentNullException(nameof(onRecord));

			var allEntries = sparseIndex.GetAllEntries();
			if (allEntries.Count == 0) return;

			foreach (var indexEntry in allEntries)
			{
				if (indexEntry.LastEntryId <= afterEntryId) continue;

				string filePath = Path.Combine(journalDir, $"journal_{indexEntry.FileNumber:D6}.bin");
				if (!File.Exists(filePath)) continue;

				using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 262144, FileOptions.SequentialScan))
				{
					if (fs.Length < JournalWriter.HEADER_SIZE) continue;
					fs.Seek(JournalWriter.HEADER_SIZE, SeekOrigin.Begin);

					byte[] lenBuf = new byte[4];
					while (fs.Position < fs.Length)
					{
						int read = fs.Read(lenBuf, 0, 4);
						if (read < 4) break;

						int bodyLen = BitConverter.ToInt32(lenBuf, 0);
						if (bodyLen <= 0 || fs.Position + bodyLen > fs.Length) break;

						byte[] body = new byte[bodyLen];
						read = fs.Read(body, 0, bodyLen);
						if (read < bodyLen) break;

						if (!BinaryEventCodec.ValidateCrc(body, bodyLen)) break;

						long entryId = BinaryEventCodec.PeekEntryId(body);
						if (entryId <= afterEntryId) continue;

						byte[] fullRecord = new byte[4 + bodyLen];
						Buffer.BlockCopy(lenBuf, 0, fullRecord, 0, 4);
						Buffer.BlockCopy(body, 0, fullRecord, 4, bodyLen);

						onRecord(entryId, fullRecord);
					}
				}
			}
		}

		// Phase 6 of the Action refactor: dropped ForEachActionDefinition. Action
		// definitions live in the journal as Define records; replication consumes
		// them via ForEachRawRecord.

		internal void FlushAll()
		{
			lock (writeLock)
			{
				metadata.Save();
				sparseIndex.Save();
			}
		}

		public void Dispose()
		{
			FlushAll();
			journalWriter?.Dispose();
		}
	}
}
