using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Puppeteer;
using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Playbill;

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

        // Playbill carry (opt-in via ShadowConfig.CarryPlaybill). Both null when off.
        private Playbill primaryPlaybill;
        private Playbill shadowPlaybill;

        internal ShadowPerformance(Puppeteer.Shadow shadow)
        {
            this.shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        }

        // Wired by Performance.Shadow when ShadowConfig.CarryPlaybill is on: `source` is
        // the primary's playbill, `target` the shadow's own (shadow name + shadow storage).
        internal void EnablePlaybillCarry(Playbill source, Playbill target)
        {
            primaryPlaybill = source ?? throw new ArgumentNullException(nameof(source));
            shadowPlaybill = target ?? throw new ArgumentNullException(nameof(target));
        }

        // The shadow's own playbill when CarryPlaybill is on; null otherwise. Lets a
        // forensic caller query the carried audit context after SyncUntil.
        public Playbill CarriedPlaybill => shadowPlaybill;

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
            CopyCarriedPlaybill(toEntryId);
        }

        // When CarryPlaybill is on, copy the primary's playbill schemas + the records up
        // to the replay ceiling into the shadow's own playbill store. Records above the
        // ceiling are skipped (they are not part of what the shadow replayed). Idempotent:
        // a duplicate EntryId on a re-sync is swallowed, mirroring the Phase 5 apply path.
        private void CopyCarriedPlaybill(long toEntryId)
        {
            if (shadowPlaybill == null) return; // carry not enabled (default)

            foreach (var (name, declarations) in primaryPlaybill.ListSchemas())
                shadowPlaybill.RegisterSchema(name, declarations); // idempotent by contract

            var records = new List<PlaybillRecord>();
            primaryPlaybill.ReadRecordsAfter(0, records);
            foreach (var record in records)
            {
                if (record.EntryId > toEntryId) continue;
                try
                {
                    shadowPlaybill.WriteRecordRaw(record.EntryId, record.SchemaName, record.SerializedParameters);
                }
                catch (LanguageException)
                {
                    // Duplicate EntryId (re-sync) or unknown schema — idempotent, skip.
                }
            }
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
