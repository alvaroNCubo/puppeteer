using System;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.DB
{
	internal sealed class OutboxStorageInMemory : OutboxStorage
	{
		// Insertion-ordered rows + a key index for O(1) dedup. Both guarded by
		// lockObject. The diary's RecordOutboxWithCheckpoint calls TryInsert while
		// holding the reactionCheckpoints lock; nothing in this type ever calls
		// back into the diary, so there is no lock-ordering cycle.
		private readonly List<OutboxRecord> rows = new List<OutboxRecord>();
		private readonly Dictionary<string, OutboxRecord> byKey = new Dictionary<string, OutboxRecord>(StringComparer.Ordinal);
		private readonly object lockObject = new object();
		private long nextOutboxId = 1;

		internal override bool TryInsert(OutboxRecord record)
		{
			ArgumentNullException.ThrowIfNull(record);

			lock (lockObject)
			{
				if (byKey.ContainsKey(record.IdempotencyKey))
					return false;

				record.OutboxId = nextOutboxId++;
				rows.Add(record);
				byKey[record.IdempotencyKey] = record;
				return true;
			}
		}

		internal override void ReadUndelivered(List<OutboxRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();

			lock (lockObject)
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
			lock (lockObject)
			{
				foreach (var row in rows)
				{
					if (row.OutboxId != outboxId)
						continue;
					if (row.Delivered)
						return false;
					row.Delivered = true;
					row.DeliveredAt = deliveredAt;
					return true;
				}
				return false;
			}
		}

		internal override bool IsRecorded(string idempotencyKey)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
			lock (lockObject)
			{
				return byKey.ContainsKey(idempotencyKey);
			}
		}

		internal override int PendingCount
		{
			get
			{
				lock (lockObject)
				{
					int n = 0;
					foreach (var row in rows)
						if (!row.Delivered) n++;
					return n;
				}
			}
		}

		internal void Clear()
		{
			lock (lockObject)
			{
				rows.Clear();
				byKey.Clear();
				nextOutboxId = 1;
			}
		}
	}
}
