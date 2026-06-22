using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Dispatch;
using Choreography.Saga;
using Puppeteer;
using Puppeteer.EventSourcing.Follower;

namespace Choreography.Theater
{
    public abstract class Performance : IDisposable
    {
        protected StageHook hook;
        // Storage config — protected so subclasses (PerformanceV2) can construct
        // their own auxiliary stores (e.g. Playbill) using the same connection.
        protected DatabaseType dbType;
        protected string connectionString;
        protected bool storageConfigured;
        private bool started;
        private bool isFollower;
        private bool handoverComplete;
        private CancellationTokenSource reactionsCts;
        private readonly List<Task> reactionTasks = new List<Task>();

        private Dispatch.Dispatch dispatch;
        private readonly List<SagaDefinition> sagas = new List<SagaDefinition>();
        private SagaStepJournal sagaStepJournal;
        private KeyLock sagaKeyLock;

        // Gate used by Dispatch workers. Reset while the actor is a follower or
        // during red-black handover; Set when the actor is alive and can process
        // external events. Primary actors come up with the gate Set in Start().
        // Followers keep it Reset until UnlockAndRunAlive completes handover.
        private readonly ManualResetEventSlim aliveGate = new ManualResetEventSlim(false);

        public string Name { get; }
        internal DateTime LastActivity { get; set; }
        public long CurrentEntryId => hook.CurrentEntryId;
        public DateTime DateOfLastActivity => hook.DateOfLastActivity;
        protected Actor ActorInstance { get; set; }
        protected Assembly[] LibraryAssemblies { get; }
        public TaskMonitor TaskMonitor => dispatch?.Monitor;

        protected abstract Actor CreateActor(string actorName);

        // Legacy hook: runs exactly once when the actor is brand-new (empty journal).
        // Kept as a backward-compat crutch for Performances already in production
        // whose seed is coupled to the ItsANewOne path. New code prefers
        // OnHydrated() with a PerformCmd containing 'upgrade('init') { ... }'.
        protected virtual void OnFirstHydration() { }

        // New hook: runs after every hydration (both the first and subsequent
        // ones on restarts). Intended to invoke PerformCmd with a script that
        // contains a sequence of 'upgrade('X') { ... }' — the already-applied ones are
        // skipped silently, the new ones are applied and journaled. This is the way to
        // version actor initialization and migrations without external .exe files.
        protected virtual void OnHydrated() { }

        internal Performance(string actorName)
            : this(actorName, Array.Empty<Assembly>())
        {
        }

        internal Performance(string actorName, params Assembly[] libraryAssemblies)
        {
            if (string.IsNullOrWhiteSpace(actorName))
                throw new ArgumentNullException(nameof(actorName));
            ArgumentNullException.ThrowIfNull(libraryAssemblies);

            Name = actorName;
            LibraryAssemblies = libraryAssemblies;
            ActorInstance = CreateActor(actorName);
            hook = new StageHook(ActorInstance);
            LastActivity = DateTime.Now;
        }

        internal Performance(Performance source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            Name = source.Name;
            LibraryAssemblies = source.LibraryAssemblies;
            dbType = source.dbType;
            connectionString = source.connectionString;
            storageConfigured = source.storageConfigured;
            started = source.started;
            LastActivity = source.LastActivity;
        }

        public void ConfigureStorage(DatabaseType dbType, string connectionString)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            if (started) throw new InvalidOperationException("Cannot configure storage while running");

            this.dbType = dbType;
            this.connectionString = connectionString;
            storageConfigured = true;
        }

