using System;

namespace Choreography.Dispatch
{
    public sealed class DispatchOptions
    {
        public int MaxParallelism { get; set; } = 4;
        public int IdempotencyWindowSize { get; set; } = 100_000;
        public TimeSpan IdempotencyTTL { get; set; } = TimeSpan.FromMinutes(15);
        public TimeSpan StuckThreshold { get; set; } = TimeSpan.FromMinutes(5);

        internal void Validate()
        {
            if (MaxParallelism < 1) throw new ArgumentOutOfRangeException(nameof(MaxParallelism), "Must be >= 1");
            if (IdempotencyWindowSize < 0) throw new ArgumentOutOfRangeException(nameof(IdempotencyWindowSize), "Must be >= 0");
            if (IdempotencyTTL <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(IdempotencyTTL), "Must be positive");
            if (StuckThreshold <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(StuckThreshold), "Must be positive");
        }
    }
}
