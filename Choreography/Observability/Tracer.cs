using System;

namespace Choreography.Observability
{
    public abstract class Tracer
    {
        // Cuando es null, Flow se resuelve dinamicamente desde TracerFactory.Current
        // — esto permite que cambios al adapter (ej: en tests aislados con un
        // ActivitySource por test) tomen efecto en singletons existentes.
        // Cuando es no-null (ctor internal con InternalsVisibleTo), el flow queda
        // congelado — util para tests con FakeFlow inyectado.
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
