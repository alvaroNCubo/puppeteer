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

        // Hook legacy: corre una sola vez cuando el actor es brand-new (journal vacio).
        // Se mantiene como muleta de backward-compat para Performances ya en produccion
        // cuyo seed esta acoplado al path de ItsANewOne. Codigo nuevo prefiere usar
        // OnHydrated() con un PerformCmd que contenga 'upgrade('init') { ... }'.
        protected virtual void OnFirstHydration() { }

        // Hook nuevo: corre despues de cada hidratacion (tanto la primera como las
        // subsiguientes en restarts). Pensado para invocar PerformCmd con un script que
        // contiene una secuencia de 'upgrade('X') { ... }' — los ya aplicados se saltan
        // silenciosamente, los nuevos se aplican y se journalizan. Es la via para
        // versionar la inicializacion y migraciones del actor sin .exe externos.
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

        // Logger seam (fluent, aplica a V1 y V2): el sink es per-actor. Esta
        // fachada propaga la impl inyectada por el host (Serilog, Microsoft.Extensions
        // .Logging, NLog, etc.) al Actor que vive bajo este Performance. Sin
        // inyeccion, Puppeteer usa un ConsoleLogger default (Error -> stderr,
        // Debug -> stdout). V1/V2 hacen `new` shadow para preservar el tipo concreto
        // en la cadena.
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

            // Modo follower: suprime el JOURNALING de los Tell terminators que
            // disparen las Cued Reactions del actor (invariante 1-escritor: solo
            // el primary escribe al journal canonico compartido). Etapa 2
            // implementada: el follower SI ejecuta el tell y SI despacha el
            // envelope via Transport; solo se omite la escritura al journal.
            hook.SuppressReactionJournaling = asFollower;

            // Gate de ReactionActivation: la Performance Theater actua como
            // director/primary cuando NO es follower. El provider es vivo: lee
            // isFollower en cada Reaction.Execute, asi tras el handover
            // (UnlockAndRunAlive pone isFollower=false) las DirectorOnly empiezan
            // a correr y las CastOnly dejan de hacerlo.
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
        // Fachada de Performance: produce un ShadowPerformance que HOSPEDA (por
        // composicion, NO herencia) un shadow del actor de esta Performance. El
        // shadow lee el journal real (replay) pero escribe en su propio storage y
        // produce cero efecto externo. ShadowPerformance es un tipo DISTINTO de
        // Performance — el compilador impide que un shadow sustituya silenciosamente
        // a un Performance real.
        public ShadowPerformance Shadow(Puppeteer.ShadowConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (!storageConfigured)
                throw new InvalidOperationException("Storage not configured. Call ConfigureStorage before Shadow.");

            Puppeteer.Shadow shadow = ActorInstance.Shadow(cfg);
            return new ShadowPerformance(shadow);
        }

        // Distill: materializa fisicamente las elisiones acumuladas en el journal del
        // actor hospedado por esta Performance. Operacional/administrativo — corresponde
        // al verbo de Performance. El equivalente declarativo en V2 (metadata.Distill()
        // dentro de un Reaction) llega en Etapa 4.
        public void Distill()
        {
            if (!storageConfigured)
                throw new InvalidOperationException("Storage not configured. Call ConfigureStorage before Distill.");
            // Materialize v2 / Fase 1: Distill ahora retorna builder DistillCommand;
            // .Now() es el terminator. Sin destinations registradas via
            // actor.Materialization.Register, ejecuta sin restriccion (compat).
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
