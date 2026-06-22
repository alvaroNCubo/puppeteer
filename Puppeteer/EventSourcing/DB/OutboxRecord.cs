using System;

namespace Puppeteer.EventSourcing.DB
{
	// One recorded-but-maybe-undelivered outbox row. Produced by
	// `.Outbox.Emit(...)` via DiaryStorage.RecordOutboxWithCheckpoint (committed
	// atomically with the reaction cursor advance) and consumed by the relay
	// (OutboxRelay), which delivers it to an IOutboxSink and then flips Delivered.
	//
	// IdempotencyKey is the natural dedup key for the recording: the storage
	// inserts at most one row per key, so a re-detected match (after a crash or
	// pod handoff) cannot produce a second row. Distinct from the cursor's
	// monotonicity guard — belt and suspenders.
	internal sealed class OutboxRecord
	{
		internal long OutboxId { get; set; }
		internal long ReactionId { get; }
		internal long AnchorEntryId { get; }
		internal string Destination { get; }
		internal string Payload { get; }
		internal string IdempotencyKey { get; }
		internal DateTime RecordedAt { get; }
		internal bool Delivered { get; set; }
		internal DateTime? DeliveredAt { get; set; }

		internal OutboxRecord(
			long reactionId,
			long anchorEntryId,
			string destination,
			string payload,
			string idempotencyKey,
			DateTime recordedAt)
		{
			if (reactionId <= 0) throw new LanguageException($"reactionId must be greater than zero, but was {reactionId}.");
			if (anchorEntryId <= 0) throw new LanguageException($"anchorEntryId must be greater than zero, but was {anchorEntryId}.");
			ArgumentException.ThrowIfNullOrWhiteSpace(destination);
			ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

			ReactionId = reactionId;
			AnchorEntryId = anchorEntryId;
			Destination = destination;
			Payload = payload ?? string.Empty;
			IdempotencyKey = idempotencyKey;
			RecordedAt = recordedAt;
			Delivered = false;
			DeliveredAt = null;
		}

		internal OutboxMessage ToMessage()
			=> new OutboxMessage(OutboxId, ReactionId, AnchorEntryId, Destination, Payload, IdempotencyKey, RecordedAt);
	}
}
