using System;

namespace Choreography.Transport.Brokered
{
	// Shared wire conventions between BrokerTellTransport (producer + ack/failure
	// ingestion) and BrokerTellConsumer (receiver + ack emission). Keeping the header
	// keys and topic-naming rules in one place is what lets the two sides agree
	// without a hidden coupling.
	//
	// The wire carries only what the sender could have uttered: the message name (the
	// sender's vocabulary), the ordered payload values (the record value), and the
	// addressee. It never carries the transport route (that is the binding table's
	// job, never on the wire) nor the receiver's command template (the directive
	// lives receiver-side: the host maps the message name to its own command).
	internal static class BrokerTellWire
	{
		// Header keys. The value of a tell record is the ordered payload values
		// (ArgumentsAsString); everything needed to correlate and route the
		// round-trip travels in headers so the value stays a clean payload.
		internal const string HeaderKind = "puppeteer-kind";
		internal const string HeaderTellId = "puppeteer-tell-id";
		internal const string HeaderMessage = "puppeteer-message";
		internal const string HeaderAddressee = "puppeteer-addressee";
		internal const string HeaderAddresseeInstance = "puppeteer-addressee-instance";
		internal const string HeaderCheck = "puppeteer-check";
		internal const string HeaderReaction = "puppeteer-reaction";
		internal const string HeaderCausalEvent = "puppeteer-causal-event";
		// Witness / reason ride only on a failure record (telemetry that flows
		// receiver → origin). The origin journals the verdict logically (by
		// addressee) and never journals these — they exist for runtime telemetry.
		internal const string HeaderWitness = "puppeteer-witness";
		internal const string HeaderReason = "puppeteer-reason";

		// W3C Trace Context — EPHEMERAL observability metadata that lets a cross-actor
		// tell chain render as ONE distributed trace. Unlike the `puppeteer-*` headers,
		// the standard header NAMES are used verbatim so any OpenTelemetry-compatible
		// APM (or an external producer/consumer) interoperates without a bespoke
		// propagator. Single source of truth is Puppeteer.Tell.TraceContext so the same
		// keys are used on every input medium, not just the broker. NEVER journaled.
		internal const string HeaderTraceParent = Puppeteer.Tell.TraceContext.TraceParentHeader;
		internal const string HeaderTraceState = Puppeteer.Tell.TraceContext.TraceStateHeader;

		// Kind discriminator — a topic and its ack topic may both be observed by the
		// same code path, so each record self-identifies.
		internal const string KindTell = "tell";
		internal const string KindAck = "ack";
		internal const string KindFailure = "failure";

		// Ack topic naming: the receiver emits acks to "<topic>.acks"; the origin
		// transport subscribes there to close the loop.
		internal static string AckTopicOf(string topic)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(topic);
			return topic + ".acks";
		}
	}
}
