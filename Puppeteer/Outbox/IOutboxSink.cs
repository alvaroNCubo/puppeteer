namespace Puppeteer
{
	// The external delivery target the relay pushes recorded outbox messages to
	// (a Kafka producer, an HTTP client, etc.). The relay calls Send and only
	// marks the row delivered if Send returns without throwing — so delivery is
	// at-least-once: a crash (or throw) after the broker accepted the message but
	// before the relay marked it delivered causes a redelivery on the next
	// Dispatch, carrying the same OutboxMessage.IdempotencyKey.
	//
	// The sink (or the consumer downstream of it) is responsible for deduplicating
	// on that key — that is the residual requirement for an exactly-once EFFECT.
	public interface IOutboxSink
	{
		void Send(OutboxMessage message);
	}
}