        // Logger seam (fluent, applies to V1 and V2): the sink is per-actor. This
        // facade propagates the impl injected by the host (Serilog, Microsoft.Extensions
        // .Logging, NLog, etc.) to the Actor that lives under this Performance. Without
        // injection, Puppeteer uses a default ConsoleLogger (Error -> stderr,
        // Debug -> stdout). V1/V2 do a `new` shadow to preserve the concrete type
        // in the chain.
        public Performance Logger(IPuppeteerLogger logger)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            ActorInstance.UseLogger(logger);
            return this;
        }

        public void Start(bool asFollower = false)
        {
            if (!storageConfigured)
                throw new InvalidOperationException("Storage not configured. Call ConfigureStorage before Start.");
            if (started)
                throw new InvalidOperationException("Performance is already started.");

            hook.InitializeStorage(dbType, connectionString);
            this.isFollower = asFollower;
            started = true;

            // Follower mode: suppresses the JOURNALING of the Tell terminators that
            // fire the actor's Cued Reactions (1-writer invariant: only the primary
            // writes to the shared canonical journal). Stage 2 implemented: the
            // follower DOES execute the tell and DOES dispatch the envelope via
            // Transport; only the journal write is omitted.
            hook.SuppressReactionJournaling = asFollower;

            // ReactionActivation gate: the Theater Performance acts as
            // director/primary when it is NOT a follower. The provider is live: it reads
            // isFollower on every Reaction.Execute, so after the handover
            // (UnlockAndRunAlive sets isFollower=false) the DirectorOnly ones start
            // running and the CastOnly ones stop.
            hook.SetActingAsDirectorProvider(() => !isFollower);

            if (!asFollower && hook.IsNew)
            {
                PerformanceTracer.Instance.RaiseHydrated(Name, isFirst: true);
                OnFirstHydration();
            }
            if (!asFollower)
            {
                PerformanceTracer.Instance.RaiseHydrated(Name, isFirst: false);
                OnHydrated();
            }

            if (!asFollower) aliveGate.Set();

            StartCuedReactions();
        }

        public void WaitUntilAlive(CancellationToken ct = default)
        {
            aliveGate.Wait(ct);
        }

        private void StartCuedReactions()
        {
            var cuedReactions = ActorInstance.Reactions.CuedReactions;
            bool hasCued = false;

            foreach (var reaction in cuedReactions)
            {
                if (!hasCued)
                {
                    reactionsCts = new CancellationTokenSource();
                    hasCued = true;
                }

                // Subscribe to OnExecutionStopped + register ObservableCounters/Gauges BEFORE
                // the reaction starts. The sampler reads the public counters periodically;
                // the StoppedEvent fires when the cancellationToken triggers the graceful exit.
                PerformanceTracer.Instance.AttachToReaction(Name, reaction);

                var ct = reactionsCts.Token;
                var task = Task.Run(() => reaction.Execute(ReactionExecutionMode.Continuous, ct));
                reactionTasks.Add(task);

                System.Diagnostics.Debug.WriteLine($"[Performance] Started cued reaction '{reaction.Name}' for '{Name}'");
            }
        }

        public bool IsAlive
        {
            get
            {
                if (!started) return false;
                if (!isFollower) return true;
                return handoverComplete;
            }
        }

        public string LockWhileNotSyncronized()
        {
            PerformanceTracer.Instance.RaiseHandoverStarted(Name);
            return hook.LockWhileNotSyncronized();
        }

        public void UnlockAndRunAlive()
        {
            hook.UnlockAndRunAlive();
            handoverComplete = true;
            isFollower = false;
            aliveGate.Set();
            PerformanceTracer.Instance.RaiseHandoverCompleted(Name);
        }

        public void CatchUpFromJournal(long targetEntryId)
        {
            PerformanceTracer.Instance.RaiseCatchUp(Name, targetEntryId);
            hook.CatchUpFromJournal(targetEntryId);
        }

        // Shadow Replay — S1 (handoff_shadow_S1_implementation.md / design §3.0).
        // Performance facade: produces a ShadowPerformance that HOSTS (by
        // composition, NOT inheritance) a shadow of this Performance's actor. The
        // shadow reads the real journal (replay) but writes to its own storage and
        // produces zero external effect. ShadowPerformance is a type DISTINCT from
        // Performance — the compiler prevents a shadow from silently substituting
        // a real Performance.
        public ShadowPerformance Shadow(Puppeteer.ShadowConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (!storageConfigured)
                throw new InvalidOperationException("Storage not configured. Call ConfigureStorage before Shadow.");

            Puppeteer.Shadow shadow = ActorInstance.Shadow(cfg);
            return new ShadowPerformance(shadow);
        }

        // Distill: physically materializes the elisions accumulated in the journal of
        // the actor hosted by this Performance. Operational/administrative — corresponds
        // to the Performance verb. The declarative equivalent in V2 (metadata.Distill()
        // inside a Reaction) arrives in Stage 4.
        public void Distill()
        {
            if (!storageConfigured)
                throw new InvalidOperationException("Storage not configured. Call ConfigureStorage before Distill.");
            // Materialize v2 / Phase 1: Distill now returns a DistillCommand builder;
            // .Now() is the terminator. Without destinations registered via
            // actor.Materialization.Register, it executes without restriction (compat).
            ActorInstance.Distill().Now();
        }

        internal Dispatch.Dispatch CreateDispatchInternal(ActorV2 actorV2, Action<DispatchOptions> configure)
        {
            if (dispatch != null)
                throw new InvalidOperationException("Dispatch already created for this Performance");

            var options = new DispatchOptions();
            configure?.Invoke(options);

            dispatch = new Dispatch.Dispatch(actorV2, options, ct => aliveGate.Wait(ct));
            sagaStepJournal = new SagaStepJournal();
            sagaKeyLock = new KeyLock();
            return dispatch;
        }

        internal SagaDefinition DefineSagaInternal(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));
            if (dispatch == null)
                throw new InvalidOperationException("CreateDispatch must be called before DefineSaga");

            var saga = new SagaDefinition(name, dispatch, sagaStepJournal, sagaKeyLock);
            sagas.Add(saga);
            return saga;
        }

        public void Dispose()
        {
            // Release any Dispatch workers waiting on the gate so they can exit cleanly.
            aliveGate.Set();

            dispatch?.Dispose();
            dispatch = null;

            sagaKeyLock?.Dispose();
            sagaKeyLock = null;
            sagas.Clear();

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
            aliveGate.Dispose();
            started = false;
        }
    }
}
