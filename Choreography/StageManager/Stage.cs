using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Choreography.Transport;
using Puppeteer;
using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Playbill;

namespace Choreography.StageManager
{
    public enum ConnectivityStatus
    {
        Isolated,
        Connected
    }

    // Bug 15 — Casting election channel rewire (Opcion B firmada 2026-05-28):
    // un host externo necesita observar la rotacion de rol para re-establecer
    // Replication/Command channels. Stage no maneja el handshake automaticamente
    // — solo expone OnRoleChanged y el host decide que hacer. Enum publico para
    // que el subscriber pueda discriminar Leader/Candidate/Follower sin reflectar
    // el state machine interno.
    public enum StageRole
    {
        Follower,
        Candidate,
        Leader
    }

    public abstract class Stage : IAsyncDisposable
    {
        private StageConfiguration config;
        private IStageTransport transport;
        protected StageHook hook;
        protected DatabaseType dbType;
        private string storageConnectionString;

        // Coordination bus: all Stages talk to all Stages (lightweight, membership/election)
        private readonly ConcurrentDictionary<PerformerId, IStageChannel> coordinationPeers = new();

        // Data star: Director↔Cast links (heavy, replication/commands)
        private DirectorLink directorLink;  // non-null when I am Cast
        private readonly ConcurrentDictionary<PerformerId, CastLink> castLinks = new();  // populated when I am Director

        private readonly List<CancellationTokenSource> backgroundTasks = new();
        protected readonly ConcurrentDictionary<Guid, TaskCompletionSource<CommandResult>> pendingCommands = new();
        private readonly ConcurrentDictionary<long, BufferedEntry> entryBuffer = new();

        // Casting election protocol fase (a) — Heartbeat loop.
        // lastSeenDirector: timestamp del ultimo Heartbeat (o DirectorAnnounce) recibido
        // del Director actual. Lo lee el DirectorWatchdog para decidir si el Director
        // dejo de emitir y hay que limpiar directorId. Solo se popula para el peer que
        // este Stage considera Director — heartbeats de otros peers se ignoran.
        private readonly ConcurrentDictionary<PerformerId, DateTime> lastSeenDirector = new();
        // heartbeatSenderCts: lifecycle por sesion-de-Director (creado en PromoteToDirector,
        // cancelado en StepDownAsync y en la rama loser de HandleDirectorAnnounce). NO va
        // en backgroundTasks porque su vida no coincide con la del Stage.
        private CancellationTokenSource heartbeatSenderCts;
        // Bug 16 — Diagnostics del HeartbeatSenderLoop. heartbeatsEmitted cuenta cada
        // Heartbeat dispatched (no por-peer; cuenta el tick exitoso post-IsDirector).
        // heartbeatSenderRunning refleja si el loop esta vivo (true entre StartHeartbeatSender
        // y exit del loop). Ambos son volatile para que tests/host lo lean sin lock.
        // Sirven para reduccion del reporte device 2026-05-27 PM3: heartbeats observados
        // 20s post-CatchUp y luego silencio 16s. Sin contadores no se distingue
        // "sender muerto" de "sender vivo pero send falla silenciosamente".
        private long heartbeatsEmittedCount;
        private volatile bool heartbeatSenderRunning;

        // Bug 17 — Diagnostics del path de replicacion ListenReplication. Cuenta
        // cuantos CueEvent fueron silently-dropped por colision de EntryId (caso
        // cue.EntryId <= localMax: el Cast ya tenia una entry en esa posicion,
        // probablemente porque escribio localmente antes del join). El reporte
        // device 2026-05-27 PM5 atribuye este sintoma a "asimetria catch-up vs
        // rehydration" pero el codigo lo muestra mas crudo: el Director envia
        // entry N y el Cast la descarta porque ya hay algo distinto en N.
        // the host Performance setea livesyncNotifier locally pre-join, lo que genera la
        // colision en EntryId=1 con el seed del Director.
        private long replicationDroppedOlderCount;
        // directorWatchdogCts: lifecycle por sesion-de-Stage (creado en StartAsync,
        // cancelado en StopAsync via backgroundTasks).
        private CancellationTokenSource directorWatchdogCts;

        // Casting election protocol fase (b) — state machine + persistencia.
        // Role: Follower (default) / Candidate (election en curso, vote-for-self
        // emitido, esperando accepts) / Leader (this Stage is Director con quorum).
        // El campo legacy directorId sigue siendo source-of-truth para
        // "quien es el Director del cluster" desde el punto de vista de este peer;
        // role agrega la dimension "que estoy haciendo yo dentro del protocolo".
        // El state machine interno reusa el enum publico StageRole — antes era
        // un nested private Role, pero la firma del evento OnRoleChanged necesita
        // un tipo publico y mantener dos enums paralelos era ruido. Las semanticas
        // (Follower default, Candidate durante eleccion, Leader con quorum) son
        // las mismas.
        private StageRole role = StageRole.Follower;
        // TermStore se construye en StartAsync (necesita StageStateDirectory creado).
        private TermStore termStore;
        // KnownPeersStore (bug 19): membresia persistida de peers de Coordination, para
        // que el host pueda reabrir Coordination tras un process-death sin importar el
        // rol previo del nodo. Se construye en StartAsync junto con TermStore.
        private KnownPeersStore knownPeersStore;
        // Election state. currentElectionId distinto de null indica role==Candidate.
        // votesReceived incluye self-vote desde el momento de StartElection.
        // neededVotes captura el quorum al inicio del round (no recalcular: si peers
        // entran/salen mid-eleccion, el round actual sigue con el quorum original).
        private Guid? currentElectionId;
        private HashSet<PerformerId> votesReceived;
        private int neededVotes;
        // Protege role, currentElectionId, votesReceived, directorId del state
        // machine. ListenCoordination corre en un task por peer — sin el lock,
        // un CastingAccept y un DirectorAnnounce concurrentes podrian dejar el
        // state inconsistente. termStore tiene su propio lock interno; no se
        // anida (las llamadas a termStore desde dentro de electionLock son ok).
        private readonly object electionLock = new object();
        // Casting election protocol fase (d) — Randomized backoff.
        // electionRoundCts: lifecycle por round-de-eleccion. Se crea en cada
        // StartElection (despues del broadcast) para gatillar el ElectionRoundTimerLoop.
        // Se cancela cuando el round termina (BecomeDirector, term-adopt en cualquiera
        // de los handlers, StepDown, shutdown).
        private CancellationTokenSource electionRoundCts;
        // Contador observable de cuantas veces el round timer disparo abort+backoff+retry.
        // Util para tests que validan que el camino de backoff fue ejercitado.
        private int electionRoundTimeoutCount;

        private PerformerId? directorId;
        private ConnectivityStatus connectivity = ConnectivityStatus.Isolated;
        protected bool isRunning;

        private bool storageConfigured;
        private bool transportConfigured;

        public PerformerId Id { get; }
        public bool IsDirector => directorId.HasValue && directorId.Value == Id
                                  && connectivity == ConnectivityStatus.Connected;
        public PerformerId? CurrentDirectorId => directorId;
        public long CurrentEntryId => hook.CurrentEntryId;
        public Reactions Reactions => hook.Reactions;
        public ConnectivityStatus Connectivity => connectivity;
        protected internal Assembly[] LibraryAssemblies { get; }

        public event Action<PerformerId> OnDirectorChanged;

        // Bug 12 — Casting election (split-brain recovery): cuando este Stage,
        // siendo Director, recibe un DirectorAnnounce de otro peer que tambien
        // se declara Director (situacion tipica tras un force-promote durante
        // particion), un tiebreaker determinista por (MaxEntryId desc, PerformerId
        // asc) decide el ganador. Si este Stage pierde, demote silenciosamente
        // y dispara este evento con el detalle de la divergencia para que la
        // aplicacion pueda reconciliar journals (SendCatchUpAsync, alerta a
        // operacion, descartar data dir, etc.). Si HasDivergentTail==true el
        // loser tiene entries propias que el winner nunca vio — esas se
        // perderian en una rehidratacion-desde-winner (no hay primitiva de
        // truncate en el journal layer); la decision queda en la aplicacion.
        public event Action<SplitBrainDetected> OnDirectorElectionLost;

        // Casting election protocol fase (a) — Director-down detection.
        // El DirectorWatchdog dispara este evento cuando no se reciben heartbeats del
        // Director actual dentro de DirectorTimeout. Despues de invocarlo, directorId
        // queda en null y EnsureCanWrite bloquea escrituras hasta que algo reasigne
        // Director (operador via PromoteToDirector, o etapa b: eleccion automatica).
        // El argumento es el Id del Director que se perdio.
        public event Action<PerformerId> OnDirectorLost;

        // Bug 15 — Casting election channel rewire (Opcion B). Se dispara cada vez
        // que el state machine de Casting election cambia de rol (Follower ↔
        // Candidate ↔ Leader), DESPUES de actualizar role/directorId/heartbeat
        // sender, FUERA del electionLock. El subscriber recibe (newRole, directorId)
        // y puede reaccionar — tipicamente para abrir/cerrar Replication/Command
        // channels hacia los peers que estan en el bus de Coordination.
        //
        // - Leader: el host deberia iterar coordinationPeers y AcceptCastConnection
        //   contra cada uno (la otra punta debe estar lista para recibir el
        //   handshake).
        // - Follower con directorId!=null: el host deberia hacer
        //   ConnectToDirector contra ese directorId.
        // - Follower con directorId==null o Candidate: usualmente no-op
        //   (transicion intermedia; otro evento llegara cuando la convergencia
        //   complete).
        //
        // Solo se dispara cuando newRole != previousRole (cambios de directorId
        // sin cambio de rol siguen yendo por OnDirectorChanged). El subscriber
        // corre en el thread que disparo la transicion — puede ser un
        // task del bus de Coordination (HandleDirectorAnnounce, HandleCastingPropose,
        // BecomeDirector via HandleCastingAccept) o el thread del watchdog
        // (StartElection -> selfQuorum -> BecomeDirector). No bloquear el handler:
        // si el handshake es asincrono lanzarlo con Task.Run.
        public event Action<StageRole, PerformerId?> OnRoleChanged;

        protected abstract Actor CreateActor(string actorName);

        // Hook legacy: corre una sola vez cuando el actor es brand-new (journal vacio
        // al momento de promoverse a Director). Se mantiene como muleta de backward-compat
        // para Stages cuyo seed esta acoplado al path de ItsANewOne. Codigo nuevo prefiere
        // OnHydrated() con un PerformCmd que contenga 'upgrade('init') { ... }'.
        protected virtual void OnFirstHydration() { }

