using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Choreography.Theater;

namespace Choreography.Ensemble
{
    public class EnsemblePerformance<T> where T : Performance
    {
        private readonly ConcurrentDictionary<string, T> performers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, T> factory;

        public EnsemblePerformance(Func<string, T> factory)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public T GetOrCreate(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentNullException(nameof(id));

            return performers.GetOrAdd(id, factory);
        }

        public bool Evict(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentNullException(nameof(id));

            if (performers.TryRemove(id, out var performance))
            {
                performance.Dispose();
                return true;
            }
            return false;
        }

        public void EvictInactive(TimeSpan threshold)
        {
            var now = DateTime.Now;
            foreach (var kvp in performers)
            {
                if (now - kvp.Value.LastActivity > threshold)
                {
                    if (performers.TryRemove(kvp.Key, out var performance))
                    {
                        performance.Dispose();
                    }
                }
            }
        }

        public IEnumerable<string> ListPerformers() => performers.Keys;

        public int Count => performers.Count;

        public string LockAllWhileNotSyncronized()
        {
            var results = new System.Text.StringBuilder();
            foreach (var kvp in performers)
            {
                var result = kvp.Value.LockWhileNotSyncronized();
                results.AppendLine($"{kvp.Key}: {result}");
            }
            return results.ToString();
        }

        public void UnlockAllAndRunAlive()
        {
            foreach (var kvp in performers)
            {
                kvp.Value.UnlockAndRunAlive();
            }
        }

        public bool AreAllAlive
        {
            get
            {
                if (performers.IsEmpty) return false;
                foreach (var kvp in performers)
                {
                    if (!kvp.Value.IsAlive) return false;
                }
                return true;
            }
        }
    }
}
