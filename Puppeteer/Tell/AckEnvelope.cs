namespace Puppeteer.Tell
{
	// Envelope that the transport delivers back to the origin actor when the
	// receiver (B) reported processing. B does not know that the tell primitive exists —
	// it emits a normal event through its endpoint, and the transport maps it to the
	// return channel toward A. A ingests it as the sentence
	// `tell ack '<id>' from <Addressee>('<instanceId>')` in A's journal — a thing A
	// can truthfully say ("the addressee acknowledged my utterance <id>").
	public readonly record struct AckEnvelope(
		string Id,
		// The logical hearer that acknowledged — a role in A's vocabulary, never an
		// infrastructure address (no partition, consumer-group, broker).
		string Addressee,
		// Optional logical instance the acknowledgement came from; null when the tell
		// addressed a role without naming an instance.
		string AddresseeInstanceId);
}
