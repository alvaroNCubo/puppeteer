using System.Diagnostics;

namespace Puppeteer.Tell
{
	// W3C Trace Context (https://www.w3.org/TR/trace-context/) for the tell path.
	//
	// Trace context is EPHEMERAL OBSERVABILITY metadata. It is captured from the
	// ambient Activity at the moment a tell is SENT so that a cross-actor,
	// tell-choreographed flow (A -> B -> A -> C) surfaces as ONE distributed trace in
	// any OpenTelemetry-compatible APM. It rides the envelope / wire headers only — it
	// is NEVER journaled and NEVER replayed. The journal records the domain fact (the
	// sentence) alone; trace context lives beside it exactly as ip / user / offset do.
	//
	// The standard header NAMES are used verbatim so any OTel propagator interoperates
	// without a bespoke format, and the VALUE is Activity.Id (the W3C traceparent) so
	// the receiver re-parents through ActivityContext.TryParse.
	//
	// No-op when tracing is off: with no ActivityListener the ActivitySource creates no
	// Activity, Activity.Current is null, capture returns false, and the sender injects
	// nothing — zero overhead and no header on the wire.
	public static class TraceContext
	{
		// W3C standard propagation header names. Not broker-specific: any input medium
		// that carries key/value headers uses these same keys, so propagation stays
		// generic (headers), never coupled to one transport.
		public const string TraceParentHeader = "traceparent";
		public const string TraceStateHeader = "tracestate";

		// Capture the ambient W3C trace context. Returns false (and null outputs) when
		// there is no active W3C trace, so the caller propagates nothing. Only the W3C
		// id format is emitted — a legacy hierarchical id would not round-trip through
		// ActivityContext.TryParse on the receiver, so it is treated as "no context".
		public static bool TryCaptureAmbient(out string traceParent, out string traceState)
		{
			Activity current = Activity.Current;
			if (current == null || current.IdFormat != ActivityIdFormat.W3C || string.IsNullOrEmpty(current.Id))
			{
				traceParent = null;
				traceState = null;
				return false;
			}

			traceParent = current.Id;
			traceState = current.TraceStateString;
			return true;
		}
	}
}
