using System;

namespace Choreography.Observability
{
    internal sealed class NoOpFlowTrace : IFlowTrace
    {
        internal static readonly NoOpFlowTrace Instance = new NoOpFlowTrace();

        private NoOpFlowTrace() { }

        public IFlowSpan StartSpan(string name, string type, string parentContext) => NoOpFlowSpan.Instance;
        public IFlowSpan StartChildSpan(string name, string type) => NoOpFlowSpan.Instance;

        public IDisposable BeginLoopIteration(string name, string type) => NoOpDisposable.Instance;
        public void LoopTick(string name, string type, bool matched, Exception error) { }

        public void IdleMark(string name, string type) { }
        public IDisposable BeginIdle(string name, string type, string reason) => NoOpDisposable.Instance;

        public void CaptureError(string step, Exception error) { }
    }

    internal sealed class NoOpFlowSpan : IFlowSpan
    {
        internal static readonly NoOpFlowSpan Instance = new NoOpFlowSpan();

        private NoOpFlowSpan() { }

        public void SetLabel(string key, string value) { }
        public void SetLabel(string key, long value) { }
        public void SetLabel(string key, double value) { }
        public void SetLabel(string key, bool value) { }
        public void SetResult(string description) { }
        public void SetOutcome(FlowOutcome outcome) { }
        public string SerializeContext() => string.Empty;
        public void Dispose() { }
    }

    internal sealed class NoOpDisposable : IDisposable
    {
        internal static readonly NoOpDisposable Instance = new NoOpDisposable();
        private NoOpDisposable() { }
        public void Dispose() { }
    }
}
