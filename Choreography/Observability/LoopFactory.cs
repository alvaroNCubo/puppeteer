using System;

namespace Choreography.Observability
{
    public readonly struct LoopFactory
    {
        private readonly IFlowTrace flow;
        private readonly string name;
        private readonly string type;

        internal LoopFactory(IFlowTrace flow, string name, string type)
        {
            ArgumentNullException.ThrowIfNull(flow);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);
            this.flow = flow;
            this.name = name;
            this.type = type;
        }

        public IDisposable BeginIteration()
        {
            EnsureInitialized();
            return flow.BeginLoopIteration(name, type);
        }

        public void Tick(bool matched)
        {
            EnsureInitialized();
            flow.LoopTick(name, type, matched, error: null);
        }

        public void TickError(Exception ex)
        {
            EnsureInitialized();
            ArgumentNullException.ThrowIfNull(ex);
            flow.LoopTick(name, type, matched: false, error: ex);
        }

        private void EnsureInitialized()
        {
            if (flow == null)
                throw new InvalidOperationException("LoopFactory was not initialized. Use Tracer.DefineLoop(name, type) instead of default(LoopFactory).");
        }
    }
}
