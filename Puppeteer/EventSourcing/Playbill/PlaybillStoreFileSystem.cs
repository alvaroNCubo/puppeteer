using Puppeteer.EventSourcing.DB.FileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Text;

namespace Puppeteer.EventSourcing.Playbill
{
	// FileSystem backend of the Playbill. Lives in path/ActorName/playbill/ —
	// sibling of the Diary's journal/ directory. Three files:
	//
	//   playbill/schemas.bin  — append-only log of schemas (header + entries)
	//   playbill/records.bin  — append-only log of records (header + entries)
	//   playbill/index.bin    — EntryId -> offset index (dense map for simplicity)
	//
	// Own format (does NOT reuse the journal's BinaryEventCodec): simpler, without
	// compression/encryption — the playbill is operational evidence, not transactional
	// load, so it does not need the same optimizations.
	//
	// Distill: reads the live EntryIds directly from the actor's journal files
	// (path/ActorName/journal/journal_NNNNNN.bin), atomic shadow-swap of records.bin
	// and rebuild of the index.
	internal sealed class PlaybillStoreFileSystem : PlaybillStore
	{
		private static readonly byte[] SCHEMAS_MAGIC = new byte[] { (byte)'P', (byte)'B', (byte)'S', (byte)'C' };
		private static readonly byte[] RECORDS_MAGIC = new byte[] { (byte)'P', (byte)'B', (byte)'R', (byte)'C' };
		private static readonly byte[] INDEX_MAGIC = new byte[] { (byte)'P', (byte)'B', (byte)'I', (byte)'X' };
		private const ushort FORMAT_VERSION = 1;
		private const int FILE_HEADER_SIZE = 6; // 4 magic + 2 version

		private readonly string playbillDir;
		private readonly string schemasFile;
		private readonly string recordsFile;
		private readonly string indexFile;
		private readonly string journalDir;
		private readonly object gate = new object();

		internal PlaybillStoreFileSystem(string actorName, string connectionString, IPuppeteerLogger logger)
			: base(actorName, connectionString, logger)
		{
			var parsed = new FileSystemConnectionString(connectionString);
			string actorBase = Path.Combine(parsed.Path, actorName);
			this.playbillDir = Path.Combine(actorBase, "playbill");
			this.journalDir = Path.Combine(actorBase, "journal");
			this.schemasFile = Path.Combine(playbillDir, "schemas.bin");
			this.recordsFile = Path.Combine(playbillDir, "records.bin");
			this.indexFile = Path.Combine(playbillDir, "index.bin");

			Directory.CreateDirectory(playbillDir);
			EnsureFileHeader(schemasFile, SCHEMAS_MAGIC);
			EnsureFileHeader(recordsFile, RECORDS_MAGIC);
			EnsureFileHeader(indexFile, INDEX_MAGIC);
		}

		private static void EnsureFileHeader(string filePath, byte[] magic)
		{
			if (File.Exists(filePath) && new FileInfo(filePath).Length >= FILE_HEADER_SIZE)
			{
				return;
			}
			byte[] header = new byte[FILE_HEADER_SIZE];
			Buffer.BlockCopy(magic, 0, header, 0, 4);
			BitConverter.TryWriteBytes(header.AsSpan(4, 2), FORMAT_VERSION);
			File.WriteAllBytes(filePath, header);
		}

		// === Schemas ===

		internal override void RegisterSchema(string schemaName, string declarations)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			ArgumentNullException.ThrowIfNull(declarations);

			lock (gate)
			{
				string existing = LoadSchemaUnlocked(schemaName);
				if (existing != null)
				{
					if (existing != declarations)
					{
						throw new LanguageException(
							$"Playbill schema '{schemaName}' is already registered with a different shape. " +
							$"Existing: '{existing}'. New: '{declarations}'. Schema drift requires migration.");
					}
				}
				else
				{
					AppendSchemaEntry(schemaName, declarations, DateTime.UtcNow);
				}
			}
			// Invoke OUTSIDE the lock to avoid deadlock if the subscriber
			// (e.g. Stage) calls back into the store on the same thread. It fires
			// both on an idempotent re-register and on a new register — the
			// cue receiver is also idempotent.
			OnSchemaRegistered?.Invoke(schemaName, declarations);
		}

