using System;
using System.Diagnostics;

namespace Choreography.Observability
{
    public static class TracerFactory
    {
        private static IFlowTrace current;
        private static readonly object gate = new object();
        private static int notConfiguredWarningLogged;

        public static void UseActivitySource(string serviceName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
            lock (gate)
            {
                current = new ActivityFlowTrace(serviceName);
            }
        }

        public static void UseNoOp()
        {
            lock (gate)
            {
                current = NoOpFlowTrace.Instance;
            }
        }

        internal static void UseCustom(IFlowTrace impl)
        {
            ArgumentNullException.ThrowIfNull(impl);
            lock (gate)
            {
                current = impl;
            }
        }

        internal static void Reset()
        {
            lock (gate)
            {
                current = null;
            }
        }

        internal static IFlowTrace Current
        {
            get
            {
                var c = current;
                if (c != null) return c;
                if (System.Threading.Interlocked.Exchange(ref notConfiguredWarningLogged, 1) == 0)
                {
                    Debug.WriteLine(
                        "[Choreography.Observability] WARNING: TracerFactory not configured — falling back to NoOp. " +
                        "Call TracerFactory.UseActivitySource(serviceName) at startup to enable observability.");
                }
                return NoOpFlowTrace.Instance;
            }
        }

        public static bool IsConfigured => current != null;
    }
}
