namespace Puppeteer.Tell
{
	// Envelope that travels from the origin actor (A) to the transport, and from the
	// transport to the receiver (B). It is an operational DTO — it is not the journal
	// sentence. The sentence lives in A's journal; the envelope is just what the
	// transport delivers so that B can hear the assertion and react in its own voice.
	//
	// A `tell` is a directed assertive: A commits to the truth of a proposition,
	// addressed to a hearer. So the envelope carries only what A could have uttered:
	// the message name (A's own vocabulary), the ordered payload values, and the
	// addressee. It does NOT carry the transport (routing is a runtime binding table,
	// not part of the commitment) nor the receiver's command template (the directive
	// lives receiver-side: B maps the message name to its own command).
	public readonly record struct TellEnvelope(
		// Per-utterance identity. By default the content hash of (message, addressee,
		// instance, ordered values) rendered as 16-char hex; when the developer wrote
		// `once '<literal>'`, the literal verbatim (an idempotent singleton key).
		string Id,
		// The assertive's name in the SENDER's vocabulary (e.g. "SaleCompleted") — a
		// fact A lived, never the receiver's verb.
		string MessageName,
		// The hearer: a logical role (e.g. "Loyalty"). The runtime binding table maps
		// it to a physical route; A never names the transport.
		string Addressee,
		// Optional logical instance the sender can name in its own vocabulary
		// (`to Loyalty('<id>')`). Null for a role-only tell — the key that selects an
		// instance then rides in the payload and is resolved by the receiver.
		string AddresseeInstanceId,
		string CausalEventId,
		string ReactionName,
		// Optional DSL predicate of a Causation.Continue(check:, ...). When it is
		// not null, the receiver re-checks it against its own state before applying
		// the directive (fan-out idempotency).
		string Check = null,
		// The ordered typed VALUES of the `with` payload — the data that crosses to
		// the receiver, serialized in order via ArgumentsAsString. Null when the
		// message carries no payload. Parameter names/types never travel: the receiver
		// applies these values positionally to the command it already holds.
		Parameters Values = null,
		// EPHEMERAL observability metadata: the W3C `traceparent` (and optional
		// `tracestate`) of the trace active when this tell is sent. It rides the
		// envelope so a transport that ships envelopes directly (rather than mapping to
		// broker headers) can carry it uniformly, and so the receiver can re-parent its
		// work onto the sender's trace — the whole cross-actor chain then renders as ONE
		// distributed trace. NEVER journaled, NEVER replayed: the journal records the
		// domain fact alone, exactly as it never records ip/user/offset. Null when
		// tracing is off. See Puppeteer.Tell.TraceContext.
		string TraceParent = null,
		string TraceState = null);
}
