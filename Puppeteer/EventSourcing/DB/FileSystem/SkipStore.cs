using System;
using System.Collections.Generic;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class SkipStore
	{
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'S', (byte)'K' };
		private const ushort FORMAT_VERSION = 1;
		private const int HEADER_SIZE = 10;

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;

		internal SkipStore(string filePath, IAtomicFileOperation atomicOp)
		{
			if (filePath == null) throw new ArgumentNullException(nameof(filePath));
			if (atomicOp == null) throw new ArgumentNullException(nameof(atomicOp));

			this.filePath = filePath;
			this.atomicOp = atomicOp;
		}

		// Reads only the 10-byte header to obtain the skip count without parsing any IDs.
		internal int LoadCount()
		{
			atomicOp.RecoverFromIncompleteOperation(filePath);
			if (!File.Exists(filePath)) return 0;

			byte[] header = new byte[HEADER_SIZE];
			using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (fs.Read(header, 0, HEADER_SIZE) < HEADER_SIZE) return 0;

			if (header[0] != MAGIC[0] || header[1] != MAGIC[1] || header[2] != MAGIC[2] || header[3] != MAGIC[3])
				return 0;

			return BitConverter.ToInt32(header, 6);
		}

		internal HashSet<long> Load()
		{
			atomicOp.RecoverFromIncompleteOperation(filePath);

			var result = new HashSet<long>();
			if (!File.Exists(filePath)) return result;

			byte[] data = File.ReadAllBytes(filePath);
			if (data.Length < HEADER_SIZE) return result;

			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return result;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;

			for (int i = 0; i < count && offset + 8 <= data.Length; i++)
			{
				long entryId = BitConverter.ToInt64(data, offset);
				offset += 8;
				result.Add(entryId);
			}

			return result;
		}

		internal void Save(SortedSet<long> skipIds)
		{
			if (skipIds == null) throw new ArgumentNullException(nameof(skipIds));

			int size = HEADER_SIZE + skipIds.Count * 8;
			byte[] data = new byte[size];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), skipIds.Count); offset += 4;

			foreach (long id in skipIds)
			{
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), id);
				offset += 8;
			}

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);
		}

		// Returns the count of entryIds that were genuinely new (not already in the skip set).
		// The caller must use this count to decrement metadata.TotalNonSkippedCount.
		internal int AddSkips(IEnumerable<long> newSkipIds)
		{
			if (newSkipIds == null) throw new ArgumentNullException(nameof(newSkipIds));

			var existing = Load();
			var sorted = new SortedSet<long>(existing);
			int newCount = 0;
			foreach (long id in newSkipIds)
			{
				if (sorted.Add(id)) newCount++;
			}

			if (newCount > 0) Save(sorted);
			return newCount;
		}

		internal List<int> FindFullySkippedFiles(SparseIndex index, HashSet<long> skipSet, string journalDir)
		{
			if (index == null) throw new ArgumentNullException(nameof(index));
			if (skipSet == null) throw new ArgumentNullException(nameof(skipSet));
			if (journalDir == null) throw new ArgumentNullException(nameof(journalDir));

			var fullySkipped = new List<int>();
			var allEntries = index.GetAllEntries();
			var allFileNumbers = index.GetAllFileNumbers();

			if (allFileNumbers.Count <= 1) return fullySkipped;

			int activeFileNumber = allFileNumbers[allFileNumbers.Count - 1];

			foreach (var entry in allEntries)
			{
				if (entry.FileNumber == activeFileNumber) continue;

				if (entry.FirstEntryId <= 0 || entry.LastEntryId <= 0) continue;

				bool allSkipped = true;
				for (long id = entry.FirstEntryId; id <= entry.LastEntryId; id++)
				{
					if (!skipSet.Contains(id))
					{
						allSkipped = false;
						break;
					}
				}

				if (allSkipped)
				{
					fullySkipped.Add(entry.FileNumber);
				}
			}

			return fullySkipped;
		}

		internal void PurgeFullySkippedFiles(SparseIndex index, HashSet<long> skipSet, string journalDir)
		{
			if (index == null) throw new ArgumentNullException(nameof(index));
			if (skipSet == null) throw new ArgumentNullException(nameof(skipSet));
			if (journalDir == null) throw new ArgumentNullException(nameof(journalDir));

			var fullySkipped = FindFullySkippedFiles(index, skipSet, journalDir);

			foreach (int fileNumber in fullySkipped)
			{
				string filePath = Path.Combine(journalDir, $"journal_{fileNumber:D6}.bin");
				if (File.Exists(filePath))
				{
					File.Delete(filePath);
				}
				index.RemoveByFileNumber(fileNumber);
			}
		}
	}
}
