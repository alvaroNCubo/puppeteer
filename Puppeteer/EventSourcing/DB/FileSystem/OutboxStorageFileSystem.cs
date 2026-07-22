using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	// Persistent journal-outbox row table for `.Outbox.Emit(...)`. The FileSystem
	// sibling of OutboxStorageInMemory: the same TryInsert / ReadUndelivered /
	// MarkDelivered surface, but the rows are durable across restarts (persisted to
	// outbox.bin via the atomic-replace file operation, exactly like ReactionStore's
	// checkpoints.bin and EventMaterializationStorageFileSystem's materialization.bin).
	//
	// The in-memory mirror (rows + byKey) is the read path; every mutation flushes
	// the whole table to disk under storeLock. TryInsert is called from inside the
	// diary's RecordOutboxWithCheckpoint critical section; nothing here calls back
	// into the diary, so there is no lock-ordering cycle.
	internal sealed class OutboxStorageFileSystem : OutboxStorage
	{
		// "PPOX" = Puppeteer OutboX. Distinct from the elision ("PPEL"),
		// materialization ("PPMT") and reaction ("PPRX"/"PPCP") markers so a file
		// mismatch is detectable.
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'O', (byte)'X' };
		private const ushort FORMAT_VERSION = 1;
		private const int HEADER_SIZE = 10;

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;
		private readonly object storeLock = new();

		// Insertion-ordered rows (already ascending by OutboxId) + a key index for
		// O(1) dedup. nextOutboxId is derived from the max persisted id on load.
		private readonly List<OutboxRecord> rows = new List<OutboxRecord>();
		private readonly Dictionary<string, OutboxRecord> byKey = new Dictionary<string, OutboxRecord>(StringComparer.Ordinal);
		private long nextOutboxId = 1;

		internal OutboxStorageFileSystem(string filePath, IAtomicFileOperation atomicOp)
		{
			ArgumentNullException.ThrowIfNull(filePath);
			ArgumentNullException.ThrowIfNull(atomicOp);

			this.filePath = filePath;
			this.atomicOp = atomicOp;

			Load();
		}

		private void Load()
		{
			atomicOp.RecoverFromIncompleteOperation(filePath);

			if (!File.Exists(filePath)) return;

			byte[] data = File.ReadAllBytes(filePath);
			if (data.Length < HEADER_SIZE) return;

			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;

			for (int i = 0; i < count; i++)
			{
				// Fixed prefix: OutboxId(8) + ReactionId(8) + AnchorEntryId(8) +
				// RecordedAtTicks(8) + Delivered(1) + DeliveredAtTicks(8) = 41 bytes.
				if (offset + 41 > data.Length) break;

				long outboxId = BitConverter.ToInt64(data, offset); offset += 8;
				long reactionId = BitConverter.ToInt64(data, offset); offset += 8;
				long anchorEntryId = BitConverter.ToInt64(data, offset); offset += 8;
				long recordedAtTicks = BitConverter.ToInt64(data, offset); offset += 8;
				bool delivered = data[offset] != 0; offset += 1;
				long deliveredAtTicks = BitConverter.ToInt64(data, offset); offset += 8;

				if (offset + 2 > data.Length) break;
				ushort destLen = BitConverter.ToUInt16(data, offset); offset += 2;
				if (offset + destLen > data.Length) break;
				string destination = Encoding.UTF8.GetString(data, offset, destLen); offset += destLen;

				if (offset + 2 > data.Length) break;
				ushort keyLen = BitConverter.ToUInt16(data, offset); offset += 2;
				if (offset + keyLen > data.Length) break;
				string idempotencyKey = Encoding.UTF8.GetString(data, offset, keyLen); offset += keyLen;

				if (offset + 4 > data.Length) break;
				int payloadLen = BitConverter.ToInt32(data, offset); offset += 4;
				if (offset + payloadLen > data.Length) break;
				string payload = Encoding.UTF8.GetString(data, offset, payloadLen); offset += payloadLen;

				var record = new OutboxRecord(reactionId, anchorEntryId, destination, payload, idempotencyKey,
					new DateTime(recordedAtTicks, DateTimeKind.Utc))
				{
					OutboxId = outboxId,
					Delivered = delivered,
					DeliveredAt = delivered ? new DateTime(deliveredAtTicks, DateTimeKind.Utc) : null
				};

				rows.Add(record);
				byKey[idempotencyKey] = record;
				if (outboxId >= nextOutboxId) nextOutboxId = outboxId + 1;
			}
		}

		private void Save()
		{
			int totalBytes = HEADER_SIZE;
			foreach (var row in rows)
			{
				totalBytes += 41
					+ 2 + Encoding.UTF8.GetByteCount(row.Destination)
					+ 2 + Encoding.UTF8.GetByteCount(row.IdempotencyKey)
					+ 4 + Encoding.UTF8.GetByteCount(row.Payload);
			}

			byte[] data = new byte[totalBytes];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), rows.Count); offset += 4;

			foreach (var row in rows)
			{
				byte[] destBytes = Encoding.UTF8.GetBytes(row.Destination);
				byte[] keyBytes = Encoding.UTF8.GetBytes(row.IdempotencyKey);
				byte[] payloadBytes = Encoding.UTF8.GetBytes(row.Payload);

				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), row.OutboxId); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), row.ReactionId); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), row.AnchorEntryId); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), row.RecordedAt.Ticks); offset += 8;
				data[offset] = (byte)(row.Delivered ? 1 : 0); offset += 1;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), row.DeliveredAt?.Ticks ?? 0L); offset += 8;

				BitConverter.TryWriteBytes(data.AsSpan(offset, 2), (ushort)destBytes.Length); offset += 2;
				Buffer.BlockCopy(destBytes, 0, data, offset, destBytes.Length); offset += destBytes.Length;

				BitConverter.TryWriteBytes(data.AsSpan(offset, 2), (ushort)keyBytes.Length); offset += 2;
				Buffer.BlockCopy(keyBytes, 0, data, offset, keyBytes.Length); offset += keyBytes.Length;

				BitConverter.TryWriteBytes(data.AsSpan(offset, 4), payloadBytes.Length); offset += 4;
				Buffer.BlockCopy(payloadBytes, 0, data, offset, payloadBytes.Length); offset += payloadBytes.Length;
			}

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);
		}

		internal override bool TryInsert(OutboxRecord record)
		{
			ArgumentNullException.ThrowIfNull(record);

			lock (storeLock)
			{
				if (byKey.ContainsKey(record.IdempotencyKey))
					return false;

				record.OutboxId = nextOutboxId++;
				rows.Add(record);
				byKey[record.IdempotencyKey] = record;
				Save();
				return true;
			}
		}

		internal override void ReadUndelivered(List<OutboxRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();

			lock (storeLock)
			{
				// rows is already ascending by OutboxId (append-only insertion).
				foreach (var row in rows)
				{
					if (!row.Delivered)
						result.Add(row);
				}
			}
		}

		internal override bool MarkDelivered(long outboxId, DateTime deliveredAt)
		{
			lock (storeLock)
			{
				foreach (var row in rows)
				{
					if (row.OutboxId != outboxId)
						continue;
					if (row.Delivered)
						return false;
					row.Delivered = true;
					row.DeliveredAt = deliveredAt;
					Save();
					return true;
				}
				return false;
			}
		}

		internal override bool IsRecorded(string idempotencyKey)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
			lock (storeLock)
			{
				return byKey.ContainsKey(idempotencyKey);
			}
		}

		internal override int PendingCount
		{
			get
			{
				lock (storeLock)
				{
					int n = 0;
					foreach (var row in rows)
						if (!row.Delivered) n++;
					return n;
				}
			}
		}
	}
}
