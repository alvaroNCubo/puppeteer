using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Choreography.Saga
{
    internal sealed class KeyLock : IDisposable
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

        internal async Task<IDisposable> AcquireAsync(string key, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(key);

            var semaphore = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            return new LockRelease(semaphore);
        }

        private sealed class LockRelease : IDisposable
        {
            private SemaphoreSlim semaphore;

            internal LockRelease(SemaphoreSlim semaphore)
            {
                this.semaphore = semaphore;
            }

            public void Dispose()
            {
                var s = Interlocked.Exchange(ref semaphore, null);
                s?.Release();
            }
        }

        public void Dispose()
        {
            foreach (var kvp in locks)
                kvp.Value.Dispose();
            locks.Clear();
        }
    }
}
