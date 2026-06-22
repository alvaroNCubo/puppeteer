using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class JournalWriter : IDisposable
	{
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'E', (byte)'J' };
		private const ushort FORMAT_VERSION = 1;
		internal const int HEADER_SIZE = 32;

		private readonly string journalDir;
		private readonly MetadataStore metadata;
		private readonly SparseIndex index;
		private readonly IAtomicFileOperation atomicOp;
		private readonly object writeLock = new();

		private FileStream activeStream;
		private int activeFileSequence;
		private long activeFirstEntryId;
		private long activeLastEntryId;
		private int activeEventCount;
		private bool disposed;

		internal JournalWriter(string journalDir, MetadataStore metadata, SparseIndex index, IAtomicFileOperation atomicOp)
		{
			if (journalDir == null) throw new ArgumentNullException(nameof(journalDir));
			if (metadata == null) throw new ArgumentNullException(nameof(metadata));
			if (index == null) throw new ArgumentNullException(nameof(index));
			if (atomicOp == null) throw new ArgumentNullException(nameof(atomicOp));

			this.journalDir = journalDir;
			this.metadata = metadata;
			this.index = index;
			this.atomicOp = atomicOp;

			Directory.CreateDirectory(journalDir);
		}

		internal void Initialize()
		{
			if (metadata.CurrentFileSequence > 0)
			{
				activeFileSequence = metadata.CurrentFileSequence;
				string activePath = GetJournalFilePath(activeFileSequence);

				if (File.Exists(activePath))
				{
					activeStream = new FileStream(activePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096);
					RecoverActiveFile();
				}
				else
				{
					OpenNewFile(activeFileSequence);
				}
			}
			else
			{
				OpenNewFile(1);
			}
		}

		internal void AppendRecord(byte[] record, long entryId)
		{
			if (record == null) throw new ArgumentNullException(nameof(record));
			if (disposed) throw new ObjectDisposedException(nameof(JournalWriter));

			lock (writeLock)
			{
				int maxFileSize = metadata.MaxFileSizeBytes;
				if (activeStream.Position + record.Length > maxFileSize && activeEventCount > 0)
				{
					SealAndRollover();
				}

				activeStream.Write(record, 0, record.Length);
				activeStream.Flush(flushToDisk: true);

				bool isFirstRecordInFile = activeFirstEntryId == 0;
				if (isFirstRecordInFile)
					activeFirstEntryId = entryId;

				activeLastEntryId = entryId;
				activeEventCount++;

				metadata.LastWrittenEntryId = entryId;
				metadata.TotalEventCount++;
				metadata.TotalNonSkippedCount++;

				if (isFirstRecordInFile)
				{
					index.AddEntry(activeFirstEntryId, activeFileSequence, activeLastEntryId);
				}
				else
				{
					index.UpdateLastEntry(activeLastEntryId);
				}
			}
		}

		internal void FlushAndSeal()
		{
			lock (writeLock)
			{
				if (activeEventCount > 0)
				{
					SealAndRollover();
				}
			}
		}

		private void SealAndRollover()
		{
			UpdateFileHeader();
			activeStream.Flush(flushToDisk: true);
			activeStream.Dispose();

			int nextSequence = activeFileSequence + 1;
			OpenNewFile(nextSequence);
		}

		private void OpenNewFile(int sequence)
		{
			activeFileSequence = sequence;
			activeFirstEntryId = 0;
			activeLastEntryId = 0;
			activeEventCount = 0;

			string path = GetJournalFilePath(sequence);
			activeStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 4096);

			WriteEmptyHeader();

			metadata.CurrentFileSequence = sequence;
			// Persist CurrentFileSequence immediately so that any subsequent instance
			// (e.g. a rehydration) can open the correct journal file even if fewer than
			// PERSIST_METADATA_INTERVAL writes have occurred since the last flush.
			metadata.Save();
		}

		private void WriteEmptyHeader()
		{
			byte[] header = new byte[HEADER_SIZE];
			Buffer.BlockCopy(MAGIC, 0, header, 0, 4);
			BitConverter.TryWriteBytes(header.AsSpan(4, 2), FORMAT_VERSION);
			BitConverter.TryWriteBytes(header.AsSpan(6, 4), activeFileSequence);

			activeStream.Write(header, 0, HEADER_SIZE);
			activeStream.Flush(flushToDisk: true);
		}

		private void UpdateFileHeader()
		{
			activeStream.Seek(10, SeekOrigin.Begin);
			byte[] buf = new byte[20];
			int offset = 0;
			BitConverter.TryWriteBytes(buf.AsSpan(offset, 8), activeFirstEntryId); offset += 8;
			BitConverter.TryWriteBytes(buf.AsSpan(offset, 8), activeLastEntryId); offset += 8;
			BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), activeEventCount);
			activeStream.Write(buf, 0, buf.Length);
			activeStream.Seek(0, SeekOrigin.End);
		}

		private void RecoverActiveFile()
		{
			if (activeStream.Length < HEADER_SIZE)
			{
				activeStream.Dispose();
				OpenNewFile(activeFileSequence);
				return;
			}

			activeStream.Seek(0, SeekOrigin.Begin);
			byte[] header = new byte[HEADER_SIZE];
			int bytesRead = activeStream.Read(header, 0, HEADER_SIZE);
			bool magicOk = bytesRead >= HEADER_SIZE && header[0] == MAGIC[0] && header[1] == MAGIC[1] && header[2] == MAGIC[2] && header[3] == MAGIC[3];
			if (!magicOk)
			{
				activeStream.Dispose();
				OpenNewFile(activeFileSequence);
				return;
			}

			activeFirstEntryId = 0;
			activeLastEntryId = 0;
			activeEventCount = 0;
			long lastValidPosition = HEADER_SIZE;

			while (activeStream.Position < activeStream.Length)
			{
				long recordStart = activeStream.Position;

				byte[] lenBuf = new byte[4];
				int read = activeStream.Read(lenBuf, 0, 4);
				if (read < 4) break;

				int recordLen = BitConverter.ToInt32(lenBuf, 0);
				if (recordLen <= 0 || recordStart + 4 + recordLen > activeStream.Length)
					break;

				byte[] recordBody = new byte[recordLen];
				read = activeStream.Read(recordBody, 0, recordLen);
				if (read < recordLen) break;

				bool crcOk = BinaryEventCodec.ValidateCrc(recordBody, recordLen);
				if (!crcOk)
					break;

				long entryId = BinaryEventCodec.PeekEntryId(recordBody);
				if (activeFirstEntryId == 0)
					activeFirstEntryId = entryId;
				activeLastEntryId = entryId;
				activeEventCount++;
				lastValidPosition = activeStream.Position;
			}

			if (activeStream.Length > lastValidPosition)
			{
				activeStream.SetLength(lastValidPosition);
			}

			if (activeEventCount > 0 && activeFirstEntryId > 0)
			{
				var existingEntry = index.GetEntryByFileNumber(activeFileSequence);
				if (existingEntry == null)
				{
					index.AddEntry(activeFirstEntryId, activeFileSequence, activeLastEntryId);
				}

				// Fix LastWrittenEntryId if meta.bin was persisted before the crash:
				// activeLastEntryId is ground truth from the actual file bytes.
				if (activeLastEntryId > metadata.LastWrittenEntryId)
					metadata.LastWrittenEntryId = activeLastEntryId;
			}

			activeStream.Seek(0, SeekOrigin.End);
		}

		// Distill: rewrites the journal files in place keeping only the records that
		// shouldKeep(entryId) accepts. Stage 3: two-phase hot-trim.
		//
		// Phase 1 (LOCK-FREE): for each sealed file (FileNumber < activeFileSequence
		// at snapshot time), it is rewritten to `.new` applying shouldKeep. Sealed
		// files are immutable (nothing is appended to them), so this phase can
		// run without taking writeLock — producers keep doing AppendRecord
		// freely on the active file.
		//
		// Phase 2 (UNDER writeLock): commit of the prepared `.new` files (AtomicReplace
		// + SparseIndex update) and processing of the active file (and of any
		// file that got sealed during phase 1 due to rollover). Short
		// window — it only processes the active file, not the large sealed ones.
		//
		// Returns the list of physically removed EntryIds so the caller can
		// clean auxiliary stores (SkipStore, EventElisionStorage).
		//
		// Guarantee: the caller must ensure that shouldKeep returns true for at
		// least one EntryId of the active file (invariant "the last record is not
		// physically elided"). This class does not validate the invariant; it trusts the caller.
		//
		// Snapshot semantics: shouldKeep captures the skipSet at snapshot time. If
		// reactions mark new events as elided during phases 1/2, those do not enter
		// this Distill (the next one will process them).
		internal List<long> Distill(Func<long, bool> shouldKeep)
		{
			if (shouldKeep == null) throw new ArgumentNullException(nameof(shouldKeep));

			var removed = new List<long>();

			// Phase 0: brief lock for an atomic snapshot of activeFileSequence + the list of sealed files.
			int activeSeqAtStart;
			List<int> sealedFileNumbers;
			lock (writeLock)
			{
				if (disposed) throw new ObjectDisposedException(nameof(JournalWriter));
				activeSeqAtStart = activeFileSequence;
				sealedFileNumbers = index.GetAllEntries()
					.Where(e => e.FileNumber < activeSeqAtStart)
					.Select(e => e.FileNumber)
					.ToList();
			}

			// Phase 1: LOCK-FREE. Prepares `.new` for each sealed file.
			var preparedSealed = new List<PreparedDistilledFile>();
			foreach (var fileNumber in sealedFileNumbers)
			{
				var prepared = PrepareFilteredFile(fileNumber, shouldKeep, removed);
				preparedSealed.Add(prepared);
				TestHookBetweenPhase1Files?.Invoke();
			}

			// Phase 2: lock + commit + process the tail (active + any rollover that occurred in phase 1).
			lock (writeLock)
			{
				if (disposed) throw new ObjectDisposedException(nameof(JournalWriter));

				foreach (var prepared in preparedSealed)
				{
					// Defensive: if the original file is no longer in the index (unlikely
					// without another purger), skip it and clean up the orphan `.new`.
					if (index.GetEntryByFileNumber(prepared.FileNumber) == null)
					{
						string newPath = GetJournalFilePath(prepared.FileNumber) + ".new";
						try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
						continue;
					}
					CommitPreparedFile(prepared, isActive: false);
				}

				// Tail: files with FileNumber >= activeSeqAtStart still in the index.
				// May include the former-active one (if there was a rollover) AND the current active one.
				var tailFileNumbers = index.GetAllEntries()
					.Where(e => e.FileNumber >= activeSeqAtStart)
					.Select(e => e.FileNumber)
					.ToList();

				foreach (var fileNumber in tailFileNumbers)
				{
					bool isActive = fileNumber == activeFileSequence;

					if (isActive && activeStream != null)
					{
						activeStream.Flush(flushToDisk: true);
						activeStream.Dispose();
						activeStream = null;
					}

					var prepared = PrepareFilteredFile(fileNumber, shouldKeep, removed);
					CommitPreparedFile(prepared, isActive);
				}

				// Recompute metadata counts from the physical state after Distill.
				long realCount = ComputeRealTotalEventCount();
				metadata.TotalEventCount = realCount;
				// TotalNonSkippedCount is recomputed by the caller with the new SkipStore.
			}
			return removed;
		}

		// Reads filePath, filters with shouldKeep, writes to filePath.new. Lock-free —
		// the caller must guarantee that filePath is sealed (receives no appends) or that
		// the writeLock is held. The CALLER must call CommitPreparedFile under
		// writeLock to make the replacement effective.
		private PreparedDistilledFile PrepareFilteredFile(int fileNumber, Func<long, bool> shouldKeep, List<long> removed)
		{
			string filePath = GetJournalFilePath(fileNumber);
			string newPath = filePath + ".new";

			if (!File.Exists(filePath))
			{
				return new PreparedDistilledFile { FileNumber = fileNumber, ResultsInEmpty = true };
			}

			// Clean up any orphan .new from a previous incomplete Distill.
			try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }

			int survivorCount = 0;
			long newFirstEntryId = 0;
			long newLastEntryId = 0;

			using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 262144, FileOptions.SequentialScan))
			using (var output = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None, 262144))
			{
				byte[] header = new byte[HEADER_SIZE];
				Buffer.BlockCopy(MAGIC, 0, header, 0, 4);
				BitConverter.TryWriteBytes(header.AsSpan(4, 2), FORMAT_VERSION);
				BitConverter.TryWriteBytes(header.AsSpan(6, 4), fileNumber);
				output.Write(header, 0, HEADER_SIZE);

				if (input.Length > HEADER_SIZE)
				{
					input.Seek(HEADER_SIZE, SeekOrigin.Begin);
					byte[] lenBuf = new byte[4];
					while (input.Position < input.Length)
					{
						int read = input.Read(lenBuf, 0, 4);
						if (read < 4) break;

						int bodyLen = BitConverter.ToInt32(lenBuf, 0);
						if (bodyLen <= 0 || input.Position + bodyLen > input.Length) break;

						byte[] body = new byte[bodyLen];
						read = input.Read(body, 0, bodyLen);
						if (read < bodyLen) break;

						if (!BinaryEventCodec.ValidateCrc(body, bodyLen)) break;

						long entryId = BinaryEventCodec.PeekEntryId(body);
						if (shouldKeep(entryId))
						{
							output.Write(lenBuf, 0, 4);
							output.Write(body, 0, bodyLen);
							if (survivorCount == 0) newFirstEntryId = entryId;
							newLastEntryId = entryId;
							survivorCount++;
						}
						else
						{
							removed.Add(entryId);
						}
					}
				}

				if (survivorCount > 0)
				{
					output.Seek(10, SeekOrigin.Begin);
					byte[] hdr = new byte[20];
					int offset = 0;
					BitConverter.TryWriteBytes(hdr.AsSpan(offset, 8), newFirstEntryId); offset += 8;
					BitConverter.TryWriteBytes(hdr.AsSpan(offset, 8), newLastEntryId); offset += 8;
					BitConverter.TryWriteBytes(hdr.AsSpan(offset, 4), survivorCount);
					output.Write(hdr, 0, 20);
				}
				output.Flush(flushToDisk: true);
			}

			if (survivorCount == 0)
			{
				try { File.Delete(newPath); } catch { }
				return new PreparedDistilledFile { FileNumber = fileNumber, ResultsInEmpty = true };
			}

			return new PreparedDistilledFile
			{
				FileNumber = fileNumber,
				ResultsInEmpty = false,
				NewFirstEntryId = newFirstEntryId,
				NewLastEntryId = newLastEntryId,
				NewEventCount = survivorCount
			};
		}

		// Commit of the prepared replacement. Must run under writeLock.
		// For the active file: the caller must have closed activeStream BEFORE.
		private void CommitPreparedFile(PreparedDistilledFile prepared, bool isActive)
		{
			string filePath = GetJournalFilePath(prepared.FileNumber);
			string newPath = filePath + ".new";

			if (prepared.ResultsInEmpty)
			{
				try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
				if (File.Exists(filePath)) File.Delete(filePath);
				index.RemoveByFileNumber(prepared.FileNumber);

				if (isActive)
				{
					// Defensive case: if the active file ended up empty (should not happen under
					// the caller's invariant), reopen a new file with a clean header.
					OpenNewFile(activeFileSequence);
				}
			}
			else
			{
				if (!File.Exists(filePath))
				{
					// Defensive: if the original was deleted between prepare and commit, move .new.
					File.Move(newPath, filePath);
				}
				else
				{
					atomicOp.AtomicReplace(newPath, filePath);
				}

				// Update SparseIndex: remove the old entry, add a new one with updated first/last.
				// The index ordering is preserved because the per-file ranges remain
				// disjoint and monotonic (Distill only shrinks ranges, it does not interleave them).
				index.RemoveByFileNumber(prepared.FileNumber);
				index.AddEntry(prepared.NewFirstEntryId, prepared.FileNumber, prepared.NewLastEntryId);

				if (isActive)
				{
					activeStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096);
					activeStream.Seek(0, SeekOrigin.End);
					activeFirstEntryId = prepared.NewFirstEntryId;
					activeLastEntryId = prepared.NewLastEntryId;
					activeEventCount = prepared.NewEventCount;
				}
			}
		}

		// Test seam: invoked between the processing of each sealed file in phase 1
		// (lock-free). Tests use it to stall phase 1 and verify that producers
		// can do AppendRecord concurrently. Production never sets it.
		internal Action TestHookBetweenPhase1Files;

		// Computes the exact total event count by reading the EventCount field from every
		// sealed file header (32 bytes per file, no record scanning) plus the in-memory
		// activeEventCount for the open file. This is O(N_files * 32 bytes) — fast even for
		// thousands of files — and is the ground truth that is immune to meta.bin staleness.
		internal long ComputeRealTotalEventCount()
		{
			long total = 0;
			var allEntries = index.GetAllEntries();

			foreach (var entry in allEntries)
			{
				if (entry.FileNumber == activeFileSequence)
				{
					total += activeEventCount;
				}
				else
				{
					string path = GetJournalFilePath(entry.FileNumber);
					var header = ReadFileHeader(path);
					if (header != null) total += header.EventCount;
				}
			}

			return total;
		}

		internal string GetJournalFilePath(int sequence)
		{
			return Path.Combine(journalDir, $"journal_{sequence:D6}.bin");
		}

		internal static JournalFileHeader ReadFileHeader(string filePath)
		{
			if (filePath == null) throw new ArgumentNullException(nameof(filePath));
			if (!File.Exists(filePath)) return null;

			byte[] header = new byte[HEADER_SIZE];
			using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				if (fs.Read(header, 0, HEADER_SIZE) < HEADER_SIZE) return null;
			}

			if (header[0] != MAGIC[0] || header[1] != MAGIC[1] || header[2] != MAGIC[2] || header[3] != MAGIC[3])
				return null;

			return new JournalFileHeader
			{
				Version = BitConverter.ToUInt16(header, 4),
				FileSequence = BitConverter.ToInt32(header, 6),
				FirstEntryId = BitConverter.ToInt64(header, 10),
				LastEntryId = BitConverter.ToInt64(header, 18),
				EventCount = BitConverter.ToInt32(header, 26)
			};
		}

		~JournalWriter()
		{
			Dispose();
		}

		public void Dispose()
		{
			if (!disposed)
			{
				disposed = true;
				GC.SuppressFinalize(this);
				if (activeStream != null)
				{
					if (activeEventCount > 0)
						UpdateFileHeader();
					activeStream.Flush(flushToDisk: true);
					activeStream.Dispose();
				}
			}
		}
	}

	internal sealed class JournalFileHeader
	{
		internal ushort Version { get; set; }
		internal int FileSequence { get; set; }
		internal long FirstEntryId { get; set; }
		internal long LastEntryId { get; set; }
		internal int EventCount { get; set; }
	}

	// Result of Distill's phase 1 (PrepareFilteredFile): describes what the
	// phase 2 commit (CommitPreparedFile) must do with the file. ResultsInEmpty
	// implies deletion of the original (all records were filtered out); otherwise
	// the .new already has the survivors written and only the AtomicReplace remains.
	internal sealed class PreparedDistilledFile
	{
		internal int FileNumber;
		internal bool ResultsInEmpty;
		internal long NewFirstEntryId;
		internal long NewLastEntryId;
		internal int NewEventCount;
	}
}
