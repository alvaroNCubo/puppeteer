using System;
using System.Collections.Generic;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class SparseIndex
	{
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'I', (byte)'X' };
		private const ushort FORMAT_VERSION = 1;
		private const int HEADER_SIZE = 10;
		private const int ENTRY_SIZE = 20;

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;
		private readonly List<IndexEntry> entries = new();

		internal int Count => entries.Count;

		internal SparseIndex(string filePath, IAtomicFileOperation atomicOp)
		{
			if (filePath == null) throw new ArgumentNullException(nameof(filePath));
			if (atomicOp == null) throw new ArgumentNullException(nameof(atomicOp));

			this.filePath = filePath;
			this.atomicOp = atomicOp;
		}

		internal bool Load()
		{
			atomicOp.RecoverFromIncompleteOperation(filePath);

			if (!File.Exists(filePath))
				return false;

			byte[] data = File.ReadAllBytes(filePath);
			if (data.Length < HEADER_SIZE)
				return false;

			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return false;

			int offset = 6;
			int count = BitConverter.ToInt32(data, offset); offset += 4;

			entries.Clear();
			for (int i = 0; i < count && offset + ENTRY_SIZE <= data.Length; i++)
			{
				long firstEntryId = BitConverter.ToInt64(data, offset); offset += 8;
				int fileNumber = BitConverter.ToInt32(data, offset); offset += 4;
				long lastEntryId = BitConverter.ToInt64(data, offset); offset += 8;

				entries.Add(new IndexEntry(firstEntryId, fileNumber, lastEntryId));
			}

			return true;
		}

		internal void Save()
		{
			int size = HEADER_SIZE + entries.Count * ENTRY_SIZE;
			byte[] data = new byte[size];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), entries.Count); offset += 4;

			foreach (var entry in entries)
			{
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), entry.FirstEntryId); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 4), entry.FileNumber); offset += 4;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), entry.LastEntryId); offset += 8;
			}

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);
		}

		internal void AddEntry(long firstEntryId, int fileNumber, long lastEntryId)
		{
			entries.Add(new IndexEntry(firstEntryId, fileNumber, lastEntryId));
		}

		internal void UpdateLastEntry(long lastEntryId)
		{
			if (entries.Count == 0) throw new InvalidOperationException("No entries in index to update.");
			var last = entries[entries.Count - 1];
			entries[entries.Count - 1] = new IndexEntry(last.FirstEntryId, last.FileNumber, lastEntryId);
		}

		internal int FindFileNumberForEntryId(long entryId)
		{
			if (entries.Count == 0) return -1;

			int lo = 0, hi = entries.Count - 1;
			int result = -1;

			while (lo <= hi)
			{
				int mid = lo + (hi - lo) / 2;
				if (entries[mid].FirstEntryId <= entryId)
				{
					result = mid;
					lo = mid + 1;
				}
				else
				{
					hi = mid - 1;
				}
			}

			return result >= 0 ? entries[result].FileNumber : entries[0].FileNumber;
		}

		internal IndexEntry GetEntry(int index)
		{
			if (index < 0 || index >= entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
			return entries[index];
		}

		internal IndexEntry GetEntryByFileNumber(int fileNumber)
		{
			for (int i = 0; i < entries.Count; i++)
			{
				if (entries[i].FileNumber == fileNumber)
					return entries[i];
			}
			return null;
		}

		internal void RemoveByFileNumber(int fileNumber)
		{
			for (int i = entries.Count - 1; i >= 0; i--)
			{
				if (entries[i].FileNumber == fileNumber)
				{
					entries.RemoveAt(i);
					return;
				}
			}
		}

		internal List<int> GetAllFileNumbers()
		{
			var result = new List<int>(entries.Count);
			foreach (var entry in entries)
			{
				result.Add(entry.FileNumber);
			}
			return result;
		}

		internal IReadOnlyList<IndexEntry> GetAllEntries()
		{
			return entries.AsReadOnly();
		}
	}

	internal sealed class IndexEntry
	{
		internal long FirstEntryId { get; }
		internal int FileNumber { get; }
		internal long LastEntryId { get; }

		internal IndexEntry(long firstEntryId, int fileNumber, long lastEntryId)
		{
			FirstEntryId = firstEntryId;
			FileNumber = fileNumber;
			LastEntryId = lastEntryId;
		}
	}
}
