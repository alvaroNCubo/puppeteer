namespace Puppeteer.Tell
{
	// Declaration testimony: the transport proactively reports that an envelope
	// will NOT be delivered (dead-letter / exhausted retries), mirroring
	// AckEnvelope on the success side. The origin actor turns it into the
	// non-delivery verdict `tell '<Id>' not delivered, per '<Witness>';` in its
	// own journal, through the same single-writer path the ack handler uses.
	//
	// Witness is the transport's self-identifying label that appears in the
	// journaled sentence (e.g. "Kafka:loyalty-v1"). Reason is free-form
	// diagnostics for logging only — it is NOT journaled, so the verdict stays
	// stable across runs and domain-agnostic.
	//
	// Plan 10 of the Tell primitive roadmap introduces the type.
	public readonly record struct TellFailure(
		string Id,
		string Witness,
		string Reason = null);
}
