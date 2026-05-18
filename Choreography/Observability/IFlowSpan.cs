using System;

namespace Choreography.Observability
{
    public interface IFlowSpan : IDisposable
    {
        void SetLabel(string key, string value);
        void SetLabel(string key, long value);
        void SetLabel(string key, double value);
        void SetLabel(string key, bool value);

        void SetResult(string description);
        void SetOutcome(FlowOutcome outcome);

        string SerializeContext();
    }
}
