using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Choreography.Theater;
using Puppeteer;
using Puppeteer.EventSourcing.Interpreter.Formatters;

namespace Choreography.Ensemble
{
    public class EnsemblePerformance<T> where T : Performance
    {
        private readonly ConcurrentDictionary<string, T> performers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, T> factory;
        private IOutputFormatter formatterPrototype;  // null = default JsonFormatter
        // Authoring transpiler (input-side mirror of formatterPrototype),
        // cascaded to V2 performers like the Formatter. Default = Identity so
        // every V2 performer always carries one.
        private INotationTranspiler transpilerPrototype = IdentityTranspiler.Instance;
        // Logger injected by the host. null = each Performance starts with its
        // default ConsoleLogger. Applied to existing performers in .Logger(x)
        // and propagated to new ones in GetOrCreate. Per-actor (not singleton): each
        // Performance in the ensemble receives the same impl but stores it in its
        // own ActorHandler.
        private IPuppeteerLogger loggerPrototype;

        public EnsemblePerformance(Func<string, T> factory)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        // ── Formatter API (cascades to V2 performers) ─────────────────────
        //
        // Sets the formatter prototype for the ensemble. Apply behavior:
        //  - V2 performers (and any derived class with a Formatter(...)
        //    setter): the prototype is propagated to each existing
        //    performer immediately, AND new performers created via
        //    GetOrCreate are configured with the prototype.
        //  - V1 performers (or any other T:Performance without the
        //    Formatter setter): silent ignore. V1 is fixed JSON by
        //    design; mixing V1+V2 in one ensemble is
        //    allowed but the V1 actors continue emitting JSON.

        public EnsemblePerformance<T> Formatter(IOutputFormatter prototype)
        {
            this.formatterPrototype = prototype;
            foreach (var perf in performers.Values)
            {
                if (perf is PerformanceV2 v2)
                {
                    v2.Formatter(prototype);
                }
                // V1 / others: silent ignore.
            }
            return this;
        }

        // Transpiler seam (input-side mirror of Formatter): cascades the
        // authoring transpiler to existing V2 performers and applies it to new
        // ones in GetOrCreate. V1 / others: silent ignore (no Enact surface).
        public EnsemblePerformance<T> Transpiler(INotationTranspiler prototype)
        {
            if (prototype == null) throw new ArgumentNullException(nameof(prototype));
            this.transpilerPrototype = prototype;
            foreach (var perf in performers.Values)
            {
                if (perf is PerformanceV2 v2)
                {
                    v2.Transpiler(prototype);
                }
            }
            return this;
        }

        // Logger seam: per-actor (not singleton). The prototype is propagated to each
        // existing performer and applied to new ones in GetOrCreate. Fluent to
        // align with Formatter(). Without injection each Performance uses its
        // default ConsoleLogger (Error -> stderr, Debug -> stdout).
        public EnsemblePerformance<T> Logger(IPuppeteerLogger logger)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            this.loggerPrototype = logger;
            foreach (var perf in performers.Values)
            {
                perf.Logger(logger);
            }
            return this;
        }

        // B.3.4: configure automatic Script → Action promotion threshold for
        // the V1 performers hosted by this ensemble. Stores the prototype so
        // new performers created via GetOrCreate also receive the setting,
        // AND propagates immediately to existing PerformanceV1 instances.
        // V2 performers are silently ignored — they explicitly declare their
        // Actions and have no Script-shaped path to promote.
        // null = use the ActorHandler default (30).
        private int? promotionThresholdPrototype;
        public EnsemblePerformance<T> InternalAutomaticPromotion(int threshold)
        {
            this.promotionThresholdPrototype = threshold;
            foreach (var perf in performers.Values)
            {
                if (perf is PerformanceV1 v1)
                {
                    v1.InternalAutomaticPromotion(threshold);
                }
            }
            return this;
        }

        public T GetOrCreate(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentNullException(nameof(id));

            return performers.GetOrAdd(id, key =>
            {
                var perf = factory(key);
                // If the ensemble has a formatter prototype set, propagate
                // it to newly created V2 performers.
                if (formatterPrototype != null && perf is PerformanceV2 v2)
                {
                    v2.Formatter(formatterPrototype);
                }
                // Propagate the authoring transpiler to newly created V2
                // performers (mirror of the formatter propagation above).
                if (perf is PerformanceV2 v2Transpiler)
                {
                    v2Transpiler.Transpiler(transpilerPrototype);
                }
                // Per-actor logger: each newly created Performance receives the
                // sink configured at the ensemble level (if the host wired it
                // via .Logger(x)).
                if (loggerPrototype != null)
                {
                    perf.Logger(loggerPrototype);
                }
                // B.3.4: propagate the promotion threshold to newly created V1
                // performers (V2 doesn't have a Script path to promote).
                if (promotionThresholdPrototype.HasValue && perf is PerformanceV1 v1Promo)
                {
                    v1Promo.InternalAutomaticPromotion(promotionThresholdPrototype.Value);
                }
                return perf;
            });
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
