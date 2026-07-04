namespace Puppeteer.Tell
{
	// Declaration testimony: the transport proactively reports that an envelope will
	// NOT be acknowledged (dead-letter / exhausted retries), mirroring AckEnvelope on
	// the success side. The origin actor turns it into the logical non-delivery
	// verdict `tell '<Id>' unacknowledged by '<Addressee>';` in its own journal,
	// through the same single-writer path the ack handler uses.
	//
	// The criterion: A can say "I did not get an acknowledgement from Loyalty"; A
	// cannot say "per Kafka". So only the addressee is journaled. Witness (which
	// transport testified) and Reason (dead-letter vs timeout) are provenance for
	// runtime telemetry/logs ONLY — they are never journaled, so the verdict stays
	// logical and domain-agnostic.
	public readonly record struct TellFailure(
		string Id,
		// The logical hearer named in the journaled verdict.
		string Addressee,
		// Transport self-identifying label (e.g. "Kafka:loyalty-v1"). Telemetry/logs
		// only — never journaled.
		string Witness = null,
		// Free-form diagnostics for logging only — never journaled.
		string Reason = null);
}
