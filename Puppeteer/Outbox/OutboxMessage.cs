using System;

namespace Puppeteer
{
	// A single message recorded by `.Outbox.Emit(destination, payload)` and handed
	// to an IOutboxSink by the relay. Immutable snapshot of one outbox row, minus
	// the relay-internal delivery bookkeeping.
	//
	// IdempotencyKey is deterministic — `(reactionId : anchorEntryId : seekLevel)` —
	// so a redelivery (the relay is at-least-once) carries the SAME key as the
	// original. A deduplicating or naturally-idempotent sink collapses the
	// duplicate into a single effect; that last hop is the residual requirement
	// the framework cannot own (see notes/reactions-outbox-emit.md).
	public readonly struct OutboxMessage
	{
		public long OutboxId { get; }
		public long ReactionId { get; }
		public long AnchorEntryId { get; }
		public string Destination { get; }
		public string Payload { get; }
		public string IdempotencyKey { get; }
		public DateTime RecordedAt { get; }

		public OutboxMessage(
			long outboxId,
			long reactionId,
			long anchorEntryId,
			string destination,
			string payload,
			string idempotencyKey,
			DateTime recordedAt)
		{
			OutboxId = outboxId;
			ReactionId = reactionId;
			AnchorEntryId = anchorEntryId;
			Destination = destination;
			Payload = payload;
			IdempotencyKey = idempotencyKey;
			RecordedAt = recordedAt;
		}

		public override string ToString()
			=> $"OutboxMessage[id={OutboxId}, dest={Destination}, key={IdempotencyKey}]";
	}
}
