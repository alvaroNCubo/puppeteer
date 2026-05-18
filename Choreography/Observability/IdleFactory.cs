using System;

namespace Choreography.Observability
{
    public readonly struct IdleFactory
    {
        private readonly IFlowTrace flow;
        private readonly string name;
        private readonly string type;

        internal IdleFactory(IFlowTrace flow, string name, string type)
        {
            ArgumentNullException.ThrowIfNull(flow);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);
            this.flow = flow;
            this.name = name;
            this.type = type;
        }

        public void Mark()
        {
            EnsureInitialized();
            flow.IdleMark(name, type);
        }

        public IDisposable Begin(string reason)
        {
            EnsureInitialized();
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            return flow.BeginIdle(name, type, reason);
        }

        private void EnsureInitialized()
        {
            if (flow == null)
                throw new InvalidOperationException("IdleFactory was not initialized. Use Tracer.DefineIdle(name, type) instead of default(IdleFactory).");
        }
    }
}