        // Hook nuevo: corre cada vez que este Stage se promueve a Director, ANTES de
        // marcar IsDirector=true y antes de aceptar PerformCmds (locales o forwarded).
        // Pensado para invocar hook.PerformCmd con un script que contiene una secuencia
        // de 'upgrade('X') { ... }' — los ya aplicados se saltan silenciosamente, los
        // nuevos se journalizan localmente y luego se replican a los Casts via catch-up
        // tras el DirectorAnnounce.
        //
        // En Cast nunca se invoca: los upgrades llegan al Cast por replicacion del
        // Director, no por hidratacion propia. Si tu subclase necesita reaccionar al
        // estar sincronizada como Cast, ese es un hook distinto (no implementado aun).
        protected virtual void OnHydrated() { }

        internal Stage(PerformerId id, string actorName)
            : this(id, actorName, Array.Empty<Assembly>())
        {
        }

        internal Stage(PerformerId id, string actorName, params Assembly[] libraryAssemblies)
        {
            if (string.IsNullOrWhiteSpace(actorName))
                throw new ArgumentNullException(nameof(actorName));
            ArgumentNullException.ThrowIfNull(libraryAssemblies);

            this.Id = id;
            this.LibraryAssemblies = libraryAssemblies;
            this.hook = new StageHook(CreateActor(actorName));
        }

        // --- Configuration ---

        public void ConfigureStorage(DatabaseType dbType, string connectionString)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            if (isRunning) throw new InvalidOperationException("Cannot configure storage while running");

            this.dbType = dbType;
            this.storageConnectionString = connectionString;

            string path = ParsePathFromConnectionString(connectionString);
            this.config = new StageConfiguration
            {
                StageStateDirectory = Path.Combine(path, "_stage")
            };
            config.Validate();

            this.storageConfigured = true;
        }

        private static string ParsePathFromConnectionString(string connectionString)
        {
            if (!connectionString.Contains('='))
                return connectionString;

            foreach (var segment in connectionString.Split(';'))
            {
                var trimmed = segment.Trim();
                int eqIndex = trimmed.IndexOf('=');
                if (eqIndex < 0) continue;

                string key = trimmed[..eqIndex].Trim();
                string value = trimmed[(eqIndex + 1)..].Trim();

                if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
                    return value;
            }

            throw new ArgumentException("connectionString must contain a 'path' key");
        }

