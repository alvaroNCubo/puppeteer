namespace Puppeteer.Tell
{
	// Envelope that travels from the origin actor (A) to the transport, and from the transport to the
	// receiver (B). It is an operational DTO — it is not the journal sentence. The sentence
	// lives in A's journal; the envelope is just the envelope that the transport
	// delivers so that B processes it through its public endpoint.
	//
	// Plan 4 of the Tell primitive roadmap introduces the type. Plan 5 wires it
	// to the journal entry of the executing TellStatement.
	public readonly record struct TellEnvelope(
		string Id,
		string TargetClass,
		string TargetId,
		string CommandText,
		string Transport,
		string CausalEventId,
		string ReactionName,
		// Optional DSL predicate of a Causation.Continue(check:, ...). When it is
		// not null, the receiver must run the CommandText as a CheckThenCommand
		// (check first, against its own state) for fan-out idempotency.
		string Check = null);
}
