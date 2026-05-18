using System;
using Choreography.Observability;

namespace Choreography.Saga
{
    internal sealed class SagaTracer : Tracer
    {
        private static SagaTracer instance;
        private static readonly object gate = new object();

        internal static SagaTracer Instance
        {
            get
            {
                if (instance != null) return instance;
                lock (gate)
                {
                    instance ??= new SagaTracer();
                    return instance;
                }
            }
        }

        public SpanGroup Span { get; }
        public IdleGroup Idle { get; }

        private SagaTracer() : base()
        {
            Span = new SpanGroup(this);
            Idle = new IdleGroup(this);
        }

        internal IFlowSpan StartStepSpan(string sagaName, string stepName, string instanceKey)
        {
            var s = Span.Step.Start();
            s.SetLabel(Tags.SagaName, sagaName);
            s.SetLabel(Tags.SagaStep, stepName);
            if (instanceKey != null) s.SetLabel(Tags.SagaInstance, instanceKey);
            return s;
        }

        internal void OnStepSkippedByIdempotency(string sagaName, string stepName, string instanceKey)
        {
            using var s = Span.StepSkippedByIdempotency.Start();
            s.SetLabel(Tags.SagaName, sagaName);
            s.SetLabel(Tags.SagaStep, stepName);
            if (instanceKey != null) s.SetLabel(Tags.SagaInstance, instanceKey);
            s.SetOutcome(FlowOutcome.Success);
        }

        internal void OnStepFailed(string sagaName, string stepName, string instanceKey, Exception ex)
        {
            CaptureError($"Saga.{sagaName}.{stepName}", ex);
        }

        public sealed class SpanGroup
        {
            private readonly SagaTracer t;
            internal SpanGroup(SagaTracer t) { this.t = t; }

            public SpanFactory Step                    => t.DefineSpan("Saga.Step",                   "choreography.saga");
            public SpanFactory StepSkippedByIdempotency => t.DefineSpan("Saga.StepSkippedByIdempotency", "choreography.saga");
        }

        public sealed class IdleGroup
        {
            private readonly SagaTracer t;
            internal IdleGroup(SagaTracer t) { this.t = t; }

            public IdleFactory WaitingForKeyLock => t.DefineIdle("Saga.WaitingForKeyLock", "choreography.saga");
        }
    }
}
