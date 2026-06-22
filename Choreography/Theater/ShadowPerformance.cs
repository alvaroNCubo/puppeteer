using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Puppeteer;
using Puppeteer.EventSourcing.Follower;

namespace Choreography.Theater
{
    // Shadow Replay — S1 (handoff_shadow_S1_implementation.md / design §3.0).
    //
    // ShadowPerformance hosts (by composition) a Shadow — an isolated laboratory
    // actor derived from a production actor. It is a DELIBERATELY DISTINCT type
    // from Performance (it does NOT inherit from Performance): the compiler must
    // prevent a shadow from silently substituting a real Performance in any API
    // that expects a production Performance. The only thing it shares with Performance
    // is the shape of the hosting surface (Start / PerformCmd / PerformQry) so that
    // an S6 pod can serve it, plus the shadow-only surface (SyncUntil,
    // StartShadowing, Reactions).
    //
    // A ShadowPerformance is obtained from performance.Shadow(cfg). It NEVER writes to
    // the primary's journal; the cross-actor Tells from its reactions are dropped; it is
    // not registered as a Materialization destination of the primary.
    public sealed class ShadowPerformance : IDisposable
    {
        private readonly Puppeteer.Shadow shadow;
        private bool started;
        private CancellationTokenSource reactionsCts;
        private readonly List<Task> reactionTasks = new List<Task>();
        private bool disposed;

        internal ShadowPerformance(Puppeteer.Shadow shadow)
        {
            this.shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        }

        // The underlying shadow actor (V1/V2 family same as the primary). Exposed to
        // declare experimental reactions and, in V1, to drive it directly.
        public Actor Actor => shadow.Actor;

        // The shadow's Reactions — same Theme A API pointing at the shadow.
        public Reactions Reactions => shadow.Reactions;

        public long CurrentEntryId => shadow.CurrentEntryId;

        // Starts the shadow's cued reactions (same pattern as Performance.Start,
        // but over the shadow actor). The shadow's storage was already configured by
        // CreateShadow, so Start here only activates the push reactions engine.
        public ShadowPerformance Start()
        {
            ThrowIfDisposed();
            if (started) throw new InvalidOperationException("ShadowPerformance is already started.");
            started = true;
            StartCuedReactions();
            return this;
        }

        private void StartCuedReactions()
        {
            var cuedReactions = shadow.Actor.Reactions.CuedReactions;
            bool hasCued = false;

            foreach (var reaction in cuedReactions)
            {
                if (!hasCued)
                {
                    reactionsCts = new CancellationTokenSource();
                    hasCued = true;
                }

                var ct = reactionsCts.Token;
                var task = Task.Run(() => reaction.Execute(ReactionExecutionMode.Continuous, ct));
                reactionTasks.Add(task);
            }
        }

        // SyncUntil(toEntryId): replay of the primary's journal from genesis up to
        // toEntryId inclusive, against the shadow's own storage. Ceiling, not floor.
        // After this the shadow is forked and accepts local commands.
        public void SyncUntil(long toEntryId)
        {
            ThrowIfDisposed();
            shadow.SyncUntil(toEntryId);
        }

        // S2 — Continuous shadowing. STUB in S1.
        public void StartShadowing()
        {
            ThrowIfDisposed();
            shadow.StartShadowing();
        }

        // Local command driver (V1). Induces the experiment's local divergence.
        public string PerformCmd(string script, string ip, string user)
        {
            ThrowIfDisposed();
            return shadow.PerformCmd(script, ip, user);
        }

        // Read-only query over the shadow's state.
        public string PerformQry(string script)
        {
            ThrowIfDisposed();
            return shadow.PerformQry(script);
        }

        // TTL kill-all (S1): stops the cued reactions and disposes the shadow (graceful
        // shutdown + cleanup of its own storage). Idempotent.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (reactionsCts != null)
            {
                reactionsCts.Cancel();
                if (reactionTasks.Count > 0)
                {
                    try
                    {
                        Task.WaitAll(reactionTasks.ToArray(), TimeSpan.FromSeconds(30));
                    }
                    catch (AggregateException)
                    {
                    }
                }
                reactionsCts.Dispose();
                reactionsCts = null;
            }
            reactionTasks.Clear();

            shadow.Dispose();
            started = false;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new LanguageException("This ShadowPerformance has been disposed (TTL kill-all). Build a new one via performance.Shadow(cfg).");
        }
    }
}