        // Logger seam (fluent, aplica a V1 y V2): el sink es per-actor. Esta
        // fachada propaga la impl inyectada por el host (Serilog, Microsoft.Extensions
        // .Logging, NLog, etc.) al Actor que vive bajo este Stage. Sin inyeccion,
        // Puppeteer usa un ConsoleLogger default (Error -> stderr, Debug -> stdout).
        // V1/V2 hacen `new` shadow para preservar el tipo concreto en la cadena.
        //
        // Ordering: .Logger() DEBE ir antes de ConfigureTransport. El transport
        // recibe el sink por ctor y queda fijado en ese instante; cambiar logger
        // despues no propagaria al transport ya construido. El throw temprano
        // hace el contrato visible en lugar de fallar en silencio.
        public Stage Logger(IPuppeteerLogger logger)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (transportConfigured)
                throw new InvalidOperationException(
                    "Call .Logger() before ConfigureTransport. The transport is built with the logger via ctor "
                    + "and re-wiring is not supported. Configure the logger first, then call ConfigureTransport.");
            hook.UseLogger(logger);
            return this;
        }

        public void ConfigureTransport(TransportType transportType, string url = null, byte[] serverFingerprint = null,
            System.Security.Cryptography.X509Certificates.X509Certificate2 httpsServerCert = null,
            string httpsAdvertiseUrl = null)
        {
            if (isRunning) throw new InvalidOperationException("Cannot configure transport while running");

            // El transport recibe el logger por ctor (snapshot del sink configurado
            // por el host via .Logger(x) o el ConsoleLogger default del Actor). Una
            // vez construido el transport queda fijado al sink elegido; cambiar
            // .Logger() despues no propaga (ver F9: ordering rule).
            var loggerForTransport = hook.Logger;
            this.transport = transportType switch
            {
                TransportType.InMemory => new InMemoryTransport(Id, loggerForTransport),
                TransportType.SimpleX => CreateSimplexTransport(url, serverFingerprint, loggerForTransport),
                TransportType.Https => CreateHttpsTransport(url, httpsServerCert, httpsAdvertiseUrl, loggerForTransport),
                _ => throw new ArgumentException($"Unknown transport type: {transportType}")
            };

            this.transportConfigured = true;
        }

        // Paper 7 Phase 2 — fingerprint accessors for the underlying HTTPS
        // transport (if any). The actual HttpsTransport type stays internal;
        // these two operations are the public surface tests + CLI + Docker
        // hosts need to pin peer certs before opening TLS connections.

        public string LocalHttpsCertFingerprint
        {
            get
            {
                var ht = this.transport as Transport.Https.HttpsTransport;
                return ht?.LocalCertFingerprint;
            }
        }

        public void TrustPeerHttpsFingerprint(string peerListenUrl, string fingerprintHex)
        {
            if (string.IsNullOrWhiteSpace(peerListenUrl)) throw new ArgumentNullException(nameof(peerListenUrl));
            if (string.IsNullOrWhiteSpace(fingerprintHex)) throw new ArgumentNullException(nameof(fingerprintHex));
            if (this.transport is Transport.Https.HttpsTransport ht)
                ht.TrustPeerFingerprint(peerListenUrl, fingerprintHex);
            else
                throw new InvalidOperationException(
                    "TrustPeerHttpsFingerprint only applies when the transport is HTTPS");
        }


        // Paper 7 Phase 2 — HTTPS now runs on Kestrel with a real TLS cert.
        // If the caller does not supply one, we auto-generate a self-signed
        // certificate valid for one year. The corresponding fingerprint is
        // exposed through ((HttpsTransport)this.transport).LocalCertFingerprint
        // for callers that need to publish it (CLI, share-link encoder).
        private Transport.Https.HttpsTransport CreateHttpsTransport(string listenUrl,
            System.Security.Cryptography.X509Certificates.X509Certificate2 cert,
            string advertiseUrl,
            IPuppeteerLogger logger)
        {
            if (listenUrl == null) throw new ArgumentNullException(nameof(listenUrl), "listenUrl is required for Https transport");
            cert ??= Transport.Https.SelfSignedCert.Generate(
                subjectCommonName: "puppeteer-stage",
                "localhost",
                System.Uri.TryCreate(listenUrl, UriKind.Absolute, out var lu) ? lu.Host : "puppeteer-stage",
                System.Uri.TryCreate(advertiseUrl, UriKind.Absolute, out var au) ? au.Host : "puppeteer-stage");
            return new Transport.Https.HttpsTransport(Id, listenUrl, cert, advertiseUrl, logger);
        }

        // SimpleX transport carga crypto/TLS managed pero la app target es mobile (Android/iOS).
        // En Windows desktop (maquina de dev) hacemos fallback a InMemoryTransport para que
        // tests y la app puedan correr sin emulador. Para el path real desde Windows, usar
        // emulador Android o WSL Linux. Ver DEVELOPMENT-WINDOWS.md.
        //
        // serverFingerprint (TOFU): SHA-256 del idCert del SMP server. Lo conoce el Stage
        // a-priori porque el Ushier (app tercera que arma invitaciones via QR) emite URIs
        // smp://HASH@host:port/... con el hash incluido. Para el creator viene del config;
        // para joiner-only puede ser null y SimplexTransport lo extrae del URI al accept.
        private IStageTransport CreateSimplexTransport(string url, byte[] serverFingerprint, IPuppeteerLogger logger)
        {
            if (OperatingSystem.IsWindows())
            {
                logger.Debug("[Choreography] SimpleX transport stubbed to InMemoryTransport on Windows. " +
                             "Run on Android emulator or Linux for the real path.");
                return new InMemoryTransport(Id, logger);
            }
            return new Transport.SimpleX.SimplexTransport(Id,
                url ?? throw new ArgumentNullException(nameof(url), "url is required for SimpleX transport"),
                serverFingerprint,
                logger);
        }

        // --- Lifecycle ---

        public Task StartAsync(CancellationToken ct = default)
        {
            if (!storageConfigured)
                throw new InvalidOperationException("Storage not configured. Call ConfigureStorage before StartAsync.");
            if (!transportConfigured)
                throw new InvalidOperationException("Transport not configured. Call ConfigureTransport before StartAsync.");

            Directory.CreateDirectory(config.StageStateDirectory);

            // Casting election fase (b): persistencia de term/votedFor. Cargar antes
            // que hook para que cualquier eleccion gatillada durante hidratacion
            // (no deberia, pero defensive) vea el term correcto.
            termStore = new TermStore(config.StageStateDirectory);
            knownPeersStore = new KnownPeersStore(config.StageStateDirectory);

            hook.InitializeStorage(dbType, storageConnectionString);
            hook.OnRecordWritten = OnRecordWritten;
            // Gate de ReactionActivation: el rol vivo del Stage P2P es su
            // IsDirector (cambia con la eleccion). DirectorOnly corre solo en el
            // director; CastOnly solo en los Casts; Company en ambos.
            hook.SetActingAsDirectorProvider(() => IsDirector);
            // Phase 5 of the Action refactor: dropped hook.OnNewActionDefined wiring.
            // Define entries are journal records and replicate via OnRecordWritten →
            // CueEvent like any other record (firmado: cross-stage atomicity is
            // unnecessary — the director's journal already persisted the pair
            // transactionally). The follower applies the Define record via
            // ApplyReplicatedEvent which dispatches to AddKnownActionFromDefine.

            isRunning = true;
            StartDirectorWatchdog();
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            hook.GracefulExit();
            isRunning = false;
            connectivity = ConnectivityStatus.Isolated;
            foreach (var cts in backgroundTasks) cts.Cancel();
            heartbeatSenderCts?.Cancel();
            electionRoundCts?.Cancel();
            return Task.CompletedTask;
        }

        // --- Transport delegation ---

        public Task<ConnectionInvitation> CreateInvitationAsync(ChannelPurpose purpose)
        {
            if (transport == null) throw new InvalidOperationException("Transport not configured");
            return transport.CreateInvitationAsync(purpose);
        }

        public Task<IStageChannel> AcceptInvitationAsync(ConnectionInvitation invitation)
        {
            if (transport == null) throw new InvalidOperationException("Transport not configured");
            return transport.AcceptInvitationAsync(invitation);
        }

        public Task<IStageChannel> WaitForConnectionAsync(ConnectionInvitation invitation, CancellationToken ct)
        {
            if (transport == null) throw new InvalidOperationException("Transport not configured");
            return transport.WaitForConnectionAsync(invitation, ct);
        }

        // --- Write guard ---

        protected void EnsureCanWrite()
        {
            if (!isRunning)
                throw new InvalidOperationException("Stage is not running");

            if (connectivity == ConnectivityStatus.Isolated)
                throw new InvalidOperationException(
                    "Stage is isolated (no verified connectivity). Commands are inhibited to protect journal integrity.");

            if (!directorId.HasValue)
                throw new InvalidOperationException("No Director available");
        }

        // --- Connectivity ---

        private void RefreshConnectivity()
        {
            bool hasCoordinationPeer = coordinationPeers.Values.Any(ch => ch.IsConnected);
            connectivity = hasCoordinationPeer
                ? ConnectivityStatus.Connected
                : ConnectivityStatus.Isolated;
        }

        // =====================================================================
        //  COORDINATION BUS: all Stages ↔ all Stages (lightweight)
        //  Messages: DirectorAnnounce, MemberLeave, MemberJoin, Heartbeat, Casting
        // =====================================================================

        // Bug 19 — Peers de Coordination conocidos, persistidos a traves de process-death.
        //
        // Disponible inmediatamente despues de StartAsync (lee de StageStateDirectory/peers.bin),
        // ANTES de que exista ningun canal de Coordination vivo. El host la consulta al
        // arrancar para reabrir Coordination con cada peer conocido — independientemente
        // del rol previo del nodo. Eso cierra el caso del ex-Director que muere y vuelve:
        // su KnownPeersStore recuerda al nuevo Director (que antes era su Cast), el host
        // reabre Coordination, llega el DirectorAnnounce y el camino term-first + (si hay
        // rotacion vigente) el re-handshake in-band de bug 18 completan la reconciliacion.
        //
        // El Stage solo aporta a QUIENES reconectar; las Address de reconexion las posee el
        // host (modelo invitation-based: el Stage nunca las ve en JoinCoordination).
        public IReadOnlyList<PerformerId> RecallKnownPeers()
            => knownPeersStore?.All ?? Array.Empty<PerformerId>();

        public async Task JoinCoordination(PerformerId peerId, IStageChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));

            coordinationPeers[peerId] = channel;
            // Bug 19: recordar la membresia para que el host pueda reabrir Coordination
            // con este peer tras un process-death (ver KnownPeersStore / RecallKnownPeers).
            // Idempotente: solo toca disco la primera vez que se conoce el peer.
            knownPeersStore?.Remember(peerId);
            RefreshConnectivity();

            var cts = new CancellationTokenSource();
            backgroundTasks.Add(cts);
            _ = Task.Run(async () => await ListenCoordination(peerId, channel, cts.Token), cts.Token);

            if (IsDirector)
            {
                // Fire-and-forget: el announce sale ya. El canal de Coordination
                // es buffered (unbounded), asi que aun si el listener del Cast no
                // arranco todavia el mensaje queda en queue y se procesa cuando
                // arranca. Quitamos el Task.Delay(50) que era defensivo sin razon
                // documentada — con WaitForDirectorAsync en ConnectToDirector el
                // race del consumer ya esta cerrado por contrato del API, no por
                // timing. Eliminar el delay tambien acelera el join del Cast en
                // ~50ms en el caso the host Performance (Director ya activo cuando aparece Cast).
                long maxAtAnnounce = hook.CurrentEntryId;
                long termAtAnnounce = termStore?.CurrentTerm ?? 0L;
                _ = Task.Run(async () =>
                {
                    try { await channel.SendAsync(new DirectorAnnounce(Id, Id, maxAtAnnounce, termAtAnnounce)); }
                    catch { }
                });
            }
        }

        // =====================================================================
        //  DATA STAR: Cast ↔ Director (heavy)
        //  Replication: CueEvent, ActionDefinition, CueAck
        //  Command:     ForwardCommand, CommandResult
        // =====================================================================

        public async Task ConnectToDirector(PerformerId directorId,
            IStageChannel replicationChannel, IStageChannel commandChannel = null,
            CancellationToken ct = default)
        {
            if (replicationChannel == null) throw new ArgumentNullException(nameof(replicationChannel));

            this.directorLink = new DirectorLink
            {
                Replication = replicationChannel,
                Command = commandChannel
            };

            var cts = new CancellationTokenSource();
            backgroundTasks.Add(cts);
            _ = Task.Run(async () => await ListenReplication(directorId, replicationChannel, cts.Token), cts.Token);
            if (commandChannel != null)
                _ = Task.Run(async () => await ListenCommand(directorId, commandChannel, cts.Token), cts.Token);

            // Cierra el race con EnsureCanWrite SOLO si el Cast tiene canal de
            // comandos (intencion de forward de PerformCmd al Director). Sin
            // commandChannel el Cast es replication-only — no escribe, no necesita
            // directorId asignado, y forzar la espera bloquearia los setups que
            // no usan el bus de Coordination (ej. HttpsTransportTests usa el data
            // star directo sin coordination membership).
            //
            // Con commandChannel: el caller declarara `cast.PerformCmd(...)` despues,
            // que entra a EnsureCanWrite y exige directorId.HasValue. Esperar al
            // DirectorAnnounce del bus de Coordination cierra el race documentado en
            // PromoteToDirector (linea ~430) y workaround-eado en the host Performance con
            // Task.Delay(5s). Pre-requisito: JoinCoordination debe haber sido
            // invocado antes — sin coordination membership, este await bloquea hasta
            // que el CT cancele (politica del caller).
            if (commandChannel != null)
                await WaitForDirectorAsync(ct);
        }

        // Espera bloqueante hasta que el state machine local registra un Director
        // activo (this.directorId tiene valor). El registro ocurre cuando:
        //   - llega un DirectorAnnounce via el bus de Coordination (Cast), o
        //   - este Stage se promueve a si mismo (PromoteToDirector).
        //
        // Suscribe al evento OnDirectorChanged ANTES del segundo chequeo de
        // directorId para evitar lost-wakeup: si el announce llega entre la
        // suscripcion y el chequeo, el TaskCompletionSource captura la transicion;
        // si llego antes de la suscripcion, el chequeo post-subscribe lo detecta.
        public async Task WaitForDirectorAsync(CancellationToken ct = default)
        {
            if (directorId.HasValue) return;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PerformerId> handler = _ => tcs.TrySetResult();
            OnDirectorChanged += handler;
            try
            {
                if (directorId.HasValue) return;
                await tcs.Task.WaitAsync(ct);
            }
            finally
            {
                OnDirectorChanged -= handler;
            }
        }

        public async Task AcceptCastConnection(PerformerId castId,
            IStageChannel replicationChannel, IStageChannel commandChannel = null)
        {
            if (replicationChannel == null) throw new ArgumentNullException(nameof(replicationChannel));

            var link = new CastLink
            {
                Replication = replicationChannel,
                Command = commandChannel
            };
            // Single-worker drain of the per-link CueEvent queue. Preserves
            // FIFO ordering across OnRecordWritten invocations on this link —
            // see the class comment on CastLink for the race this guards.
            link.StartReplicationSender(Id, hook.Logger);
            castLinks[castId] = link;

            var cts = new CancellationTokenSource();
            backgroundTasks.Add(cts);
            _ = Task.Run(async () => await ListenReplication(castId, replicationChannel, cts.Token), cts.Token);
            if (commandChannel != null)
                _ = Task.Run(async () => await ListenCommand(castId, commandChannel, cts.Token), cts.Token);
        }

        // --- Director transitions ---

        public async Task StepDownAsync()
        {
            if (!IsDirector) return;

            hook.Logger.Debug($"[Stage {Id}] Stepping down as Director");
            StageRole previousRole;
            lock (electionLock)
            {
                previousRole = role;
                directorId = null;
                role = StageRole.Follower;
                currentElectionId = null;
                votesReceived = null;
            }
            heartbeatSenderCts?.Cancel();
            electionRoundCts?.Cancel();
            RaiseRoleChanged(previousRole);

            foreach (var kvp in coordinationPeers)
            {
                if (kvp.Value.IsConnected)
                {
                    try { await kvp.Value.SendAsync(new MemberLeave(Id)); }
                    catch { }
                }
            }
        }

        public void PromoteToDirector(bool force = false)
        {
            if (!force && connectivity == ConnectivityStatus.Isolated)
                throw new InvalidOperationException(
                    "Cannot promote to Director while isolated. At least one peer must be reachable.");

            if (force && connectivity == ConnectivityStatus.Isolated)
                connectivity = ConnectivityStatus.Connected;

            // Hooks de hidratacion del Director: corren ANTES de marcar IsDirector=true.
            // Mientras corren, las escrituras del hook entran al journal local pero NO
            // se broadcastean (OnRecordWritten chequea IsDirector). PerformCmds locales
            // que entren en esta ventana fallan en EnsureCanWrite con "No Director
            // available" — la app reintentara. Los Casts (si los hubiese) recibiran las
            // entradas del upgrade en el catch-up posterior al DirectorAnnounce.
            if (hook.IsNew) OnFirstHydration();
            OnHydrated();

            hook.Logger.Debug($"[Stage {Id}] Promoting self to Director (force={force})");
            StageRole previousRole;
            lock (electionLock)
            {
                previousRole = role;
                directorId = Id;
                role = StageRole.Leader;
                // Si veniamos de Candidate en otra eleccion, abortamos ese round.
                currentElectionId = null;
                votesReceived = null;
            }
            OnDirectorChanged?.Invoke(Id);
            RaiseRoleChanged(previousRole);

            // PromoteToDirector(force:true) NO bumpea term (decision firmada 2026-05-27,
            // entorno Stage end-user: el protocolo es source-of-truth; force-promote
            // queda provisional hasta que el cluster reconverja). Si otro lado de la
            // particion eligio legitimamente con term mayor, su DirectorAnnounce nos
            // demoteara silenciosamente en HandleDirectorAnnounce term-first.
            long maxAtPromote = hook.CurrentEntryId;
            long myTerm = termStore?.CurrentTerm ?? 0L;
            foreach (var kvp in coordinationPeers)
            {
                if (kvp.Value.IsConnected)
                {
                    var announce = new DirectorAnnounce(Id, Id, maxAtPromote, myTerm);
                    var ch = kvp.Value;
                    _ = Task.Run(async () =>
                    {
                        try { await ch.SendAsync(announce); }
                        catch { }
                    });
                }
            }

            StartHeartbeatSender();
        }

        // --- Command forwarding (Cast -> Director) ---

        protected async Task<string> ForwardToDirector(ForwardCommand msg, CancellationToken ct)
        {
            EnsureCanWrite();

            if (directorLink == null || directorLink.Command == null || !directorLink.Command.IsConnected)
                throw new InvalidOperationException("Command channel to Director not available");

            var tcs = new TaskCompletionSource<CommandResult>();
            pendingCommands[msg.CommandId] = tcs;

            try
            {
                await directorLink.Command.SendAsync(msg, ct);

                using var timeoutCts = new CancellationTokenSource(config.CommandForwardingTimeout);
                using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

                var result = await tcs.Task;

                if (result.Success)
                    return result.Result;
                else
                    throw new InvalidOperationException(result.Result);
            }
            finally
            {
                pendingCommands.TryRemove(msg.CommandId, out _);
            }
        }

        // --- Catch-up ---

        public async Task SendCatchUpAsync(PerformerId peerId, long peerLastEntryId, CancellationToken ct = default)
        {
            if (!castLinks.TryGetValue(peerId, out var link))
                throw new InvalidOperationException($"Cast {peerId} not connected");
            if (link.Replication == null)
                throw new InvalidOperationException($"Replication channel to {peerId} not available");

            long myMax = hook.CurrentEntryId;
            hook.Logger.Debug($"[Stage {Id}] CatchUp for {peerId}: from {peerLastEntryId + 1} to {myMax}");

            // Fase 5 — Playbill catch-up: enviar primero todos los schemas
            // (idempotente en el Cast) y luego todos los records con EntryId >
            // peerLastEntryId. Esto debe ir ANTES del catch-up del journal para
            // que cuando el Cast aplique un CueEvent con un EntryId que tambien
            // tiene playbill record, el schema ya este registrado. Tracking
            // separado de last-applied playbill EntryId no existe — usamos el
            // mismo peerLastEntryId (asumiendo que journal y playbill avanzan
            // juntos por construccion de Performance.PerformCommand).
            if (hook.Playbill != null)
            {
                foreach (var (name, declarations) in hook.Playbill.ListSchemas())
                {
                    var schemaCue = new PlaybillSchemaCue(Id, name, declarations);
                    await link.EnqueueReplicationAsync(schemaCue, ct);
                }

                var pendingPlaybill = new List<Puppeteer.EventSourcing.Playbill.PlaybillRecord>();
                hook.Playbill.ReadRecordsAfter(peerLastEntryId, pendingPlaybill);
                foreach (var record in pendingPlaybill)
                {
                    var playbillCue = new PlaybillCue(Id, record.EntryId, record.SchemaName, record.SerializedParameters);
                    await link.EnqueueReplicationAsync(playbillCue, ct);
                }
            }

            for (long eid = peerLastEntryId + 1; eid <= myMax; eid++)
            {
                if (entryBuffer.TryGetValue(eid, out var entry) && entry.Record != null)
                {
                    // Phase 5 of the Action refactor: dropped the ActionDef
                    // pre-broadcast in catch-up. Define records ride CueEvent like
                    // any other journal record.

                    // Route via the same per-link queue used by OnRecordWritten so
                    // catch-up records and live broadcasts cannot interleave out
                    // of order on this link. WriteAsync awaits if the worker has
                    // not yet drained — for an unbounded channel that is a no-op,
                    // but it preserves cancellation semantics.
                    var cue = new CueEvent(Id, eid, entry.Record);
                    await link.EnqueueReplicationAsync(cue, ct);
                    await Task.Delay(10, ct);
                }
                else
                {
                    hook.Logger.Debug($"[Stage {Id}] CatchUp: record {eid} not in buffer");
                }
            }
        }

        // --- Channel listeners ---

        private async Task ListenCoordination(PerformerId peerId, IStageChannel channel, CancellationToken ct)
        {
            try
            {
                await foreach (var msg in channel.Receive(ct))
                {
                    switch (msg)
                    {
                        case DirectorAnnounce announce:
                            hook.Logger.Debug($"[Stage {Id}] DirectorAnnounce from {announce.DirectorId} (peerMax={announce.MaxEntryId})");
                            HandleDirectorAnnounce(announce, channel);
                            break;

                        case Heartbeat hb:
                            // Solo registra evidencia de vida del Director ACTUAL. Heartbeats
                            // de otros peers (no Director, o de un ex-Director cuyo rol ya
                            // perdimos) se ignoran. lastSeenDirector lo lee el watchdog.
                            if (directorId.HasValue && directorId.Value == hb.SenderId)
                                lastSeenDirector[hb.SenderId] = DateTime.UtcNow;
                            break;

                        case CastingPropose propose:
                            HandleCastingPropose(propose, channel);
                            break;

                        case CastingAccept accept:
                            HandleCastingAccept(accept);
                            break;

                        case CastingReject reject:
                            HandleCastingReject(reject);
                            break;

                        case MemberLeave leave:
                            hook.Logger.Debug($"[Stage {Id}] MemberLeave from {leave.SenderId}");
                            if (directorId.HasValue && directorId.Value == leave.SenderId)
                            {
                                hook.Logger.Debug($"[Stage {Id}] Director left, director is now null");
                                directorId = null;
                                lastSeenDirector.TryRemove(leave.SenderId, out _);
                            }
                            break;

                        case MemberJoinAck ack:
                            break;

                        // Bug 18 — re-handshake in-band de los data channels tras
                        // una rotacion de roles. El Cast pide (Request) y el Director
                        // responde con las Address de las nuevas invitaciones (Proposal).
                        case RehandshakeRequest rehReq:
                            HandleRehandshakeRequest(rehReq, channel);
                            break;

                        case RehandshakeProposal rehProp:
                            HandleRehandshakeProposal(rehProp);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { hook.Logger.Error($"[Stage {Id}] ListenCoordination error", ex); }
        }

        // Casting election protocol — HandleDirectorAnnounce TERM-FIRST.
        //
        // Tres reglas en orden:
        //
        // 1) announce.Term < myTerm:
        //    Stale announcement (peer Director de un term viejo). Lo ignoro
        //    silenciosamente. No re-anuncio nada — el peer aprendera que esta
        //    atrasado en el proximo propose round o por algun mensaje subsiguiente.
        //
        // 2) announce.Term > myTerm:
        //    El peer Director es legitimamente mas reciente. Adopto el term
        //    superior (reset de votedFor), paso a Follower (cancelo eleccion en
        //    curso si era Candidate, cancelo heartbeat si era Leader), y acepto
        //    al peer como Director. Esto es lo que demoteara silenciosamente a
        //    un force-promoted local si el cluster reconverge con term mayor —
        //    la garantia que el entorno Stage pide (decision 2026-05-27).
        //
        // 3) announce.Term == myTerm:
        //    Mismo term que el mio. Sub-casos:
        //      3a) no soy Director, o el peer anuncia mi propio Id: acepto.
        //          Comportamiento legacy (el Cast obedece al announce).
        //      3b) ambos creemos ser Director con MISMO term: caso bug-12.
        //          Tiebreaker (MaxEntryId desc, PerformerId asc). Si pierdo,
        //          demote + OnDirectorElectionLost; si gano, rebut. Term-empate
        //          solo ocurre por force-promote concurrente o bug en persistencia;
        //          el tiebreaker preserva convergencia determinista en ese caso.
        private void HandleDirectorAnnounce(DirectorAnnounce announce, IStageChannel channel)
        {
            long myTerm = termStore?.CurrentTerm ?? 0L;

            // Regla 1: stale announce — ignoro.
            if (announce.Term < myTerm)
            {
                hook.Logger.Debug(
                    $"[Stage {Id}] Stale DirectorAnnounce from {announce.DirectorId} " +
                    $"(announceTerm={announce.Term} < myTerm={myTerm}). Ignoring.");
                return;
            }

            // Regla 2: term superior — adopt + accept.
            if (announce.Term > myTerm)
            {
                hook.Logger.Debug(
                    $"[Stage {Id}] DirectorAnnounce term-upgrade {myTerm} → {announce.Term} " +
                    $"from {announce.DirectorId}. Becoming Follower.");
                termStore?.AdoptTermIfHigher(announce.Term);
                bool wasLeader;
                StageRole previousRole;
                PerformerId? oldDirector;
                lock (electionLock)
                {
                    previousRole = role;
                    wasLeader = role == StageRole.Leader;
                    role = StageRole.Follower;
                    currentElectionId = null;
                    votesReceived = null;
                    oldDirector = directorId;
                    directorId = announce.DirectorId;
                }
                if (wasLeader) heartbeatSenderCts?.Cancel();
                electionRoundCts?.Cancel();
                lastSeenDirector[announce.DirectorId] = DateTime.UtcNow;
                OnDirectorChanged?.Invoke(announce.DirectorId);
                RaiseRoleChanged(previousRole);
                // Bug 18 — failover por term-upgrade: si esto es una rotacion (ya
                // conociamos otro Director, o eramos el Director), los data channels
                // viejos quedaron muertos. Pedir re-handshake in-band al nuevo Director.
                RequestRehandshakeIfRotated(oldDirector, announce.DirectorId);
                return;
            }

            // Regla 3: announce.Term == myTerm.
            bool iAmDirector;
            lock (electionLock)
            {
                iAmDirector = role == StageRole.Leader && connectivity == ConnectivityStatus.Connected;
            }

            if (!iAmDirector || announce.DirectorId == Id)
            {
                bool wasCandidate;
                StageRole previousRole;
                PerformerId? oldDirector;
                lock (electionLock)
                {
                    previousRole = role;
                    oldDirector = directorId;
                    directorId = announce.DirectorId;
                    wasCandidate = role == StageRole.Candidate;
                    // Si yo era Candidate del mismo term, abortamos: alguien ya gano.
                    if (wasCandidate)
                    {
                        role = StageRole.Follower;
                        currentElectionId = null;
                        votesReceived = null;
                    }
                }
                if (wasCandidate) electionRoundCts?.Cancel();
                lastSeenDirector[announce.DirectorId] = DateTime.UtcNow;
                OnDirectorChanged?.Invoke(announce.DirectorId);
                if (wasCandidate) RaiseRoleChanged(previousRole);
                // Bug 18 — same-term: si el Director cambio de identidad (rotacion),
                // re-cablear data channels in-band. El join inicial (oldDirector==null)
                // se excluye: ese pairing es out-of-band por contrato.
                RequestRehandshakeIfRotated(oldDirector, announce.DirectorId);
                return;
            }

            // Sub-caso 3b: ambos Directors con mismo term — tiebreaker bug-12.
            long myMax = hook.CurrentEntryId;
            long peerMax = announce.MaxEntryId;

            bool peerWins;
            if (peerMax != myMax)
                peerWins = peerMax > myMax;
            else
                peerWins = announce.DirectorId.CompareTo(Id) < 0;

            if (peerWins)
            {
                hook.Logger.Debug(
                    $"[Stage {Id}] Director conflict same-term: lost to {announce.DirectorId} " +
                    $"(myMax={myMax}, peerMax={peerMax}). Demote.");
                StageRole previousRole;
                lock (electionLock)
                {
                    previousRole = role;
                    directorId = announce.DirectorId;
                    role = StageRole.Follower;
                }
                lastSeenDirector[announce.DirectorId] = DateTime.UtcNow;
                heartbeatSenderCts?.Cancel();
                OnDirectorChanged?.Invoke(announce.DirectorId);
                RaiseRoleChanged(previousRole);
                var info = new SplitBrainDetected(
                    winner: announce.DirectorId,
                    myMaxEntryId: myMax,
                    winnerMaxEntryId: peerMax);
                OnDirectorElectionLost?.Invoke(info);
            }
            else
            {
                hook.Logger.Debug(
                    $"[Stage {Id}] Director conflict same-term: won over {announce.DirectorId} " +
                    $"(myMax={myMax}, peerMax={peerMax}). Re-asserting.");
                var rebut = new DirectorAnnounce(Id, Id, myMax, myTerm);
                var ch = channel;
                _ = Task.Run(async () =>
                {
                    try { await ch.SendAsync(rebut); }
                    catch { }
                });
            }
        }

        private async Task ListenReplication(PerformerId peerId, IStageChannel channel, CancellationToken ct)
        {
            try
            {
                await foreach (var msg in channel.Receive(ct))
                {
                    switch (msg)
                    {
                        // Phase 5 of the Action refactor: dropped the ActionDefinition
                        // case. Action definitions arrive as Define records inside a
                        // CueEvent like any other journal record (the receiver applies
                        // them via hook.ApplyReplicatedEvent → AddKnownActionFromDefine).

                        case CueEvent cue:
                            long localMax = hook.CurrentEntryId;
                            if (cue.EntryId == localMax + 1)
                            {
                                hook.WriteRawRecord(cue.JournalRecord, cue.EntryId);
                                hook.ApplyReplicatedEvent(cue.JournalRecord);

                                var cueEntry = entryBuffer.GetOrAdd(cue.EntryId, _ => new BufferedEntry());
                                cueEntry.Record = cue.JournalRecord;

                                await channel.SendAsync(new CueAck(Id, cue.EntryId), ct);
                            }
                            else if (cue.EntryId > localMax + 1)
                            {
                                hook.Logger.Debug($"[Stage {Id}] GAP: expected {localMax + 1}, got {cue.EntryId}");
                            }
                            else
                            {
                                // Bug 17 — silently-dropped por colision de EntryId. El Cast
                                // ya tiene algo en cue.EntryId, distinto de lo que el Director
                                // esta enviando. Path tipico: el Cast escribio localmente con
                                // PerformCmdLocal antes del join, ocupando EntryIds bajos que
                                // el seed del Director tambien quiere usar. La entry del Director
                                // se pierde. No throw para preservar el contrato del protocolo
                                // (catch-up debe ser tolerante a re-sends), pero logueamos cada
                                // drop e incrementamos un counter para que el host detecte que
                                // tiene un journal incoherente. Si replicationDroppedOlderCount
                                // > 0 post-pairing, el Cast NO recibio el seed completo.
                                System.Threading.Interlocked.Increment(ref replicationDroppedOlderCount);
                                hook.Logger.Debug(
                                    $"[Stage {Id}] DROP older CueEvent EntryId={cue.EntryId} (localMax={localMax} from peer={peerId}). " +
                                    $"Collision: este Stage ya tiene una entry en esa posicion. Total drops={replicationDroppedOlderCount}.");
                            }
                            break;

                        // Fase 5 — Playbill replication apply path.
                        // Si el Cast no tiene Playbill configurado, los cues se ignoran
                        // (Playbill es audit-off legitimo). Si lo tiene, se aplican
                        // idempotentemente: RegisterSchema es idempotente por contrato;
                        // WriteRecord lanza LanguageException en duplicado, que se traga.
                        case PlaybillSchemaCue schemaCue:
                            if (hook.Playbill != null)
                            {
                                try
                                {
                                    hook.Playbill.RegisterSchemaRaw(schemaCue.SchemaName, schemaCue.Declarations);
                                }
                                catch (LanguageException ex)
                                {
                                    hook.Logger.Error($"[Stage {Id}] PlaybillSchemaCue apply error for '{schemaCue.SchemaName}'", ex);
                                }
                            }
                            break;

                        case PlaybillCue recordCue:
                            if (hook.Playbill != null)
                            {
                                try
                                {
                                    hook.Playbill.WriteRecordRaw(recordCue.EntryId, recordCue.SchemaName, recordCue.SerializedParameters);
                                }
                                catch (LanguageException)
                                {
                                    // Duplicate EntryId (idempotent apply) o schema desconocido —
                                    // log debug y continuar para no bloquear la replicacion.
                                    hook.Logger.Debug($"[Stage {Id}] PlaybillCue apply skip for EntryId={recordCue.EntryId} schema='{recordCue.SchemaName}' (duplicate or unknown schema)");
                                }
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { hook.Logger.Error($"[Stage {Id}] ListenReplication error", ex); }
        }

        private async Task ListenCommand(PerformerId peerId, IStageChannel channel, CancellationToken ct)
        {
            try
            {
                await foreach (var msg in channel.Receive(ct))
                {
                    switch (msg)
                    {
                        case ForwardCommand fwd when IsDirector:
                            string result;
                            bool success;
                            try
                            {
                                if (fwd.CommandType == ForwardCommandType.CheckThenCommand)
                                {
                                    if (!string.IsNullOrEmpty(fwd.SerializedParameters))
                                    {
                                        var parameters = Parameters.DeserializeFromTransport(fwd.SerializedParameters);
                                        result = hook.PerformCheckThenCmd(fwd.CheckScript, fwd.Script, parameters, fwd.OccurredAt, fwd.Ip, fwd.User);
                                    }
                                    else
                                    {
                                        result = hook.PerformCheckThenCmd(fwd.CheckScript, fwd.Script, fwd.OccurredAt, fwd.Ip, fwd.User);
                                    }
                                }
                                else if (!string.IsNullOrEmpty(fwd.SerializedParameters))
                                {
                                    var parameters = Parameters.DeserializeFromTransport(fwd.SerializedParameters);
                                    result = hook.PerformCmd(fwd.Script, parameters, fwd.OccurredAt, fwd.Ip, fwd.User);
                                }
                                else
                                {
                                    result = hook.PerformCmd(fwd.Script, fwd.OccurredAt, fwd.Ip, fwd.User);
                                }
                                success = true;
                            }
                            catch (Exception ex)
                            {
                                result = ex.Message;
                                success = false;
                            }
                            await channel.SendAsync(new CommandResult(Id, fwd.CommandId, result, success), ct);
                            break;

                        case CommandResult cmdResult:
                            if (pendingCommands.TryGetValue(cmdResult.CommandId, out var tcs))
                            {
                                tcs.TrySetResult(cmdResult);
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { hook.Logger.Error($"[Stage {Id}] ListenCommand error", ex); }
        }

        // --- Playbill attachment (Fase 5 — cross-pod replication) ---
        //
        // Anuda un Playbill instance a este Stage. Despues de esto:
        //   - Como Director: cada RegisterSchema/WriteRecord sobre el Playbill
        //     dispara un PlaybillSchemaCue/PlaybillCue broadcast a las Casts.
        //   - Como Cast: los cues entrantes se aplican a este Playbill via
        //     RegisterSchemaRaw / WriteRecordRaw (ver ListenReplication).
        //
        // Ambos roles necesitan attach — el Director para emitir, el Cast para
        // recibir. Una vez attached, no se puede desatachar (no implementado;
        // dispose del Stage suficiente).
        //
        // Llamar despues de ConfigureStorage y antes de StartAsync para que
        // el Director-mode pueda emitir desde el primer momento.
        public void AttachPlaybill(Playbill playbill)
        {
            if (playbill == null) throw new ArgumentNullException(nameof(playbill));
            if (hook.Playbill != null && !ReferenceEquals(hook.Playbill, playbill))
                throw new InvalidOperationException("A different Playbill is already attached to this Stage.");

            // Idempotent re-attach: no duplicar suscripcion si es el mismo instance.
            if (hook.Playbill != null) return;

            hook.Playbill = playbill;
            playbill.OnSchemaRegistered += OnPlaybillSchemaRegistered;
            playbill.OnRecordWritten += OnPlaybillRecordWritten;
        }

        // --- Replication broadcast ---

        // Phase 5 of the Action refactor: dropped Stage.OnNewActionDefined.
        // Define records flow through OnRecordWritten like any other journal record.

        private void OnRecordWritten(long entryId, byte[] record)
        {
            var entry = entryBuffer.GetOrAdd(entryId, _ => new BufferedEntry());
            entry.Record = record;

            if (!IsDirector) return;

            foreach (var kvp in castLinks)
            {
                var link = kvp.Value;
                if (link.Replication != null && link.Replication.IsConnected)
                {
                    // Phase 5 of the Action refactor: dropped the ActionDefinition
                    // pre-broadcast. Define records are journal records and ride
                    // CueEvent like any other record (firmado: cross-stage atomicity
                    // is unnecessary because the director's journal already
                    // persisted the Define + Invocation pair transactionally —
                    // followers receive them as separate CueEvents and apply them
                    // in order).
                    //
                    // Enqueue rather than fire-and-forget Task.Run. The per-link
                    // worker drains the queue serially and awaits each SendAsync
                    // before pulling the next CueEvent, so the Cast sees records
                    // in the same order the writer thread invoked OnRecordWritten.
                    // The previous fire-and-forget shape let two sequential
                    // invocations (e.g. the Define + Invocation pair produced by
                    // a parametric PerformCmd) race to reach Channel.Writer
                    // .WriteAsync and a Cast could see entry N+1 before entry N,
                    // hitting the GAP path in ListenReplication and stalling
                    // replication on that link.
                    var cueEvent = new CueEvent(Id, entryId, record);
                    link.EnqueueReplication(cueEvent);
                }
            }
        }

        // Fase 5 — Playbill schema registration broadcast. Solo el Director
        // emite; los Casts ignoran el callback (su Playbill local registra
        // schemas via apply path en ListenReplication).
        private void OnPlaybillSchemaRegistered(string schemaName, string declarations)
        {
            if (!IsDirector) return;

            foreach (var kvp in castLinks)
            {
                var link = kvp.Value;
                if (link.Replication != null && link.Replication.IsConnected)
                {
                    var cue = new PlaybillSchemaCue(Id, schemaName, declarations);
                    link.EnqueueReplication(cue);
                }
            }
        }

        // Fase 5 — Playbill record write broadcast. Mismo patron que
        // OnRecordWritten del journal: enqueue al per-link worker para preservar
        // FIFO con respecto al journal record del mismo EntryId.
        private void OnPlaybillRecordWritten(long entryId, string schemaName, string serializedParameters)
        {
            if (!IsDirector) return;

            foreach (var kvp in castLinks)
            {
                var link = kvp.Value;
                if (link.Replication != null && link.Replication.IsConnected)
                {
                    var cue = new PlaybillCue(Id, entryId, schemaName, serializedParameters);
                    link.EnqueueReplication(cue);
                }
            }
        }

        // =====================================================================
        //  Casting election protocol fase (a) — Heartbeat sender + watchdog
        // =====================================================================

        // Bug 15 — dispara OnRoleChanged si el rol actual difiere de previousRole.
        // Se llama FUERA del electionLock y DESPUES de actualizar heartbeatSenderCts
        // / electionRoundCts / directorId, asi el subscriber ve estado consistente
        // (IsDirector, CurrentDirectorId) cuando inspecciona el Stage. Captura un
        // snapshot atomico del rol+directorId actuales bajo el lock para que dos
        // eventos disparados en rapida sucesion no se entrecrucen visiblemente.
        private void RaiseRoleChanged(StageRole previousRole)
        {
            StageRole currentSnapshot;
            PerformerId? directorSnapshot;
            lock (electionLock)
            {
                currentSnapshot = role;
                directorSnapshot = directorId;
            }
            if (currentSnapshot == previousRole) return;
            var handler = OnRoleChanged;
            if (handler == null) return;
            try { handler(currentSnapshot, directorSnapshot); }
            catch (Exception ex)
            {
                hook.Logger.Error($"[Stage {Id}] OnRoleChanged handler threw", ex);
            }
        }

        // Arranca/reinicia el sender loop. Llamado al final de PromoteToDirector.
        // Cancelar el CTS previo cubre el caso re-promote sin StepDown intermedio.
        private void StartHeartbeatSender()
        {
            heartbeatSenderCts?.Cancel();
            heartbeatSenderCts = new CancellationTokenSource();
            var ct = heartbeatSenderCts.Token;
            _ = Task.Run(async () => await HeartbeatSenderLoop(ct));
        }

        // Loop del Director: cada HeartbeatInterval envia un Heartbeat a cada peer
        // del bus de coordination con su CurrentEntryId como evidencia de actividad.
        // Sale silenciosamente si IsDirector se vuelve false (por StepDown, split-brain
        // loss, o aislamiento) o si el CTS se cancela.
        //
        // Bug 16 diagnostics: cada tick loguea (IsDirector, peers_connected, sent_now,
        // total) y la salida loguea su razon — el reporte 2026-05-27 PM3 muestra ~33s
        // de silencio del Director sin pistas para discriminar "loop muerto" de "loop
        // vivo emite pero el transport descarta". El contador heartbeatsEmittedCount
        // permite a tests asertar que el tick funciona aun cuando el transport no
        // confirme entrega.
        private async Task HeartbeatSenderLoop(CancellationToken ct)
        {
            heartbeatSenderRunning = true;
            string exitReason = "unknown";
            try
            {
                hook.Logger.Debug($"[Stage {Id}] HeartbeatSender loop start (interval={config.HeartbeatInterval}).");
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(config.HeartbeatInterval, ct);
                    if (!IsDirector) { exitReason = "IsDirector_false"; break; }
                    long maxNow = hook.CurrentEntryId;
                    int peersTotal = coordinationPeers.Count;
                    int peersSent = 0;
                    foreach (var kvp in coordinationPeers)
                    {
                        if (!kvp.Value.IsConnected) continue;
                        var ch = kvp.Value;
                        var hb = new Heartbeat(Id, maxNow);
                        _ = Task.Run(async () =>
                        {
                            try { await ch.SendAsync(hb); }
                            catch (Exception ex)
                            {
                                hook.Logger.Debug($"[Stage {Id}] Heartbeat send to peer threw {ex.GetType().Name}: {ex.Message}");
                            }
                        });
                        peersSent++;
                    }
                    long total = System.Threading.Interlocked.Increment(ref heartbeatsEmittedCount);
                    hook.Logger.Debug(
                        $"[Stage {Id}] HeartbeatSender tick #{total} (peers={peersSent}/{peersTotal}, maxEntryId={maxNow}).");
                }
                if (ct.IsCancellationRequested && exitReason == "unknown")
                    exitReason = "ct_cancelled";
            }
            catch (OperationCanceledException) { exitReason = "ct_cancelled_via_delay"; }
            catch (Exception ex)
            {
                exitReason = "exception_" + ex.GetType().Name;
                hook.Logger.Error($"[Stage {Id}] HeartbeatSender error", ex);
            }
            finally
            {
                heartbeatSenderRunning = false;
                hook.Logger.Debug(
                    $"[Stage {Id}] HeartbeatSender loop exit (reason={exitReason}, emitted={heartbeatsEmittedCount}).");
            }
        }

        // Arranca el watchdog del lado Cast. Lifecycle por Stage: se crea en StartAsync
        // y se cancela en StopAsync/DisposeAsync via backgroundTasks.
        private void StartDirectorWatchdog()
        {
            directorWatchdogCts = new CancellationTokenSource();
            var ct = directorWatchdogCts.Token;
            backgroundTasks.Add(directorWatchdogCts);
            _ = Task.Run(async () => await DirectorWatchdogLoop(ct));
        }

        // Loop del watchdog: cada HeartbeatInterval evalua si el Director registrado
        // dejo de enviar heartbeats. Solo cuenta si:
        //   1) Tenemos un directorId asignado.
        //   2) Ese directorId no soy yo (no me chequeo).
        //   3) Hay un lastSeen registrado (DirectorAnnounce o Heartbeat previos).
        // Si ahora-lastSeen > DirectorTimeout, limpia directorId, remueve el lastSeen
        // y dispara OnDirectorLost. EnsureCanWrite bloqueara escrituras a partir de
        // este punto hasta que algo reasigne Director.
        //
        // Etapa b: el handler de OnDirectorLost (o codigo equivalente aqui mismo)
        // arrancara una CastingPropose para auto-eleccion.
        private async Task DirectorWatchdogLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(config.HeartbeatInterval, ct);

                    if (!directorId.HasValue) continue;
                    if (directorId.Value == Id) continue;

                    var dirId = directorId.Value;
                    if (!lastSeenDirector.TryGetValue(dirId, out var last)) continue;

                    if (DateTime.UtcNow - last > config.DirectorTimeout)
                    {
                        hook.Logger.Debug($"[Stage {Id}] Director {dirId} timed out (last seen {last:O}). Clearing.");
                        lock (electionLock)
                        {
                            directorId = null;
                        }
                        lastSeenDirector.TryRemove(dirId, out _);
                        OnDirectorLost?.Invoke(dirId);

                        // Fase b: auto-iniciar eleccion. Si connectivity == Isolated
                        // o ya somos Candidate de otro round, StartElection es no-op.
                        StartElection();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { hook.Logger.Error($"[Stage {Id}] DirectorWatchdog error", ex); }
        }

        // =====================================================================
        //  Casting election protocol fase (b) — state machine + voter logic
        // =====================================================================

        // Arranca un round de eleccion como Candidate. Llamado desde DirectorWatchdog
        // tras Director-down. Idempotente con guards: no-op si ya somos Candidate o
        // Leader, o si estamos Isolated (sin peers no se puede elegir).
        //
        // Quorum: floor(N/2)+1 incluyendo self, donde N = peers + 1. Excepcion
        // declarada: si N<=2 (apps Stage 2-peer), 1 voto basta — weak quorum aprobado
        // 2026-05-27. Para N=1 (alone, sin peers) el guard de Isolated atrapa antes.
        private void StartElection()
        {
            if (connectivity == ConnectivityStatus.Isolated)
            {
                hook.Logger.Debug($"[Stage {Id}] StartElection: skipping (isolated).");
                return;
            }

            // Fase (d) refinement: el backoff retry no debe correr si ya conocemos
            // un Director (peer ganador ya anuncio mientras nosotros esperabamos
            // backoff, o nosotros mismos ganamos). Sin este guard, un retry tardio
            // bumpea term y demote injustamente al Director recien electo, generando
            // un loop entre Candidates atrasados que vuelven a hacer split-vote.
            // El watchdog sigue siendo la fuente unica de "Director caido"; el
            // backoff retry asume que esa decision ya fue tomada y solo continua
            // si sigue siendo cierta.
            if (directorId.HasValue)
            {
                hook.Logger.Debug($"[Stage {Id}] StartElection: skipping (director already known: {directorId.Value}).");
                return;
            }

            long newTerm;
            Guid electionId;
            CastingPropose propose;
            IStageChannel[] peerChannels;
            bool selfQuorum;
            StageRole previousRole;

            lock (electionLock)
            {
                if (role == StageRole.Candidate || role == StageRole.Leader)
                {
                    hook.Logger.Debug($"[Stage {Id}] StartElection: skipping (role={role}).");
                    return;
                }

                previousRole = role;

                newTerm = termStore.CurrentTerm + 1;
                termStore.BumpTerm(newTerm);   // resetea votedFor
                termStore.RecordVote(Id);       // self-vote

                electionId = Guid.NewGuid();
                currentElectionId = electionId;
                votesReceived = new HashSet<PerformerId> { Id };

                int totalPeers = coordinationPeers.Count + 1;
                neededVotes = totalPeers <= 2 ? 1 : totalPeers / 2 + 1;

                role = StageRole.Candidate;

                propose = new CastingPropose(Id, newTerm, hook.CurrentEntryId, electionId);
                peerChannels = new IStageChannel[coordinationPeers.Count];
                int i = 0;
                foreach (var kvp in coordinationPeers)
                    peerChannels[i++] = kvp.Value;

                selfQuorum = votesReceived.Count >= neededVotes;
            }

            hook.Logger.Debug($"[Stage {Id}] StartElection term={newTerm} electionId={electionId} neededVotes={neededVotes}.");

            // Si el quorum se cumple solo con self-vote (weak 2-peer con peer caido,
            // o N=1 pero connectivity Connected — no deberia pasar), promovemos ya.
            // No notificamos Candidate intermedio en esta rama: el evento Leader
            // que dispara BecomeDirector hace el role-change visible al subscriber
            // sin pasar por una transicion fantasma.
            if (selfQuorum)
            {
                BecomeDirector();
                return;
            }

            // Notificacion de transicion a Candidate (no hubo self-quorum). El host
            // raramente actua sobre Candidate, pero la simetria del API exige
            // dispararlo: el subscriber puede usarlo para UI (esperando eleccion).
            RaiseRoleChanged(previousRole);

            // Broadcast a todos los peers conectados. Fire-and-forget; los accepts
            // (o rejects con term superior) llegaran via ListenCoordination.
            foreach (var ch in peerChannels)
            {
                if (!ch.IsConnected) continue;
                var chCaptured = ch;
                _ = Task.Run(async () =>
                {
                    try { await chCaptured.SendAsync(propose); }
                    catch { }
                });
            }

            // Fase (d): arranca round-timer. Si tras CastingElectionTimeout
            // seguimos Candidate sin alcanzar quorum (split-vote, perdidas en la
            // red, etc.), OnElectionRoundTimeout aborta este round, espera un
            // backoff aleatorio y reintenta con term bumpeado.
            StartElectionRoundTimer(electionId);
        }

        // Arranca el timer del round actual. Cancela el CTS previo (si quedo
        // colgado por race con otro retry) y crea uno nuevo. Task ejecuta en
        // background, no bloquea StartElection.
        private void StartElectionRoundTimer(Guid forElectionId)
        {
            electionRoundCts?.Cancel();
            var roundCts = new CancellationTokenSource();
            electionRoundCts = roundCts;
            var ct = roundCts.Token;
            _ = Task.Run(async () => await ElectionRoundTimerLoop(forElectionId, ct));
        }

        private async Task ElectionRoundTimerLoop(Guid forElectionId, CancellationToken ct)
        {
            try { await Task.Delay(config.CastingElectionTimeout, ct); }
            catch (OperationCanceledException) { return; }
            OnElectionRoundTimeout(forElectionId);
        }

        // Llamado por el round timer (o por test seam) cuando el round expira.
        // Verifica que seguimos siendo Candidate de ESE round especifico antes
        // de actuar — si entre tanto recibimos DirectorAnnounce o adoptamos
        // term superior, currentElectionId apunta a otro round o es null, y
        // este timeout debe ser silenciosamente ignorado.
        private void OnElectionRoundTimeout(Guid forElectionId)
        {
            bool shouldRetry;
            StageRole previousRole = StageRole.Follower;
            lock (electionLock)
            {
                shouldRetry = role == StageRole.Candidate
                              && currentElectionId.HasValue
                              && currentElectionId.Value == forElectionId;
                if (shouldRetry)
                {
                    previousRole = role;
                    role = StageRole.Follower;
                    currentElectionId = null;
                    votesReceived = null;
                    electionRoundTimeoutCount++;
                }
            }
            if (!shouldRetry) return;
            RaiseRoleChanged(previousRole);

            // Backoff aleatorio uniforme en [0, CastingElectionTimeout/2].
            // El maximo asegura que dos candidatos con timeouts iguales tengan
            // una probabilidad alta de divergir tras 1-2 rounds. Mas grande
            // hace la convergencia mas lenta; mas chico aumenta el riesgo de
            // re-split. Half es el sweet spot que el plan original cito (Raft
            // canon).
            int maxBackoffMs = Math.Max(1, (int)(config.CastingElectionTimeout.TotalMilliseconds / 2));
            int backoffMs = Random.Shared.Next(0, maxBackoffMs + 1);
            hook.Logger.Debug(
                $"[Stage {Id}] Election round {forElectionId} timed out (term={termStore.CurrentTerm}). " +
                $"Backoff {backoffMs}ms then retry.");

            _ = Task.Run(async () =>
            {
                try { await Task.Delay(backoffMs); }
                catch { }
                // Retry. StartElection re-guard: si entre tanto otro round
                // arranco (term-adopt, force-promote), el role!=Follower y
                // skip; sino bumpea term y arranca round nuevo.
                StartElection();
            });
        }

        // Transicion Candidate → Leader: quorum alcanzado en el round actual.
        // Corre OnFirstHydration/OnHydrated (igual que PromoteToDirector), broadcastea
        // DirectorAnnounce con term ganador, arranca heartbeat sender. Despues de
        // esto, IsDirector == true y EnsureCanWrite permite escrituras.
        private void BecomeDirector()
        {
            if (hook.IsNew) OnFirstHydration();
            OnHydrated();

            // Round ganado — cancelar timer del round actual.
            electionRoundCts?.Cancel();

            long myTerm;
            long maxAtPromote;
            IStageChannel[] peerChannels;
            StageRole previousRole;

            lock (electionLock)
            {
                previousRole = role;
                role = StageRole.Leader;
                directorId = Id;
                currentElectionId = null;
                votesReceived = null;
                myTerm = termStore.CurrentTerm;
                maxAtPromote = hook.CurrentEntryId;
                peerChannels = new IStageChannel[coordinationPeers.Count];
                int i = 0;
                foreach (var kvp in coordinationPeers)
                    peerChannels[i++] = kvp.Value;
            }

            hook.Logger.Debug($"[Stage {Id}] BecomeDirector term={myTerm} maxEntryId={maxAtPromote}.");
            OnDirectorChanged?.Invoke(Id);
            RaiseRoleChanged(previousRole);

            foreach (var ch in peerChannels)
            {
                if (!ch.IsConnected) continue;
                var announce = new DirectorAnnounce(Id, Id, maxAtPromote, myTerm);
                var chCaptured = ch;
                _ = Task.Run(async () =>
                {
                    try { await chCaptured.SendAsync(announce); }
                    catch { }
                });
            }

            StartHeartbeatSender();
        }

        // Voter side. Reglas Raft con el ajuste term-first ya descrito:
        //   - propose.Term < myTerm           → reject(myTerm).
        //   - propose.Term > myTerm           → adopt term + reset vote + evaluar.
        //   - propose.Term == myTerm:
        //       - ya vote a otro candidato     → reject.
        //       - ya vote a este candidato     → re-accept idempotente.
        //       - sin voto previo:
        //           - proposer.EntryCount < mio → reject (Raft completeness).
        //           - else                       → accept + persist vote.
        private void HandleCastingPropose(CastingPropose propose, IStageChannel channel)
        {
            long myTerm = termStore.CurrentTerm;

            if (propose.Term < myTerm)
            {
                SendReject(channel, myTerm, propose.ElectionId);
                return;
            }

            if (propose.Term > myTerm)
            {
                termStore.AdoptTermIfHigher(propose.Term);
                StageRole previousRole;
                bool roleChanged;
                lock (electionLock)
                {
                    previousRole = role;
                    bool wasLeader = role == StageRole.Leader;
                    role = StageRole.Follower;
                    roleChanged = previousRole != StageRole.Follower;
                    currentElectionId = null;
                    votesReceived = null;
                    if (wasLeader)
                        // No limpiar directorId aun — el ganador del nuevo term enviara
                        // DirectorAnnounce y ahi se actualiza.
                        ;
                }
                if (heartbeatSenderCts != null) heartbeatSenderCts.Cancel();
                electionRoundCts?.Cancel();
                if (roleChanged) RaiseRoleChanged(previousRole);
            }

            // Tras posible adopt, propose.Term == termStore.CurrentTerm.
            var prevVote = termStore.VotedFor;
            if (prevVote.HasValue)
            {
                if (prevVote.Value == propose.SenderId)
                {
                    // Re-vote idempotente.
                    SendAccept(channel, termStore.CurrentTerm, propose.ElectionId);
                }
                else
                {
                    SendReject(channel, termStore.CurrentTerm, propose.ElectionId);
                }
                return;
            }

            if (propose.ProposerEntryCount < hook.CurrentEntryId)
            {
                SendReject(channel, termStore.CurrentTerm, propose.ElectionId);
                return;
            }

            termStore.RecordVote(propose.SenderId);
            hook.Logger.Debug(
                $"[Stage {Id}] Voted for {propose.SenderId} in term {termStore.CurrentTerm} " +
                $"(proposerEntries={propose.ProposerEntryCount}, mine={hook.CurrentEntryId}).");
            SendAccept(channel, termStore.CurrentTerm, propose.ElectionId);
        }

        // Candidate side, recibe un accept. Si el round/term coinciden con el
        // actual, registra el voto; si llega a quorum, transiciona a Leader.
        private void HandleCastingAccept(CastingAccept accept)
        {
            bool reachedQuorum = false;
            lock (electionLock)
            {
                if (role != StageRole.Candidate) return;
                if (accept.Term != termStore.CurrentTerm) return;
                if (!currentElectionId.HasValue || currentElectionId.Value != accept.ElectionId) return;

                votesReceived.Add(accept.SenderId);
                hook.Logger.Debug(
                    $"[Stage {Id}] CastingAccept from {accept.SenderId} " +
                    $"(term={accept.Term}, votes={votesReceived.Count}/{neededVotes}).");

                if (votesReceived.Count >= neededVotes)
                    reachedQuorum = true;
            }

            if (reachedQuorum) BecomeDirector();
        }

        // Recibe un reject. Si el voter aprende que tenemos un term inferior
        // (reject.Term > myTerm), adoptamos el term y pasamos a Follower (step
        // down silencioso). Si term igual, fase d agregara retry con backoff;
        // por ahora ignoramos el voto negativo (Candidate sigue esperando quorum
        // o un term-update via reject/announce posterior).
        private void HandleCastingReject(CastingReject reject)
        {
            if (reject.Term <= termStore.CurrentTerm) return;

            hook.Logger.Debug(
                $"[Stage {Id}] CastingReject term-upgrade {termStore.CurrentTerm} → {reject.Term} " +
                $"from {reject.SenderId}. Step down to Follower.");
            termStore.AdoptTermIfHigher(reject.Term);

            bool wasLeader;
            StageRole previousRole;
            bool roleChanged;
            lock (electionLock)
            {
                previousRole = role;
                wasLeader = role == StageRole.Leader;
                role = StageRole.Follower;
                roleChanged = previousRole != StageRole.Follower;
                currentElectionId = null;
                votesReceived = null;
            }
            if (wasLeader) heartbeatSenderCts?.Cancel();
            electionRoundCts?.Cancel();
            if (roleChanged) RaiseRoleChanged(previousRole);
        }

        private void SendAccept(IStageChannel channel, long term, Guid electionId)
        {
            var msg = new CastingAccept(Id, term, electionId);
            var ch = channel;
            _ = Task.Run(async () => { try { await ch.SendAsync(msg); } catch { } });
        }

        private void SendReject(IStageChannel channel, long term, Guid electionId)
        {
            var msg = new CastingReject(Id, term, electionId);
            var ch = channel;
            _ = Task.Run(async () => { try { await ch.SendAsync(msg); } catch { } });
        }

        // =====================================================================
        //  Bug 18 — Failover replication gap: re-handshake in-band de data channels
        //
        //  Tras una rotacion de roles el bus de Coordination sobrevive pero los
        //  data channels (Replication/Command) quedan muertos: el nuevo Director no
        //  tiene CastLink hacia el nuevo Cast y el nuevo Cast no tiene DirectorLink
        //  hacia el nuevo Director. En produccion cada Stage corre en un proceso
        //  distinto, asi que el host NO puede repetir el pairing out-of-band (no
        //  tiene como cruzar las nuevas ConnectionInvitation entre procesos).
        //
        //  La reconexion viaja in-band sobre el unico canal vivo (Coordination):
        //  el Cast pide (RehandshakeRequest con su lastEntryId), el Director crea
        //  las invitaciones y responde con sus Address (RehandshakeProposal), el
        //  Cast las acepta y conecta. El Director cierra con un catch-up desde el
        //  lastEntryId del Cast — asi la entry que el nuevo Director escriba durante
        //  la ventana de reconexion no se pierde.
        // =====================================================================

        // Disparado por el Cast cuando adopta un Director. Solo pide re-handshake si
        // esto es una ROTACION: ya conociamos un Director distinto (o eramos nosotros
        // el Director). El primer aprendizaje (oldDirector == null) es el join inicial
        // y se cablea out-of-band por contrato de pairing — no auto-rehandshake ahi,
        // para no duplicar el ConnectToDirector que el host hara. Tampoco si el nuevo
        // Director soy yo (soy Director, no Cast).
        private void RequestRehandshakeIfRotated(PerformerId? oldDirector, PerformerId newDirector)
        {
            if (newDirector == Id) return;
            if (!oldDirector.HasValue) return;
            if (oldDirector.Value == newDirector) return;

            if (!coordinationPeers.TryGetValue(newDirector, out var coordChannel) || !coordChannel.IsConnected)
            {
                hook.Logger.Debug(
                    $"[Stage {Id}] Rehandshake skip: no live coordination channel to new Director {newDirector}.");
                return;
            }

            long myLast = hook.CurrentEntryId;
            hook.Logger.Debug(
                $"[Stage {Id}] Director rotated {oldDirector.Value} → {newDirector}. " +
                $"Requesting in-band re-handshake (myLastEntryId={myLast}).");
            var req = new RehandshakeRequest(Id, myLast);
            _ = Task.Run(async () => { try { await coordChannel.SendAsync(req); } catch { } });
        }

        // Director side: el Cast pide reconexion. Creo invitaciones Replication+Command,
        // respondo con sus Address sobre el mismo canal de Coordination, espero a que
        // el Cast las acepte, hago AcceptCastConnection y un catch-up desde el
        // lastEntryId del Cast. Todo en un Task aparte: NO bloquear el listener de
        // Coordination (heartbeats, announces, votos siguen fluyendo).
        private void HandleRehandshakeRequest(RehandshakeRequest req, IStageChannel channel)
        {
            if (!IsDirector)
            {
                hook.Logger.Debug(
                    $"[Stage {Id}] Ignoring RehandshakeRequest from {req.SenderId}: not Director.");
                return;
            }

            var castId = req.SenderId;
            long castLast = req.LastKnownEntryId;
            var ch = channel;
            _ = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(config.CommandForwardingTimeout);
                try
                {
                    var repInv = await CreateInvitationAsync(ChannelPurpose.Replication);
                    var cmdInv = await CreateInvitationAsync(ChannelPurpose.Command);
                    var waitRep = WaitForConnectionAsync(repInv, cts.Token);
                    var waitCmd = WaitForConnectionAsync(cmdInv, cts.Token);

                    var proposal = new RehandshakeProposal(Id, repInv.Address, cmdInv.Address);
                    await ch.SendAsync(proposal, cts.Token);

                    var dirRep = await waitRep;
                    var dirCmd = await waitCmd;
                    await AcceptCastConnection(castId, dirRep, dirCmd);

                    // Catch-up desde el lastEntryId del Cast. Cubre las entries escritas
                    // por este Director entre la rotacion y el cierre del re-handshake.
                    await SendCatchUpAsync(castId, castLast, cts.Token);

                    hook.Logger.Debug(
                        $"[Stage {Id}] Re-handshake (director side) with {castId} complete (catch-up from {castLast}).");
                }
                catch (Exception ex)
                {
                    hook.Logger.Error($"[Stage {Id}] Re-handshake (director side) for {castId} failed", ex);
                }
            });
        }

        // Cast side: el Director respondio con las Address de las nuevas invitaciones.
        // Las acepto y conecto. ConnectionInvitation es reconstruible por completo
        // desde (InviterId, Purpose, Address) — InviterId es el SenderId del proposal.
        private void HandleRehandshakeProposal(RehandshakeProposal proposal)
        {
            var dir = proposal.SenderId;
            if (!directorId.HasValue || directorId.Value != dir)
            {
                hook.Logger.Debug(
                    $"[Stage {Id}] Ignoring RehandshakeProposal from {dir}: not my current Director.");
                return;
            }

            _ = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(config.CommandForwardingTimeout);
                try
                {
                    var repInv = new ConnectionInvitation(dir, ChannelPurpose.Replication, proposal.ReplicationAddress);
                    var cmdInv = new ConnectionInvitation(dir, ChannelPurpose.Command, proposal.CommandAddress);
                    var castRep = await AcceptInvitationAsync(repInv);
                    var castCmd = await AcceptInvitationAsync(cmdInv);
                    await ConnectToDirector(dir, castRep, castCmd, cts.Token);

                    hook.Logger.Debug(
                        $"[Stage {Id}] Re-handshake (cast side) with Director {dir} complete.");
                }
                catch (Exception ex)
                {
                    hook.Logger.Error($"[Stage {Id}] Re-handshake (cast side) from {dir} failed", ex);
                }
            });
        }

        // =====================================================================
        //  Internal test seams (InternalsVisibleTo UnitTestChoreography)
        //
        //  Estos miembros existen para que los tests del Casting election protocol
        //  puedan (1) acortar los timeouts a escala de ms, (2) leer el estado
        //  observable del watchdog, y (3) simular "Director silent sin StepDown"
        //  cancelando el sender sin tocar coordination channels.
        // =====================================================================

        internal StageConfiguration ConfigurationForTesting => config;

        internal DateTime? LastSeenDirectorForTesting(PerformerId id)
            => lastSeenDirector.TryGetValue(id, out var ts) ? ts : (DateTime?)null;

        internal void StopHeartbeatSenderForTesting()
            => heartbeatSenderCts?.Cancel();

        // Bug 16 diagnostics — los tests aserta que el loop esta vivo y emitiendo
        // sin depender del transport (que en device InMemory != PortableHttps).
        internal long HeartbeatsEmittedCountForTesting => System.Threading.Interlocked.Read(ref heartbeatsEmittedCount);
        internal bool HeartbeatSenderRunningForTesting => heartbeatSenderRunning;

        // Bug 17 diagnostics — counter de CueEvent silently-dropped por colision.
        // Tests asertan que el counter sube cuando hay colision pre-existente, y
        // se queda en 0 en el path feliz.
        internal long ReplicationDroppedOlderCountForTesting => System.Threading.Interlocked.Read(ref replicationDroppedOlderCount);

        // Snapshot del state machine de eleccion para tests E2E.
        internal long CurrentTermForTesting => termStore?.CurrentTerm ?? 0L;
        internal PerformerId? VotedForForTesting => termStore?.VotedFor;
        internal string RoleForTesting { get { lock (electionLock) return role.ToString(); } }
        // Fase (d) — instrumentacion para tests del backoff path.
        internal int ElectionRoundTimeoutCountForTesting => electionRoundTimeoutCount;
        // Disparar eleccion explicitamente desde tests sin pasar por watchdog/timeout.
        // El guard de StartElection (isolated, role!=Follower) sigue activo, asi que
        // dos llamadas paralelas con el mismo stage no producen 2 elecciones.
        internal void StartElectionForTesting() => StartElection();

        public async ValueTask DisposeAsync()
        {
            hook.GracefulExit();
            isRunning = false;
            heartbeatSenderCts?.Cancel();
            electionRoundCts?.Cancel();
            foreach (var cts in backgroundTasks)
            {
                cts.Cancel();
                cts.Dispose();
            }
            // Drain and dispose each cast link's replication-sender worker so
            // its queued sends complete (or cancel) before the Stage goes away.
            foreach (var kvp in castLinks)
            {
                try { await kvp.Value.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
            castLinks.Clear();
            // Release transport-owned resources. An HTTPS transport holds a TLS
            // listener bound to a TCP port; without disposing it here the port
            // stays bound after the Stage is torn down, so a Stage recreated on
            // the same port (the Reset & Join flow) fails to bind with "address
            // already in use". InMemoryTransport and SimplexTransport do not
            // implement IAsyncDisposable, so this is a no-op for them.
            if (transport is IAsyncDisposable disposableTransport)
            {
                try { await disposableTransport.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }

        private class DirectorLink
        {
            public IStageChannel Replication;
            public IStageChannel Command;
        }

        // Per-link replication outbox. A single worker drains a FIFO queue and
        // awaits each SendAsync before pulling the next message. This is the
        // structural guarantee that — for a given Cast — records arrive in the
        // same order in which the writer thread invoked OnRecordWritten /
        // OnPlaybillRecordWritten / OnPlaybillSchemaRegistered.
        //
        // Why a queue rather than a synchronous block in OnRecordWritten: the
        // writer thread of the Diary (FileSystem or otherwise) is a critical
        // path. Awaiting a network/transport send inline would couple journal
        // throughput to replication latency. The queue decouples them while
        // still serializing per-link sends.
        //
        // Why per-link rather than a single Stage-wide queue: a slow Cast link
        // would head-of-line block other Casts. Each link has its own queue
        // and worker, so a stall on one Cast does not affect the others.
        //
        // Fase 5 — generalizado a StageMessage para permitir tambien Playbill
        // cues (PlaybillSchemaCue / PlaybillCue) por el mismo canal y con la
        // misma garantia de FIFO. La ordenacion journal-vs-playbill se preserva
        // dentro del link: el writer thread del Director invoca primero el
        // callback del journal y luego el callback del playbill (orden de
        // Performance.PerformCommand), asi que los cues entran al queue en ese
        // orden.
        private class CastLink : IAsyncDisposable
        {
            public IStageChannel Replication;
            public IStageChannel Command;

            // Set when the sender worker starts; only used for diagnostic logs.
            private PerformerId stageId;
            private IPuppeteerLogger logger;

            private readonly Channel<StageMessage> outboundQueue = Channel.CreateUnbounded<StageMessage>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });
            private CancellationTokenSource workerCts;
            private Task workerTask;

            public void StartReplicationSender(PerformerId stageId, IPuppeteerLogger logger)
            {
                this.stageId = stageId;
                this.logger = logger;
                this.workerCts = new CancellationTokenSource();
                var token = this.workerCts.Token;
                this.workerTask = Task.Run(() => PumpAsync(token));
            }

            // Non-blocking enqueue for the writer thread. Returns false only if
            // the queue has been completed (the link is shutting down). Acepta
            // cualquier StageMessage (CueEvent, PlaybillSchemaCue, PlaybillCue).
            public bool EnqueueReplication(StageMessage msg) => outboundQueue.Writer.TryWrite(msg);

            // Async enqueue for callers that want backpressure-friendly
            // semantics — e.g. catch-up loops that should respect a
            // CancellationToken. For an unbounded channel this completes
            // synchronously in practice.
            public ValueTask EnqueueReplicationAsync(StageMessage msg, CancellationToken ct)
                => outboundQueue.Writer.WriteAsync(msg, ct);

            private async Task PumpAsync(CancellationToken ct)
            {
                try
                {
                    await foreach (var msg in outboundQueue.Reader.ReadAllAsync(ct))
                    {
                        var channel = Replication;
                        if (channel == null || !channel.IsConnected) continue;
                        try
                        {
                            await channel.SendAsync(msg, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            logger.Error($"[Stage {stageId}] Error sending {msg.MessageType}", ex);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.Error($"[Stage {stageId}] Replication sender worker exited", ex);
                }
            }

            public async ValueTask DisposeAsync()
            {
                outboundQueue.Writer.TryComplete();
                if (workerCts != null)
                {
                    try { workerCts.Cancel(); } catch { }
                }
                if (workerTask != null)
                {
                    try { await workerTask.ConfigureAwait(false); }
                    catch { }
                }
                workerCts?.Dispose();
            }
        }

        private class BufferedEntry
        {
            // Phase 5 of the Action refactor: dropped the ActionDef field.
            // Replication of action definitions now flows through Record like any
            // other journal entry (the byte[] is a Define record decoded on the
            // follower side via hook.ApplyReplicatedEvent).
            public byte[] Record;
        }
    }
}