		private string LoadSchemaUnlocked(string schemaName)
		{
			foreach (var (name, declarations, _) in EnumerateSchemas())
			{
				if (name == schemaName) return declarations;
			}
			return null;
		}

		private void AppendSchemaEntry(string schemaName, string declarations, DateTime createdAt)
		{
			byte[] nameBytes = Encoding.UTF8.GetBytes(schemaName);
			byte[] declBytes = Encoding.UTF8.GetBytes(declarations);
			if (nameBytes.Length > byte.MaxValue) throw new LanguageException($"Schema name '{schemaName}' is too long ({nameBytes.Length} bytes).");
			if (declBytes.Length > ushort.MaxValue) throw new LanguageException($"Declarations for schema '{schemaName}' too long ({declBytes.Length} bytes).");

			// Layout per entry:
			//   [4 bytes length prefix (excludes itself, includes CRC)]
			//   [1 byte nameLen][nameLen bytes UTF8 name]
			//   [2 bytes declLen][declLen bytes UTF8 declarations]
			//   [8 bytes CreatedAt.Ticks]
			//   [4 bytes CRC32]
			int bodySize = 1 + nameBytes.Length + 2 + declBytes.Length + 8;
			int totalAfterPrefix = bodySize + 4; // body + CRC
			int totalSize = 4 + totalAfterPrefix;

			byte[] buffer = new byte[totalSize];
			int offset = 0;
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), totalAfterPrefix); offset += 4;
			int bodyStart = offset;
			buffer[offset++] = (byte)nameBytes.Length;
			Buffer.BlockCopy(nameBytes, 0, buffer, offset, nameBytes.Length); offset += nameBytes.Length;
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), (ushort)declBytes.Length); offset += 2;
			Buffer.BlockCopy(declBytes, 0, buffer, offset, declBytes.Length); offset += declBytes.Length;
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 8), createdAt.Ticks); offset += 8;
			uint crc = ComputeCrc(buffer, bodyStart, offset - bodyStart);
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), crc);

			using (var fs = new FileStream(schemasFile, FileMode.Append, FileAccess.Write, FileShare.Read))
			{
				fs.Write(buffer, 0, buffer.Length);
				fs.Flush(flushToDisk: true);
			}
		}

		private IEnumerable<(string Name, string Declarations, DateTime CreatedAt)> EnumerateSchemas()
		{
			if (!File.Exists(schemasFile)) yield break;

			byte[] data;
			using (var fs = new FileStream(schemasFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				data = new byte[fs.Length];
				int read = 0;
				while (read < data.Length)
				{
					int n = fs.Read(data, read, data.Length - read);
					if (n <= 0) break;
					read += n;
				}
			}

			if (data.Length < FILE_HEADER_SIZE) yield break;
			if (data[0] != SCHEMAS_MAGIC[0] || data[1] != SCHEMAS_MAGIC[1] || data[2] != SCHEMAS_MAGIC[2] || data[3] != SCHEMAS_MAGIC[3])
				throw new LanguageException($"Playbill schemas file '{schemasFile}' has invalid magic header.");

			int offset = FILE_HEADER_SIZE;
			while (offset + 4 <= data.Length)
			{
				int recordLen = BitConverter.ToInt32(data, offset);
				if (recordLen <= 0 || offset + 4 + recordLen > data.Length) yield break;

				int bodyStart = offset + 4;
				int bodyEnd = bodyStart + recordLen - 4; // exclude CRC
				int crcOffset = bodyEnd;
				uint storedCrc = BitConverter.ToUInt32(data, crcOffset);
				uint computedCrc = ComputeCrc(data, bodyStart, bodyEnd - bodyStart);
				if (storedCrc != computedCrc) yield break;

				int cursor = bodyStart;
				int nameLen = data[cursor++];
				string name = Encoding.UTF8.GetString(data, cursor, nameLen); cursor += nameLen;
				ushort declLen = BitConverter.ToUInt16(data, cursor); cursor += 2;
				string decl = Encoding.UTF8.GetString(data, cursor, declLen); cursor += declLen;
				long ticks = BitConverter.ToInt64(data, cursor); cursor += 8;

				yield return (name, decl, new DateTime(ticks));

				offset += 4 + recordLen;
			}
		}

		internal override string GetSchemaDeclarations(string schemaName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			lock (gate)
			{
				return LoadSchemaUnlocked(schemaName);
			}
		}

		internal override IEnumerable<(string Name, string Declarations)> ListSchemas()
		{
			lock (gate)
			{
				var result = new List<(string, string)>();
				foreach (var (name, decl, _) in EnumerateSchemas())
				{
					result.Add((name, decl));
				}
				result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
				return result;
			}
		}

		// === Records ===

		internal override void WriteRecord(long entryId, string schemaName, string serializedParameters)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			ArgumentNullException.ThrowIfNull(serializedParameters);

			lock (gate)
			{
				if (LoadSchemaUnlocked(schemaName) == null)
				{
					throw new LanguageException($"Cannot write Playbill record: schema '{schemaName}' is not registered.");
				}
				if (LookupOffsetForEntryUnlocked(entryId) >= 0)
				{
					throw new LanguageException($"Playbill record for EntryId {entryId} already exists (expected at most one per entry).");
				}

				long appendedOffset = AppendRecordEntry(entryId, schemaName, serializedParameters);
				AppendIndexEntry(entryId, appendedOffset);
			}
			// Invoke OUTSIDE the lock to avoid deadlock if the subscriber
			// (e.g. Stage) calls back into the store on the same thread.
			OnRecordWritten?.Invoke(entryId, schemaName, serializedParameters);
		}

		private long AppendRecordEntry(long entryId, string schemaName, string serializedParameters)
		{
			byte[] nameBytes = Encoding.UTF8.GetBytes(schemaName);
			byte[] paramsBytes = Encoding.UTF8.GetBytes(serializedParameters);
			if (nameBytes.Length > byte.MaxValue) throw new LanguageException($"Schema name '{schemaName}' too long.");
			if (paramsBytes.Length > ushort.MaxValue) throw new LanguageException($"Serialized parameters too long ({paramsBytes.Length} bytes).");

			// Layout per record (after length prefix):
			//   [8 bytes EntryId]
			//   [1 byte nameLen][nameLen bytes UTF8]
			//   [2 bytes paramsLen][paramsLen bytes UTF8]
			//   [4 bytes CRC32]
			int bodySize = 8 + 1 + nameBytes.Length + 2 + paramsBytes.Length;
			int totalAfterPrefix = bodySize + 4;
			int totalSize = 4 + totalAfterPrefix;

			byte[] buffer = new byte[totalSize];
			int offset = 0;
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), totalAfterPrefix); offset += 4;
			int bodyStart = offset;
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 8), entryId); offset += 8;
			buffer[offset++] = (byte)nameBytes.Length;
			Buffer.BlockCopy(nameBytes, 0, buffer, offset, nameBytes.Length); offset += nameBytes.Length;
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), (ushort)paramsBytes.Length); offset += 2;
			Buffer.BlockCopy(paramsBytes, 0, buffer, offset, paramsBytes.Length); offset += paramsBytes.Length;
			uint crc = ComputeCrc(buffer, bodyStart, offset - bodyStart);
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), crc);

			long appendedAt;
			using (var fs = new FileStream(recordsFile, FileMode.Append, FileAccess.Write, FileShare.Read))
			{
				appendedAt = fs.Position;
				fs.Write(buffer, 0, buffer.Length);
				fs.Flush(flushToDisk: true);
			}
			return appendedAt;
		}

		// Index entry layout: [8 bytes EntryId][8 bytes offset in records.bin]
		private const int INDEX_ENTRY_SIZE = 16;

		private void AppendIndexEntry(long entryId, long offsetInRecords)
		{
			byte[] buffer = new byte[INDEX_ENTRY_SIZE];
			BitConverter.TryWriteBytes(buffer.AsSpan(0, 8), entryId);
			BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), offsetInRecords);
			using (var fs = new FileStream(indexFile, FileMode.Append, FileAccess.Write, FileShare.Read))
			{
				fs.Write(buffer, 0, buffer.Length);
				fs.Flush(flushToDisk: true);
			}
		}

		private long LookupOffsetForEntryUnlocked(long entryId)
		{
			if (!File.Exists(indexFile)) return -1;
			using (var fs = new FileStream(indexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				if (fs.Length < FILE_HEADER_SIZE) return -1;
				fs.Seek(FILE_HEADER_SIZE, SeekOrigin.Begin);
				byte[] buffer = new byte[INDEX_ENTRY_SIZE];
				while (fs.Position + INDEX_ENTRY_SIZE <= fs.Length)
				{
					int read = 0;
					while (read < INDEX_ENTRY_SIZE)
					{
						int n = fs.Read(buffer, read, INDEX_ENTRY_SIZE - read);
						if (n <= 0) return -1;
						read += n;
					}
					long id = BitConverter.ToInt64(buffer, 0);
					long off = BitConverter.ToInt64(buffer, 8);
					if (id == entryId) return off;
				}
			}
			return -1;
		}

		private IEnumerable<(long EntryId, long Offset)> EnumerateIndex()
		{
			if (!File.Exists(indexFile)) yield break;
			byte[] data;
			using (var fs = new FileStream(indexFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				data = new byte[fs.Length];
				int read = 0;
				while (read < data.Length)
				{
					int n = fs.Read(data, read, data.Length - read);
					if (n <= 0) break;
					read += n;
				}
			}
			if (data.Length < FILE_HEADER_SIZE) yield break;
			int offset = FILE_HEADER_SIZE;
			while (offset + INDEX_ENTRY_SIZE <= data.Length)
			{
				long id = BitConverter.ToInt64(data, offset);
				long off = BitConverter.ToInt64(data, offset + 8);
				yield return (id, off);
				offset += INDEX_ENTRY_SIZE;
			}
		}

		private (long EntryId, string SchemaName, string SerializedParameters)? ReadRecordAtOffset(long fileOffset)
		{
			if (fileOffset < 0) return null;
			using (var fs = new FileStream(recordsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				if (fileOffset + 4 > fs.Length) return null;
				fs.Seek(fileOffset, SeekOrigin.Begin);
				byte[] prefix = new byte[4];
				if (fs.Read(prefix, 0, 4) < 4) return null;
				int recordLen = BitConverter.ToInt32(prefix, 0);
				if (recordLen <= 4 || fs.Position + recordLen > fs.Length) return null;

				byte[] body = new byte[recordLen];
				int read = 0;
				while (read < body.Length)
				{
					int n = fs.Read(body, read, body.Length - read);
					if (n <= 0) return null;
					read += n;
				}

				int bodyContentLen = recordLen - 4;
				uint storedCrc = BitConverter.ToUInt32(body, bodyContentLen);
				uint computedCrc = ComputeCrc(body, 0, bodyContentLen);
				if (storedCrc != computedCrc) return null;

				int cursor = 0;
				long entryId = BitConverter.ToInt64(body, cursor); cursor += 8;
				int nameLen = body[cursor++];
				string name = Encoding.UTF8.GetString(body, cursor, nameLen); cursor += nameLen;
				ushort paramsLen = BitConverter.ToUInt16(body, cursor); cursor += 2;
				string ser = Encoding.UTF8.GetString(body, cursor, paramsLen);

				return (entryId, name, ser);
			}
		}

		internal override (string SchemaName, string SerializedParameters)? ReadRecord(long entryId)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");

			lock (gate)
			{
				long offset = LookupOffsetForEntryUnlocked(entryId);
				if (offset < 0) return null;
				var rec = ReadRecordAtOffset(offset);
				if (rec == null) return null;
				return (rec.Value.SchemaName, rec.Value.SerializedParameters);
			}
		}

		internal override IEnumerable<(long EntryId, string SerializedParameters)> ReadRecordsForSchema(string schemaName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);

			var result = new List<(long, string)>();
			lock (gate)
			{
				foreach (var entry in EnumerateAllRecords())
				{
					if (entry.SchemaName == schemaName)
					{
						result.Add((entry.EntryId, entry.SerializedParameters));
					}
				}
			}
			result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
			return result;
		}

		internal override void ReadRecordsAfter(long afterEntryId, List<PlaybillRecord> result)
		{
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();
			var temp = new List<PlaybillRecord>();
			lock (gate)
			{
				foreach (var entry in EnumerateAllRecords())
				{
					if (entry.EntryId > afterEntryId)
					{
						temp.Add(new PlaybillRecord(entry.EntryId, entry.SchemaName, entry.SerializedParameters));
					}
				}
			}
			temp.Sort((a, b) => a.EntryId.CompareTo(b.EntryId));
			result.AddRange(temp);
		}

		private IEnumerable<(long EntryId, string SchemaName, string SerializedParameters)> EnumerateAllRecords()
		{
			if (!File.Exists(recordsFile)) yield break;
			byte[] data;
			using (var fs = new FileStream(recordsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				data = new byte[fs.Length];
				int read = 0;
				while (read < data.Length)
				{
					int n = fs.Read(data, read, data.Length - read);
					if (n <= 0) break;
					read += n;
				}
			}
			if (data.Length < FILE_HEADER_SIZE) yield break;

			int offset = FILE_HEADER_SIZE;
			while (offset + 4 <= data.Length)
			{
				int recordLen = BitConverter.ToInt32(data, offset);
				if (recordLen <= 4 || offset + 4 + recordLen > data.Length) yield break;

				int bodyStart = offset + 4;
				int bodyContentLen = recordLen - 4;
				int crcOffset = bodyStart + bodyContentLen;
				uint storedCrc = BitConverter.ToUInt32(data, crcOffset);
				uint computedCrc = ComputeCrc(data, bodyStart, bodyContentLen);
				if (storedCrc != computedCrc) yield break;

				int cursor = bodyStart;
				long entryId = BitConverter.ToInt64(data, cursor); cursor += 8;
				int nameLen = data[cursor++];
				string name = Encoding.UTF8.GetString(data, cursor, nameLen); cursor += nameLen;
				ushort paramsLen = BitConverter.ToUInt16(data, cursor); cursor += 2;
				string ser = Encoding.UTF8.GetString(data, cursor, paramsLen);

				yield return (entryId, name, ser);

				offset += 4 + recordLen;
			}
		}

		// === Distill ===
		//
		// Reads the live EntryIds directly from the actor's journal files —
		// scans each journal_NNNNNN.bin skipping the 32-byte header
		// and extracting the EntryId of each record (4-byte length prefix + 1-byte
		// typeByte + 8-byte EntryId). Does not deserialize payloads — only headers.
		// Then rewrites records.bin omitting orphans and rebuilds index.bin.
		internal override void Distill()
		{
			lock (gate)
			{
				HashSet<long> aliveJournalEntries = LoadAliveJournalEntries();

				string newRecordsFile = recordsFile + ".new";
				string newIndexFile = indexFile + ".new";

				// Defensive cleanup of shadow files from an aborted Distill.
				if (File.Exists(newRecordsFile)) File.Delete(newRecordsFile);
				if (File.Exists(newIndexFile)) File.Delete(newIndexFile);

				EnsureFileHeader(newRecordsFile, RECORDS_MAGIC);
				EnsureFileHeader(newIndexFile, INDEX_MAGIC);

				using (var recOut = new FileStream(newRecordsFile, FileMode.Append, FileAccess.Write, FileShare.Read))
				using (var idxOut = new FileStream(newIndexFile, FileMode.Append, FileAccess.Write, FileShare.Read))
				{
					foreach (var entry in EnumerateAllRecords())
					{
						if (!aliveJournalEntries.Contains(entry.EntryId)) continue;

						long offsetBefore = recOut.Position;
						byte[] encoded = EncodeRecord(entry.EntryId, entry.SchemaName, entry.SerializedParameters);
						recOut.Write(encoded, 0, encoded.Length);

						byte[] idxEntry = new byte[INDEX_ENTRY_SIZE];
						BitConverter.TryWriteBytes(idxEntry.AsSpan(0, 8), entry.EntryId);
						BitConverter.TryWriteBytes(idxEntry.AsSpan(8, 8), offsetBefore);
						idxOut.Write(idxEntry, 0, idxEntry.Length);
					}
					recOut.Flush(flushToDisk: true);
					idxOut.Flush(flushToDisk: true);
				}

				// Atomic replacement — the Windows rename requires the destination
				// not to exist. File.Replace guarantees atomicity when both exist.
				File.Replace(newRecordsFile, recordsFile, destinationBackupFileName: null);
				File.Replace(newIndexFile, indexFile, destinationBackupFileName: null);
			}
		}

		private static byte[] EncodeRecord(long entryId, string schemaName, string serializedParameters)
		{
			byte[] nameBytes = Encoding.UTF8.GetBytes(schemaName);
			byte[] paramsBytes = Encoding.UTF8.GetBytes(serializedParameters);

			int bodySize = 8 + 1 + nameBytes.Length + 2 + paramsBytes.Length;
			int totalAfterPrefix = bodySize + 4;
			int totalSize = 4 + totalAfterPrefix;

			byte[] buffer = new byte[totalSize];
			int offset = 0;
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), totalAfterPrefix); offset += 4;
			int bodyStart = offset;
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 8), entryId); offset += 8;
			buffer[offset++] = (byte)nameBytes.Length;
			Buffer.BlockCopy(nameBytes, 0, buffer, offset, nameBytes.Length); offset += nameBytes.Length;
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), (ushort)paramsBytes.Length); offset += 2;
			Buffer.BlockCopy(paramsBytes, 0, buffer, offset, paramsBytes.Length); offset += paramsBytes.Length;
			uint crc = ComputeCrc(buffer, bodyStart, offset - bodyStart);
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), crc);

			return buffer;
		}

		// Scans the actor's journal files and extracts the set of live EntryIds.
		// Each file has a 32-byte header (JournalWriter.HEADER_SIZE)
		// followed by records with layout:
		//   [4 bytes length-after-prefix (includes CRC)]
		//   [1 byte typeByte][8 bytes EntryId][...payload...][4 bytes CRC]
		// Only the first 13 bytes of each record are read (4 prefix + 1 type +
		// 8 entryId) to build the set — the payload is not decoded.
		private HashSet<long> LoadAliveJournalEntries()
		{
			var alive = new HashSet<long>();
			if (!Directory.Exists(journalDir)) return alive;

			const int JOURNAL_FILE_HEADER_SIZE = 32;

			foreach (string file in Directory.EnumerateFiles(journalDir, "journal_*.bin"))
			{
				try
				{
					using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
					if (fs.Length < JOURNAL_FILE_HEADER_SIZE) continue;

					fs.Seek(JOURNAL_FILE_HEADER_SIZE, SeekOrigin.Begin);

					byte[] prefix = new byte[4];
					byte[] head = new byte[9]; // type + entryId

					while (fs.Position + 4 <= fs.Length)
					{
						long recordStart = fs.Position;
						if (fs.Read(prefix, 0, 4) < 4) break;
						int recordLen = BitConverter.ToInt32(prefix, 0);
						if (recordLen <= 4 || recordStart + 4 + recordLen > fs.Length) break;

						int readHead = 0;
						while (readHead < 9)
						{
							int n = fs.Read(head, readHead, 9 - readHead);
							if (n <= 0) break;
							readHead += n;
						}
						if (readHead < 9) break;

						long entryId = BitConverter.ToInt64(head, 1);
						alive.Add(entryId);

						// Advance to the next record: skip the rest of the body + CRC.
						long nextRecord = recordStart + 4 + recordLen;
						fs.Seek(nextRecord, SeekOrigin.Begin);
					}
				}
				catch (IOException e)
				{
					Logger.Error($"Distill scanning journal file '{file}': {e.Message}", e);
				}
			}

			return alive;
		}

		internal override MemoryStream Archive(DateTime startDate, DateTime endDate)
		{
			throw new NotImplementedException("Archive pending for PlaybillStoreFileSystem");
		}

		private static uint ComputeCrc(byte[] buffer, int offset, int length)
		{
			var crc32 = new Crc32();
			crc32.Append(buffer.AsSpan(offset, length));
			return BitConverter.ToUInt32(crc32.GetCurrentHash(), 0);
		}
	}
}
