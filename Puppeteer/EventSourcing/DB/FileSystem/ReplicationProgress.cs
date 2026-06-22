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

			// Same contract: a missing file is valid (new replication), but
			// corruption must throw. If we silenced it, the agent would rewrite
			// progress to 0 and re-send already-replicated records to the remote storage.
			if (!File.Exists(filePath))
				return false;

			byte[] data = File.ReadAllBytes(filePath);
			if (data.Length < FILE_SIZE)
				throw new LanguageException($"Replication progress file '{filePath}' exists but is truncated ({data.Length} bytes, expected at least {FILE_SIZE}). Back up the file; deleting it forces re-replication of all records from the local buffer (no data loss but may duplicate writes on the remote).");

			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				throw new LanguageException($"Replication progress file '{filePath}' has an invalid magic header (expected 'PPRP'). Corrupted or from an incompatible format.");

			int offset = 4;
			ushort version = BitConverter.ToUInt16(data, offset); offset += 2;
			if (version != FORMAT_VERSION)
				throw new LanguageException($"Replication progress file '{filePath}' has format version {version}, but this Puppeteer expects version {FORMAT_VERSION}. No automatic migration; contact maintainers.");

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
