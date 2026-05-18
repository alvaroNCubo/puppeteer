using System;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class ReplicationProgress
	{
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'R', (byte)'P' };
		private const ushort FORMAT_VERSION = 1;
		private const int FILE_SIZE = 22;

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;

		internal long LastReplicatedEntryId { get; set; }

		internal ReplicationProgress(string filePath, IAtomicFileOperation atomicOp)
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
			if (data.Length < FILE_SIZE)
				return false;

			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return false;

			int offset = 4;
			ushort version = BitConverter.ToUInt16(data, offset); offset += 2;
			if (version != FORMAT_VERSION)
				return false;

			LastReplicatedEntryId = BitConverter.ToInt64(data, offset); offset += 8;
			// 8 bytes reserved

			return true;
		}

		internal void Save()
		{
			byte[] data = new byte[FILE_SIZE];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 8), LastReplicatedEntryId); offset += 8;
			// 8 bytes reserved (zeros)

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);
		}
	}
}
