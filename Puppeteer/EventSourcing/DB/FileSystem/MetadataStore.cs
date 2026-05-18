using System;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class MetadataStore
	{
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'M', (byte)'M' };
		private const ushort FORMAT_VERSION = 1;
		private const int FILE_SIZE = 48;

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;

		internal int CurrentFileSequence { get; set; }
		internal long LastWrittenEntryId { get; set; }
		internal long TotalEventCount { get; set; }
		internal long TotalNonSkippedCount { get; set; }
		internal byte CompressionFlag { get; set; }
		internal byte EncryptionFlag { get; set; }
		internal int MaxFileSizeBytes { get; set; }

		internal MetadataStore(string filePath, IAtomicFileOperation atomicOp)
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

			CurrentFileSequence = BitConverter.ToInt32(data, offset); offset += 4;
			LastWrittenEntryId = BitConverter.ToInt64(data, offset); offset += 8;
			TotalEventCount = BitConverter.ToInt64(data, offset); offset += 8;
			TotalNonSkippedCount = BitConverter.ToInt64(data, offset); offset += 8;
			CompressionFlag = data[offset++];
			EncryptionFlag = data[offset++];
			MaxFileSizeBytes = BitConverter.ToInt32(data, offset); offset += 4;

			return true;
		}

		internal void Save()
		{
			byte[] data = new byte[FILE_SIZE];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), CurrentFileSequence); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 8), LastWrittenEntryId); offset += 8;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 8), TotalEventCount); offset += 8;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 8), TotalNonSkippedCount); offset += 8;
			data[offset++] = CompressionFlag;
			data[offset++] = EncryptionFlag;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), MaxFileSizeBytes); offset += 4;

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);
		}

		internal void InitializeDefaults(int maxFileSizeBytes = 4 * 1024 * 1024, byte compressionFlag = 0, byte encryptionFlag = 0)
		{
			CurrentFileSequence = 0;
			LastWrittenEntryId = 0;
			TotalEventCount = 0;
			TotalNonSkippedCount = 0;
			CompressionFlag = compressionFlag;
			EncryptionFlag = encryptionFlag;
			MaxFileSizeBytes = maxFileSizeBytes;
		}
	}
}
