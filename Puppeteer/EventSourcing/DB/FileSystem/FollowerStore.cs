using System;
using System.Collections.Generic;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class FollowerStore
	{
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'F', (byte)'L' };
		private const ushort FORMAT_VERSION = 1;
		private const int HEADER_SIZE = 10;
		private const int RECORD_SIZE = 12;

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;
		private readonly Dictionary<int, long> followers = new();

		internal FollowerStore(string filePath, IAtomicFileOperation atomicOp)
		{
			if (filePath == null) throw new ArgumentNullException(nameof(filePath));
			if (atomicOp == null) throw new ArgumentNullException(nameof(atomicOp));

			this.filePath = filePath;
			this.atomicOp = atomicOp;
		}

		internal void Load()
		{
			atomicOp.RecoverFromIncompleteOperation(filePath);
			followers.Clear();

			if (!File.Exists(filePath)) return;

			byte[] data = File.ReadAllBytes(filePath);
			if (data.Length < HEADER_SIZE) return;

			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;

			for (int i = 0; i < count && offset + RECORD_SIZE <= data.Length; i++)
			{
				int followerId = BitConverter.ToInt32(data, offset); offset += 4;
				long lastEntryId = BitConverter.ToInt64(data, offset); offset += 8;
				followers[followerId] = lastEntryId;
			}
		}

		internal void Save()
		{
			int size = HEADER_SIZE + followers.Count * RECORD_SIZE;
			byte[] data = new byte[size];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), followers.Count); offset += 4;

			foreach (var kvp in followers)
			{
				BitConverter.TryWriteBytes(data.AsSpan(offset, 4), kvp.Key); offset += 4;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), kvp.Value); offset += 8;
			}

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);
		}

		internal long GetLastProcessedEntryId(int followerId)
		{
			return followers.TryGetValue(followerId, out long entryId) ? entryId : 0;
		}

		internal void SaveLastProcessedEntryId(int followerId, long entryId)
		{
			if (followerId <= 0) throw new LanguageException($"Follower id '{followerId}' must be greater than zero");
			if (entryId <= 0) throw new LanguageException($"Last processed entry id '{entryId}' must be greater than zero");

			followers[followerId] = entryId;
			Save();
		}
	}
}
