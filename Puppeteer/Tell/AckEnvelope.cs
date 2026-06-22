namespace Puppeteer.Tell
{
	// Envelope that the transport delivers back to the origin actor when the
	// receiver (B) reported processing. B does not know that the tell primitive exists —
	// it emits a normal event through its endpoint, and the transport (KafkaTransport,
	// RestTransport, etc.) maps it to the return channel toward A. Plan 6
	// ingests it as the sentence `tell ack <id> from <Target>(<id>)` in A's journal.
	public readonly record struct AckEnvelope(
		string Id,
		string TargetClass,
		string TargetId);
}
