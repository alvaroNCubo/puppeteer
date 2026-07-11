using Choreography.Observability;

namespace Choreography.Told
{
    // Tracer for receiver-side uptake of a cross-actor `tell`. The uptake span covers
    // the hearer running its command (or enacting its notation) in response to a told.
    // When the received tell carried a W3C trace context, the span is opened as a CHILD
    // of that remote trace so the sender's and hearer's work share ONE distributed
    // trace; otherwise it opens a normal span. Because the span is Activity.Current
    // while the hearer runs, any ONWARD tell the hearer emits within that scope inherits
    // the same trace — which is what stitches a multi-hop chain (A -> B -> A -> C).
    internal sealed class ToldTracer : Tracer
    {
        private static ToldTracer instance;
        private static readonly object gate = new object();

        internal static ToldTracer Instance
        {
            get
            {
                if (instance != null) return instance;
                lock (gate)
                {
                    instance ??= new ToldTracer();
                    return instance;
                }
            }
        }

        public SpanGroup Span { get; }

        private ToldTracer() : base()
        {
            Span = new SpanGroup(this);
        }

        // Open the uptake span, re-parenting onto the sender's trace when the wire
        // carried one. traceContext null/empty -> a normal span (root or ambient child).
        internal IFlowSpan StartUptakeSpan(string messageName, string role, string traceContext)
        {
            IFlowSpan s = string.IsNullOrWhiteSpace(traceContext)
                ? Span.Uptake.Start()
                : Span.Uptake.StartFromContext(traceContext);
            if (messageName != null) s.SetLabel("told.message", messageName);
            if (role != null) s.SetLabel("told.role", role);
            return s;
        }

        public sealed class SpanGroup
        {
            private readonly ToldTracer t;
            internal SpanGroup(ToldTracer t) { this.t = t; }

            public SpanFactory Uptake => t.DefineSpan("Told.Uptake", "choreography.told");
        }
    }
}
