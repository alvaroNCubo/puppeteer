using System;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.DB
{
	// Journal-internal outbox table for `.Outbox.Emit(...)`. Ontologically a
	// sibling of EventElisionStorage / EventMaterializationStorage: a per-actor-
	// per-DB side table the diary storage owns. The asymmetry vs Materialize:
	// this table carries the full outgoing PAYLOAD and a deterministic
	// idempotency key, plus per-row delivery state, so a relay can deliver
	// at-least-once and ack each row — Materialize stored only diaryId+destination.
	//
	// The atomic record path (insert row + advance reaction cursor in ONE store
	// write) is NOT here — it lives on DiaryStorage.RecordOutboxWithCheckpoint,
	// which owns the reaction checkpoint table and the lock/transaction that makes
	// the two writes one. This type owns only the row table + the relay verbs.
	internal abstract class OutboxStorage
	{
		// Insert a recorded message. Idempotent on IdempotencyKey: if a row with
		// the same key already exists, this is a no-op and returns false (the row
		// is left untouched, including its delivery state). Returns true when a new
		// row was inserted (and assigns record.OutboxId). Callers invoke this from
		// inside the diary's checkpoint critical section, so implementations must
		// not take a lock that could deadlock against it; the in-memory impl uses a
		// separate lock with no nesting the other way.
		internal abstract bool TryInsert(OutboxRecord record);

		// Relay discovery: append every not-yet-delivered row, ascending by
		// OutboxId (recording order), to result.
		internal abstract void ReadUndelivered(List<OutboxRecord> result);

		// Relay ack: flip the row to delivered. Idempotent and monotonic — marking
		// an already-delivered row, or an unknown id, returns false.
		internal abstract bool MarkDelivered(long outboxId, DateTime deliveredAt);

		internal abstract bool IsRecorded(string idempotencyKey);

		internal abstract int PendingCount { get; }
	}
}
