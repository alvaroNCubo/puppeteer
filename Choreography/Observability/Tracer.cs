using System;

namespace Choreography.Observability
{
    public abstract class Tracer
    {
        // When null, Flow is resolved dynamically from TracerFactory.Current
        // — this allows changes to the adapter (e.g. in isolated tests with an
        // ActivitySource per test) to take effect on existing singletons.
        // When non-null (internal ctor with InternalsVisibleTo), the flow is
        // frozen — useful for tests with an injected FakeFlow.
        private readonly IFlowTrace explicitFlow;

        protected Tracer()
        {
            this.explicitFlow = null;
        }

        internal Tracer(IFlowTrace flow)
        {
            ArgumentNullException.ThrowIfNull(flow);
            this.explicitFlow = flow;
        }

        private IFlowTrace Flow => explicitFlow ?? TracerFactory.Current;

        protected SpanFactory DefineSpan(string name, string type)
        {
            return new SpanFactory(Flow, name, type);
        }

        protected LoopFactory DefineLoop(string name, string type)
        {
            return new LoopFactory(Flow, name, type);
        }

        protected IdleFactory DefineIdle(string name, string type)
        {
            return new IdleFactory(Flow, name, type);
        }

        protected void CaptureError(string step, Exception error)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(step);
            ArgumentNullException.ThrowIfNull(error);
            Flow.CaptureError(step, error);
        }
    }
}
