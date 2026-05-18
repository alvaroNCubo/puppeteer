using System;
using Choreography.Observability;

namespace Choreography.Dispatch
{
    internal sealed class DispatchTracer : Tracer
    {
        private static DispatchTracer instance;
        private static readonly object gate = new object();

        internal static DispatchTracer Instance
        {
            get
            {
                if (instance != null) return instance;
                lock (gate)
                {
                    instance ??= new DispatchTracer();
                    return instance;
                }
            }
        }

        public SpanGroup Span { get; }

        private DispatchTracer() : base()
        {
            Span = new SpanGroup(this);
        }

        internal IFlowSpan StartHandlerSpan(string messageId, string handlerName, string sagaName, string stepName, string instanceKey)
        {
            var s = Span.Handler.Start();
            if (messageId != null) s.SetLabel(Tags.MessageId, messageId);
            if (handlerName != null) s.SetLabel("dispatch.handler", handlerName);
            if (sagaName != null) s.SetLabel(Tags.SagaName, sagaName);
            if (stepName != null) s.SetLabel(Tags.SagaStep, stepName);
            if (instanceKey != null) s.SetLabel(Tags.SagaInstance, instanceKey);
            return s;
        }

        internal void OnIdempotencyHit(string messageId)
        {
            using var s = Span.IdempotencyHit.Start();
            s.SetLabel(Tags.MessageId, messageId);
            s.SetLabel("dispatch.idempotency_hit", true);
            s.SetOutcome(FlowOutcome.Success);
        }

        internal void OnHandlerFailed(string handlerName, Exception ex)
        {
            CaptureError($"Dispatch.Handler.{handlerName}", ex);
        }

        public sealed class SpanGroup
        {
            private readonly DispatchTracer t;
            internal SpanGroup(DispatchTracer t) { this.t = t; }

            public SpanFactory Handler         => t.DefineSpan("Dispatch.Handler",         "choreography.dispatch");
            public SpanFactory IdempotencyHit  => t.DefineSpan("Dispatch.IdempotencyHit",  "choreography.dispatch");
        }
    }
}
