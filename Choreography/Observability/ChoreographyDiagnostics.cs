using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Choreography.Observability
{
    internal static class ChoreographyDiagnostics
    {
        internal const string FrameworkSourceName = "Choreography";

        internal static readonly ActivitySource FrameworkSource = new ActivitySource(FrameworkSourceName);
        internal static readonly Meter FrameworkMeter = new Meter(FrameworkSourceName);
    }
}
