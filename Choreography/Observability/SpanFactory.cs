using System;

namespace Choreography.Observability
{
    public readonly struct SpanFactory
    {
        private readonly IFlowTrace flow;
        private readonly string name;
        private readonly string type;

        internal SpanFactory(IFlowTrace flow, string name, string type)
        {
            ArgumentNullException.ThrowIfNull(flow);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);
            this.flow = flow;
            this.name = name;
            this.type = type;
        }

        public IFlowSpan Start()
        {
            EnsureInitialized();
            return flow.StartSpan(name, type, parentContext: null);
        }

        public IFlowSpan StartFromContext(string traceContext)
        {
            EnsureInitialized();
            ArgumentException.ThrowIfNullOrWhiteSpace(traceContext);
            return flow.StartSpan(name, type, traceContext);
        }

        public IFlowSpan StartChild()
        {
            EnsureInitialized();
            return flow.StartChildSpan(name, type);
        }

        private void EnsureInitialized()
        {
            if (flow == null)
                throw new InvalidOperationException("SpanFactory was not initialized. Use Tracer.DefineSpan(name, type) instead of default(SpanFactory).");
        }
    }
}
