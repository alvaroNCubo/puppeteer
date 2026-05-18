using System;

namespace Choreography.Observability
{
    internal interface IFlowTrace
    {
        IFlowSpan StartSpan(string name, string type, string parentContext);
        IFlowSpan StartChildSpan(string name, string type);

        IDisposable BeginLoopIteration(string name, string type);
        void LoopTick(string name, string type, bool matched, Exception error);

        void IdleMark(string name, string type);
        IDisposable BeginIdle(string name, string type, string reason);

        void CaptureError(string step, Exception error);
    }
}
