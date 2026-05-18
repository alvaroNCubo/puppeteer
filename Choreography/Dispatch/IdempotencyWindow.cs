using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Choreography.Dispatch
{
    internal sealed class IdempotencyWindow : IDisposable
    {
        private readonly ConcurrentDictionary<string, long> processed;
        private readonly int maxSize;
        private readonly long ttlTicks;
        private readonly Timer evictionTimer;
        private int count;

        internal IdempotencyWindow(int maxSize, TimeSpan ttl)
        {
            if (maxSize < 0) throw new ArgumentOutOfRangeException(nameof(maxSize));
            if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));

            this.maxSize = maxSize;
            this.ttlTicks = ttl.Ticks;
            this.processed = new ConcurrentDictionary<string, long>(
                concurrencyLevel: Environment.ProcessorCount,
                capacity: Math.Min(maxSize, 1024));

            var evictionInterval = TimeSpan.FromMilliseconds(Math.Max(ttl.TotalMilliseconds / 4, 1000));
            evictionTimer = new Timer(_ => Evict(), null, evictionInterval, evictionInterval);
        }

        internal bool AlreadyProcessed(string messageId)
        {
            ArgumentNullException.ThrowIfNull(messageId);

            long nowTicks = DateTime.UtcNow.Ticks;

            if (processed.TryGetValue(messageId, out long existingTicks))
            {
                if (nowTicks - existingTicks < ttlTicks)
                    return true;

                processed.TryUpdate(messageId, nowTicks, existingTicks);
                return false;
            }

            if (processed.TryAdd(messageId, nowTicks))
            {
                Interlocked.Increment(ref count);

                if (count > maxSize)
                    EvictOldest();

                return false;
            }

            return true;
        }

        private void Evict()
        {
            long cutoff = DateTime.UtcNow.Ticks - ttlTicks;

            foreach (var kvp in processed)
            {
                if (kvp.Value < cutoff)
                {
                    if (processed.TryRemove(kvp.Key, out _))
                        Interlocked.Decrement(ref count);
                }
            }
        }

        private void EvictOldest()
        {
            long cutoff = DateTime.UtcNow.Ticks - ttlTicks;
            int evicted = 0;

            foreach (var kvp in processed)
            {
                if (kvp.Value < cutoff)
                {
                    if (processed.TryRemove(kvp.Key, out _))
                    {
                        Interlocked.Decrement(ref count);
                        evicted++;
                    }
                }
                if (evicted > 0 && count <= maxSize)
                    break;
            }
        }

        public void Dispose()
        {
            evictionTimer.Dispose();
            processed.Clear();
        }
    }
}
