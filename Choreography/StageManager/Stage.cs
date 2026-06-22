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

    // Bug 15 — Casting election channel rewire (Option B):
    // an external host needs to observe the role rotation to re-establish
    // Replication/Command channels. Stage does not handle the handshake
    // automatically — it only exposes OnRoleChanged and the host decides what to
    // do. Public enum so the subscriber can discriminate Leader/Candidate/Follower
    // without reflecting the internal state machine.
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

        // Casting election protocol phase (a) — Heartbeat loop.
        // lastSeenDirector: timestamp of the last Heartbeat (or DirectorAnnounce)
        // received from the current Director. The DirectorWatchdog reads it to decide
        // whether the Director stopped emitting and directorId must be cleared. Only
        // populated for the peer this Stage considers Director — heartbeats from other
        // peers are ignored.
        private readonly ConcurrentDictionary<PerformerId, DateTime> lastSeenDirector = new();
        // heartbeatSenderCts: lifecycle per Director-session (created in PromoteToDirector,
        // cancelled in StepDownAsync and in the loser branch of HandleDirectorAnnounce). NOT
        // added to backgroundTasks because its lifetime does not match the Stage's.
        private CancellationTokenSource heartbeatSenderCts;
        // Bug 16 — Diagnostics for the HeartbeatSenderLoop. heartbeatsEmitted counts each
        // Heartbeat dispatched (not per-peer; counts the successful tick after IsDirector).
        // heartbeatSenderRunning reflects whether the loop is alive (true between
        // StartHeartbeatSender and the loop exit). Both are volatile so tests/host can read
        // them without a lock. They serve to diagnose the symptom where heartbeats are
        // observed shortly after CatchUp and then fall silent: without counters there is no
        // way to distinguish "sender dead" from "sender alive but send fails silently".
        private long heartbeatsEmittedCount;
        private volatile bool heartbeatSenderRunning;

        // Bug 17 — Diagnostics for the ListenReplication replication path. Counts how
        // many CueEvent were silently dropped due to an EntryId collision (case
        // cue.EntryId <= localMax: the Cast already had an entry at that position,
        // probably because it wrote locally before joining). The symptom can be
        // attributed to "catch-up vs rehydration asymmetry", but the code shows it more
        // bluntly: the Director sends entry N and the Cast discards it because there is
        // already something different at N. When the host writes locally before the
        // join, it occupies a low EntryId, which produces the collision with the
        // Director's seed.
        private long replicationDroppedOlderCount;
        // directorWatchdogCts: lifecycle per Stage-session (created in StartAsync,
        // cancelled in StopAsync via backgroundTasks).
        private CancellationTokenSource directorWatchdogCts;

        // Casting election protocol phase (b) — state machine + persistence.
        // Role: Follower (default) / Candidate (election in progress, vote-for-self
        // emitted, awaiting accepts) / Leader (this Stage is Director with quorum).
        // The legacy directorId field is still the source-of-truth for
        // "who is the Director of the cluster" from this peer's point of view;
        // role adds the dimension "what am I doing inside the protocol".
        // The internal state machine reuses the public StageRole enum — it used to be
        // a nested private Role, but the OnRoleChanged event signature needs a public
        // type and keeping two parallel enums was noise. The semantics
        // (Follower default, Candidate during election, Leader with quorum) are
        // the same.
        private StageRole role = StageRole.Follower;
        // TermStore is built in StartAsync (it needs StageStateDirectory created).
        private TermStore termStore;
        // KnownPeersStore (bug 19): persisted membership of Coordination peers, so
        // the host can reopen Coordination after a process-death regardless of the
        // node's previous role. Built in StartAsync along with TermStore.
        private KnownPeersStore knownPeersStore;
        // Election state. currentElectionId different from null indicates role==Candidate.
        // votesReceived includes the self-vote from the moment of StartElection.
        // neededVotes captures the quorum at the start of the round (do not recalculate: if
        // peers join/leave mid-election, the current round continues with the original quorum).
        private Guid? currentElectionId;
        private HashSet<PerformerId> votesReceived;
        private int neededVotes;
        // Protects role, currentElectionId, votesReceived, directorId of the state
        // machine. ListenCoordination runs in one task per peer — without the lock,
        // a concurrent CastingAccept and DirectorAnnounce could leave the
        // state inconsistent. termStore has its own internal lock; it is not
        // nested (calls to termStore from inside electionLock are ok).
        private readonly object electionLock = new object();
        // Casting election protocol phase (d) — Randomized backoff.
        // electionRoundCts: lifecycle per election-round. Created in each
        // StartElection (after the broadcast) to trigger the ElectionRoundTimerLoop.
        // Cancelled when the round ends (BecomeDirector, term-adopt in any of the
        // handlers, StepDown, shutdown).
        private CancellationTokenSource electionRoundCts;
        // Observable counter of how many times the round timer fired abort+backoff+retry.
        // Useful for tests that validate that the backoff path was exercised.
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

        // Bug 12 — Casting election (split-brain recovery): when this Stage,
        // while Director, receives a DirectorAnnounce from another peer that also
        // declares itself Director (typical situation after a force-promote during
        // a partition), a deterministic tiebreaker by (MaxEntryId desc, PerformerId
        // asc) decides the winner. If this Stage loses, it demotes silently
        // and fires this event with the divergence detail so the
        // application can reconcile journals (SendCatchUpAsync, operator alert,
        // discard data dir, etc.). If HasDivergentTail==true the
        // loser has its own entries the winner never saw — those would be
        // lost in a rehydration-from-winner (there is no truncate primitive
        // in the journal layer); the decision is left to the application.
        public event Action<SplitBrainDetected> OnDirectorElectionLost;

        // Casting election protocol phase (a) — Director-down detection.
        // The DirectorWatchdog fires this event when no heartbeats are received from the
        // current Director within DirectorTimeout. After it is invoked, directorId
        // is left at null and EnsureCanWrite blocks writes until something reassigns
        // Director (operator via PromoteToDirector, or phase b: automatic election).
        // The argument is the Id of the Director that was lost.
        public event Action<PerformerId> OnDirectorLost;

        // Bug 15 — Casting election channel rewire (Option B). Fired every time
        // the Casting election state machine changes role (Follower ↔
        // Candidate ↔ Leader), AFTER updating role/directorId/heartbeat
        // sender, OUTSIDE the electionLock. The subscriber receives (newRole, directorId)
        // and can react — typically to open/close Replication/Command
        // channels toward the peers on the Coordination bus.
        //
        // - Leader: the host should iterate coordinationPeers and AcceptCastConnection
        //   against each one (the other end must be ready to receive the
        //   handshake).
        // - Follower with directorId!=null: the host should do
        //   ConnectToDirector against that directorId.
        // - Follower with directorId==null or Candidate: usually a no-op
        //   (intermediate transition; another event will arrive when convergence
        //   completes).
        //
        // Fired only when newRole != previousRole (directorId changes
        // without a role change still go through OnDirectorChanged). The subscriber
        // runs on the thread that fired the transition — it may be a
        // Coordination bus task (HandleDirectorAnnounce, HandleCastingPropose,
        // BecomeDirector via HandleCastingAccept) or the watchdog thread
        // (StartElection -> selfQuorum -> BecomeDirector). Do not block the handler:
        // if the handshake is asynchronous, launch it with Task.Run.
        public event Action<StageRole, PerformerId?> OnRoleChanged;

        protected abstract Actor CreateActor(string actorName);

        // Legacy hook: runs once when the actor is brand-new (empty journal
        // at the moment of promoting to Director). Kept as a backward-compat crutch
        // for Stages whose seed is coupled to the ItsANewOne path. New code prefers
        // OnHydrated() with a PerformCmd that contains 'upgrade('init') { ... }'.
        protected virtual void OnFirstHydration() { }

        // New hook: runs every time this Stage promotes to Director, BEFORE
        // marking IsDirector=true and before accepting PerformCmds (local or forwarded).
        // Intended to invoke hook.PerformCmd with a script that contains a sequence
        // of 'upgrade('X') { ... }' — the already-applied ones are skipped silently, the
        // new ones are journaled locally and then replicated to the Casts via catch-up
        // after the DirectorAnnounce.
        //
        // Never invoked on a Cast: upgrades reach the Cast through replication from the
        // Director, not through its own hydration. If your subclass needs to react when
        // synchronized as a Cast, that is a different hook (not implemented yet).
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

        // Logger seam (fluent, applies to V1 and V2): the sink is per-actor. This
        // facade propagates the impl injected by the host (Serilog, Microsoft.Extensions
        // .Logging, NLog, etc.) to the Actor that lives under this Stage. Without injection,
        // Puppeteer uses a default ConsoleLogger (Error -> stderr, Debug -> stdout).
        // V1/V2 use a `new` shadow to preserve the concrete type in the chain.
        //
        // Ordering: .Logger() MUST come before ConfigureTransport. The transport
        // receives the sink via ctor and is fixed at that instant; changing the logger
        // afterwards would not propagate to the already-built transport. The early throw
        // makes the contract visible instead of failing silently.
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

            // The transport receives the logger via ctor (snapshot of the sink configured
            // by the host via .Logger(x) or the Actor's default ConsoleLogger). Once
            // built, the transport is fixed to the chosen sink; changing
            // .Logger() afterwards does not propagate (see F9: ordering rule).
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

        // SimpleX transport loads managed crypto/TLS but the target app is mobile (Android/iOS).
        // On Windows desktop (dev machine) we fall back to InMemoryTransport so that
        // tests and the app can run without an emulator. For the real path from Windows, use
        // an Android emulator or WSL Linux. See DEVELOPMENT-WINDOWS.md.
        //
        // serverFingerprint (TOFU): SHA-256 of the SMP server's idCert. The Stage knows it
        // a-priori because the Usher (a separate component that builds invitations via QR)
        // emits URIs smp://HASH@host:port/... with the hash included. For the creator it
        // comes from config; for joiner-only it can be null and SimplexTransport extracts it
        // from the URI on accept.
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

            // Casting election phase (b): persistence of term/votedFor. Load before
            // hook so that any election triggered during hydration
            // (should not happen, but defensive) sees the correct term.
            termStore = new TermStore(config.StageStateDirectory);
            knownPeersStore = new KnownPeersStore(config.StageStateDirectory);

            hook.InitializeStorage(dbType, storageConnectionString);
            hook.OnRecordWritten = OnRecordWritten;
            // ReactionActivation gate: the live role of the P2P Stage is its
            // IsDirector (changes with the election). DirectorOnly runs only on the
            // director; CastOnly only on the Casts; Company on both.
            hook.SetActingAsDirectorProvider(() => IsDirector);
            // Phase 5 of the Action refactor: dropped hook.OnNewActionDefined wiring.
            // Define entries are journal records and replicate via OnRecordWritten →
            // CueEvent like any other record (signed: cross-stage atomicity is
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

        // Bug 19 — Known Coordination peers, persisted across process-death.
        //
        // Available immediately after StartAsync (reads from StageStateDirectory/peers.bin),
        // BEFORE any live Coordination channel exists. The host queries it on
        // startup to reopen Coordination with each known peer — regardless of
        // the node's previous role. That closes the case of the ex-Director that dies and
        // returns: its KnownPeersStore remembers the new Director (which used to be its Cast),
        // the host reopens Coordination, the DirectorAnnounce arrives and the term-first path
        // + (if there is an active rotation) the in-band re-handshake of bug 18 complete the
        // reconciliation.
        //
        // The Stage only contributes WHO to reconnect; the reconnection Address values are
        // owned by the host (invitation-based model: the Stage never sees them in
        // JoinCoordination).
        public IReadOnlyList<PerformerId> RecallKnownPeers()
            => knownPeersStore?.All ?? Array.Empty<PerformerId>();

        public async Task JoinCoordination(PerformerId peerId, IStageChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));

            coordinationPeers[peerId] = channel;
            // Bug 19: remember the membership so the host can reopen Coordination
            // with this peer after a process-death (see KnownPeersStore / RecallKnownPeers).
            // Idempotent: only touches disk the first time the peer is known.
            knownPeersStore?.Remember(peerId);
            RefreshConnectivity();

            var cts = new CancellationTokenSource();
            backgroundTasks.Add(cts);
            _ = Task.Run(async () => await ListenCoordination(peerId, channel, cts.Token), cts.Token);

            if (IsDirector)
            {
                // Fire-and-forget: the announce goes out now. The Coordination channel
                // is buffered (unbounded), so even if the Cast's listener has not
                // started yet the message stays queued and is processed when it
                // starts. We removed the Task.Delay(50) that was defensive without a
                // documented reason — with WaitForDirectorAsync in ConnectToDirector the
                // consumer race is already closed by the API contract, not by
                // timing. Removing the delay also speeds up the Cast join by
                // ~50ms in the host case (Director already active when the Cast appears).
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

            // Closes the race with EnsureCanWrite ONLY if the Cast has a command
            // channel (intent to forward PerformCmd to the Director). Without a
            // commandChannel the Cast is replication-only — it does not write, does not need
            // directorId assigned, and forcing the wait would block setups that
            // do not use the Coordination bus (e.g. HttpsTransportTests uses the data
            // star directly without coordination membership).
            //
            // With commandChannel: the caller will declare `cast.PerformCmd(...)` afterwards,
            // which enters EnsureCanWrite and requires directorId.HasValue. Waiting for the
            // DirectorAnnounce on the Coordination bus closes the race documented in
            // PromoteToDirector (line ~430) and worked around on the host side with
            // Task.Delay(5s). Pre-requisite: JoinCoordination must have been
            // invoked before — without coordination membership, this await blocks until
            // the CT cancels (caller's policy).
            if (commandChannel != null)
                await WaitForDirectorAsync(ct);
        }

        // Blocking wait until the local state machine registers an active Director
        // (this.directorId has a value). The registration happens when:
        //   - a DirectorAnnounce arrives via the Coordination bus (Cast), or
        //   - this Stage promotes itself (PromoteToDirector).
        //
        // Subscribes to the OnDirectorChanged event BEFORE the second check of
        // directorId to avoid a lost-wakeup: if the announce arrives between the
        // subscription and the check, the TaskCompletionSource captures the transition;
        // if it arrived before the subscription, the post-subscribe check detects it.
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

            // Director hydration hooks: run BEFORE marking IsDirector=true.
            // While they run, the hook's writes enter the local journal but are NOT
            // broadcast (OnRecordWritten checks IsDirector). Local PerformCmds
            // that fall in this window fail in EnsureCanWrite with "No Director
            // available" — the app will retry. The Casts (if any) will receive the
            // upgrade entries in the catch-up after the DirectorAnnounce.
            if (hook.IsNew) OnFirstHydration();
            OnHydrated();

            hook.Logger.Debug($"[Stage {Id}] Promoting self to Director (force={force})");
            StageRole previousRole;
            lock (electionLock)
            {
                previousRole = role;
                directorId = Id;
                role = StageRole.Leader;
                // If we came from Candidate in another election, we abort that round.
                currentElectionId = null;
                votesReceived = null;
            }
            OnDirectorChanged?.Invoke(Id);
            RaiseRoleChanged(previousRole);

            // PromoteToDirector(force:true) does NOT bump the term (in the end-user Stage
            // environment the protocol is the source-of-truth; force-promote
            // stays provisional until the cluster reconverges). If another side of the
            // partition elected legitimately with a higher term, its DirectorAnnounce will
            // demote us silently in HandleDirectorAnnounce term-first.
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

            // Phase 5 — Playbill catch-up: first send all schemas
            // (idempotent on the Cast) and then all records with EntryId >
            // peerLastEntryId. This must come BEFORE the journal catch-up so
            // that when the Cast applies a CueEvent with an EntryId that also
            // has a playbill record, the schema is already registered. Separate
            // tracking of the last-applied playbill EntryId does not exist — we use the
            // same peerLastEntryId (assuming journal and playbill advance
            // together by construction of Performance.PerformCommand).
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
                            // Only records liveness evidence for the CURRENT Director. Heartbeats
                            // from other peers (non-Director, or from an ex-Director whose role
                            // we already lost) are ignored. The watchdog reads lastSeenDirector.
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

                        // Bug 18 — in-band re-handshake of the data channels after
                        // a role rotation. The Cast asks (Request) and the Director
                        // responds with the Address values of the new invitations (Proposal).
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
        // Three rules in order:
        //
        // 1) announce.Term < myTerm:
        //    Stale announcement (peer Director of an old term). Ignored
        //    silently. We re-announce nothing — the peer will learn it is
        //    behind in the next propose round or via some subsequent message.
        //
        // 2) announce.Term > myTerm:
        //    The peer Director is legitimately more recent. We adopt the higher
        //    term (reset votedFor), move to Follower (cancel the in-progress
        //    election if Candidate, cancel heartbeat if Leader), and accept
        //    the peer as Director. This is what will silently demote a
        //    locally force-promoted node if the cluster reconverges with a higher term —
        //    the guarantee the Stage environment requires.
        //
        // 3) announce.Term == myTerm:
        //    Same term as mine. Sub-cases:
        //      3a) I am not Director, or the peer announces my own Id: accept.
        //          Legacy behavior (the Cast obeys the announce).
        //      3b) both believe we are Director with the SAME term: bug-12 case.
        //          Tiebreaker (MaxEntryId desc, PerformerId asc). If I lose,
        //          demote + OnDirectorElectionLost; if I win, rebut. Term-tie
        //          only happens by concurrent force-promote or a persistence bug;
        //          the tiebreaker preserves deterministic convergence in that case.
        private void HandleDirectorAnnounce(DirectorAnnounce announce, IStageChannel channel)
        {
            long myTerm = termStore?.CurrentTerm ?? 0L;

            // Rule 1: stale announce — ignore.
            if (announce.Term < myTerm)
            {
                hook.Logger.Debug(
                    $"[Stage {Id}] Stale DirectorAnnounce from {announce.DirectorId} " +
                    $"(announceTerm={announce.Term} < myTerm={myTerm}). Ignoring.");
                return;
            }

            // Rule 2: higher term — adopt + accept.
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
                // Bug 18 — failover by term-upgrade: if this is a rotation (we already
                // knew another Director, or we were the Director), the old data channels
                // are dead. Request an in-band re-handshake from the new Director.
                RequestRehandshakeIfRotated(oldDirector, announce.DirectorId);
                return;
            }

            // Rule 3: announce.Term == myTerm.
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
                    // If I was Candidate of the same term, we abort: someone already won.
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
                // Bug 18 — same-term: if the Director changed identity (rotation),
                // re-wire data channels in-band. The initial join (oldDirector==null)
                // is excluded: that pairing is out-of-band by contract.
                RequestRehandshakeIfRotated(oldDirector, announce.DirectorId);
                return;
            }

            // Sub-case 3b: both Directors with the same term — bug-12 tiebreaker.
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
                                // Bug 17 — silently dropped due to an EntryId collision. The Cast
                                // already has something at cue.EntryId, different from what the
                                // Director is sending. Typical path: the Cast wrote locally with
                                // PerformCmdLocal before the join, occupying low EntryIds that
                                // the Director's seed also wants to use. The Director's entry
                                // is lost. No throw, to preserve the protocol contract
                                // (catch-up must tolerate re-sends), but we log each
                                // drop and increment a counter so the host detects that
                                // it has an incoherent journal. If replicationDroppedOlderCount
                                // > 0 after pairing, the Cast did NOT receive the full seed.
                                System.Threading.Interlocked.Increment(ref replicationDroppedOlderCount);
                                hook.Logger.Debug(
                                    $"[Stage {Id}] DROP older CueEvent EntryId={cue.EntryId} (localMax={localMax} from peer={peerId}). " +
                                    $"Collision: este Stage ya tiene una entry en esa posicion. Total drops={replicationDroppedOlderCount}.");
                            }
                            break;

                        // Phase 5 — Playbill replication apply path.
                        // If the Cast has no Playbill configured, the cues are ignored
                        // (Playbill is legitimately audit-off). If it has one, they are applied
                        // idempotently: RegisterSchema is idempotent by contract;
                        // WriteRecord throws LanguageException on a duplicate, which is swallowed.
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
                                    // Duplicate EntryId (idempotent apply) or unknown schema —
                                    // log debug and continue so replication is not blocked.
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

        // --- Playbill attachment (Phase 5 — cross-pod replication) ---
        //
        // Binds a Playbill instance to this Stage. After this:
        //   - As Director: each RegisterSchema/WriteRecord on the Playbill
        //     fires a PlaybillSchemaCue/PlaybillCue broadcast to the Casts.
        //   - As Cast: incoming cues are applied to this Playbill via
        //     RegisterSchemaRaw / WriteRecordRaw (see ListenReplication).
        //
        // Both roles need attach — the Director to emit, the Cast to
        // receive. Once attached, it cannot be detached (not implemented;
        // disposing the Stage is sufficient).
        //
        // Call after ConfigureStorage and before StartAsync so that
        // Director-mode can emit from the first moment.
        public void AttachPlaybill(Playbill playbill)
        {
            if (playbill == null) throw new ArgumentNullException(nameof(playbill));
            if (hook.Playbill != null && !ReferenceEquals(hook.Playbill, playbill))
                throw new InvalidOperationException("A different Playbill is already attached to this Stage.");

            // Idempotent re-attach: do not duplicate the subscription if it is the same instance.
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
                    // CueEvent like any other record (signed: cross-stage atomicity
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

        // Phase 5 — Playbill schema registration broadcast. Only the Director
        // emits; the Casts ignore the callback (their local Playbill registers
        // schemas via the apply path in ListenReplication).
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

        // Phase 5 — Playbill record write broadcast. Same pattern as
        // the journal's OnRecordWritten: enqueue to the per-link worker to preserve
        // FIFO with respect to the journal record of the same EntryId.
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
        //  Casting election protocol phase (a) — Heartbeat sender + watchdog
        // =====================================================================

        // Bug 15 — fires OnRoleChanged if the current role differs from previousRole.
        // Called OUTSIDE the electionLock and AFTER updating heartbeatSenderCts
        // / electionRoundCts / directorId, so the subscriber sees consistent state
        // (IsDirector, CurrentDirectorId) when it inspects the Stage. Captures an
        // atomic snapshot of the current role+directorId under the lock so that two
        // events fired in rapid succession do not visibly interleave.
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

        // Starts/restarts the sender loop. Called at the end of PromoteToDirector.
        // Cancelling the previous CTS covers the re-promote case without an intervening StepDown.
        private void StartHeartbeatSender()
        {
            heartbeatSenderCts?.Cancel();
            heartbeatSenderCts = new CancellationTokenSource();
            var ct = heartbeatSenderCts.Token;
            _ = Task.Run(async () => await HeartbeatSenderLoop(ct));
        }

        // Director loop: every HeartbeatInterval it sends a Heartbeat to each peer
        // on the coordination bus with its CurrentEntryId as activity evidence.
        // Exits silently if IsDirector becomes false (due to StepDown, split-brain
        // loss, or isolation) or if the CTS is cancelled.
        //
        // Bug 16 diagnostics: each tick logs (IsDirector, peers_connected, sent_now,
        // total) and the exit logs its reason — a symptom was observed where the
        // Director fell silent for tens of seconds with no clue to discriminate "loop
        // dead" from "loop alive emitting but the transport drops". The
        // heartbeatsEmittedCount counter lets tests assert that the tick works even when
        // the transport does not confirm delivery.
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

        // Starts the Cast-side watchdog. Lifecycle per Stage: created in StartAsync
        // and cancelled in StopAsync/DisposeAsync via backgroundTasks.
        private void StartDirectorWatchdog()
        {
            directorWatchdogCts = new CancellationTokenSource();
            var ct = directorWatchdogCts.Token;
            backgroundTasks.Add(directorWatchdogCts);
            _ = Task.Run(async () => await DirectorWatchdogLoop(ct));
        }

        // Watchdog loop: every HeartbeatInterval it evaluates whether the registered
        // Director stopped sending heartbeats. Only counts if:
        //   1) We have a directorId assigned.
        //   2) That directorId is not me (I do not check myself).
        //   3) There is a recorded lastSeen (previous DirectorAnnounce or Heartbeat).
        // If now-lastSeen > DirectorTimeout, it clears directorId, removes the lastSeen
        // and fires OnDirectorLost. EnsureCanWrite will block writes from
        // this point until something reassigns Director.
        //
        // Phase b: the OnDirectorLost handler (or equivalent code right here)
        // will start a CastingPropose for auto-election.
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

                        // Phase b: auto-start election. If connectivity == Isolated
                        // or we are already Candidate of another round, StartElection is a no-op.
                        StartElection();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { hook.Logger.Error($"[Stage {Id}] DirectorWatchdog error", ex); }
        }

        // =====================================================================
        //  Casting election protocol phase (b) — state machine + voter logic
        // =====================================================================

        // Starts an election round as Candidate. Called from DirectorWatchdog
        // after Director-down. Idempotent with guards: no-op if we are already Candidate or
        // Leader, or if we are Isolated (with no peers an election is impossible).
        //
        // Quorum: floor(N/2)+1 including self, where N = peers + 1. Declared
        // exception: if N<=2 (2-peer Stage apps), 1 vote suffices — weak quorum.
        // For N=1 (alone, no peers) the Isolated guard catches earlier.
        private void StartElection()
        {
            if (connectivity == ConnectivityStatus.Isolated)
            {
                hook.Logger.Debug($"[Stage {Id}] StartElection: skipping (isolated).");
                return;
            }

            // Phase (d) refinement: the backoff retry must not run if we already know
            // a Director (the winning peer already announced while we were waiting on
            // backoff, or we won ourselves). Without this guard, a late retry
            // bumps the term and unfairly demotes the just-elected Director, generating
            // a loop between lagging Candidates that split-vote again.
            // The watchdog remains the single source of "Director down"; the
            // backoff retry assumes that decision was already made and only continues
            // if it is still true.
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
                termStore.BumpTerm(newTerm);   // resets votedFor
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

            // If quorum is met with the self-vote alone (weak 2-peer with a downed peer,
            // or N=1 but connectivity Connected — should not happen), we promote now.
            // We do not notify an intermediate Candidate in this branch: the Leader
            // event that BecomeDirector fires makes the role-change visible to the subscriber
            // without going through a phantom transition.
            if (selfQuorum)
            {
                BecomeDirector();
                return;
            }

            // Notification of the transition to Candidate (no self-quorum). The host
            // rarely acts on Candidate, but the API symmetry requires
            // firing it: the subscriber can use it for UI (awaiting election).
            RaiseRoleChanged(previousRole);

            // Broadcast to all connected peers. Fire-and-forget; the accepts
            // (or rejects with a higher term) will arrive via ListenCoordination.
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

            // Phase (d): start the round-timer. If after CastingElectionTimeout
            // we remain Candidate without reaching quorum (split-vote, network
            // losses, etc.), OnElectionRoundTimeout aborts this round, waits a
            // random backoff and retries with a bumped term.
            StartElectionRoundTimer(electionId);
        }

        // Starts the timer for the current round. Cancels the previous CTS (if it was
        // left dangling by a race with another retry) and creates a new one. The task runs in
        // the background, it does not block StartElection.
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

        // Called by the round timer (or by a test seam) when the round expires.
        // Verifies that we are still Candidate of THAT specific round before
        // acting — if in the meantime we received a DirectorAnnounce or adopted
        // a higher term, currentElectionId points to another round or is null, and
        // this timeout must be silently ignored.
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

            // Uniform random backoff in [0, CastingElectionTimeout/2].
            // The maximum ensures that two candidates with equal timeouts have
            // a high probability of diverging after 1-2 rounds. Larger
            // makes convergence slower; smaller increases the risk of
            // re-split. Half is the sweet spot the original plan cited (Raft
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
                // Retry. StartElection re-guard: if in the meantime another round
                // started (term-adopt, force-promote), role!=Follower and it
                // skips; otherwise it bumps the term and starts a new round.
                StartElection();
            });
        }

        // Candidate → Leader transition: quorum reached in the current round.
        // Runs OnFirstHydration/OnHydrated (same as PromoteToDirector), broadcasts
        // DirectorAnnounce with the winning term, starts the heartbeat sender. After
        // this, IsDirector == true and EnsureCanWrite permits writes.
        private void BecomeDirector()
        {
            if (hook.IsNew) OnFirstHydration();
            OnHydrated();

            // Round won — cancel the current round timer.
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

        // Voter side. Raft rules with the term-first adjustment already described:
        //   - propose.Term < myTerm           → reject(myTerm).
        //   - propose.Term > myTerm           → adopt term + reset vote + evaluate.
        //   - propose.Term == myTerm:
        //       - already voted for another candidate → reject.
        //       - already voted for this candidate    → idempotent re-accept.
        //       - no previous vote:
        //           - proposer.EntryCount < mine → reject (Raft completeness).
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
                        // Do not clear directorId yet — the winner of the new term will send
                        // DirectorAnnounce and it is updated there.
                        ;
                }
                if (heartbeatSenderCts != null) heartbeatSenderCts.Cancel();
                electionRoundCts?.Cancel();
                if (roleChanged) RaiseRoleChanged(previousRole);
            }

            // After a possible adopt, propose.Term == termStore.CurrentTerm.
            var prevVote = termStore.VotedFor;
            if (prevVote.HasValue)
            {
                if (prevVote.Value == propose.SenderId)
                {
                    // Idempotent re-vote.
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

        // Candidate side, receives an accept. If the round/term match the
        // current one, it records the vote; if it reaches quorum, it transitions to Leader.
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

        // Receives a reject. If the voter learns that we have a lower term
        // (reject.Term > myTerm), we adopt the term and move to Follower (silent step
        // down). If the term is equal, phase d adds retry with backoff;
        // for now we ignore the negative vote (the Candidate keeps awaiting quorum
        // or a term-update via a later reject/announce).
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
        //  Bug 18 — Failover replication gap: in-band re-handshake of data channels
        //
        //  After a role rotation the Coordination bus survives but the
        //  data channels (Replication/Command) are dead: the new Director has no
        //  CastLink toward the new Cast and the new Cast has no DirectorLink
        //  toward the new Director. In production each Stage runs in a separate
        //  process, so the host CANNOT repeat the out-of-band pairing (it has
        //  no way to cross the new ConnectionInvitation between processes).
        //
        //  The reconnection travels in-band over the only live channel (Coordination):
        //  the Cast asks (RehandshakeRequest with its lastEntryId), the Director creates
        //  the invitations and responds with their Address values (RehandshakeProposal), the
        //  Cast accepts and connects. The Director closes with a catch-up from the
        //  Cast's lastEntryId — so the entry the new Director writes during
        //  the reconnection window is not lost.
        // =====================================================================

        // Fired by the Cast when it adopts a Director. Only requests re-handshake if
        // this is a ROTATION: we already knew a different Director (or we were
        // the Director ourselves). The first learning (oldDirector == null) is the initial
        // join and is wired out-of-band by the pairing contract — no auto-rehandshake there,
        // to avoid duplicating the ConnectToDirector the host will do. Nor if the new
        // Director is me (I am Director, not Cast).
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

        // Director side: the Cast requests reconnection. I create Replication+Command
        // invitations, respond with their Address values over the same Coordination channel,
        // wait for the Cast to accept them, do AcceptCastConnection and a catch-up from the
        // Cast's lastEntryId. All in a separate Task: do NOT block the Coordination
        // listener (heartbeats, announces, votes keep flowing).
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

                    // Catch-up from the Cast's lastEntryId. Covers the entries written
                    // by this Director between the rotation and the close of the re-handshake.
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

        // Cast side: the Director responded with the Address values of the new invitations.
        // I accept and connect. ConnectionInvitation is fully reconstructible
        // from (InviterId, Purpose, Address) — InviterId is the proposal's SenderId.
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
        //  These members exist so that the Casting election protocol tests
        //  can (1) shorten the timeouts to ms scale, (2) read the observable
        //  state of the watchdog, and (3) simulate "Director silent without StepDown"
        //  by cancelling the sender without touching coordination channels.
        // =====================================================================

        internal StageConfiguration ConfigurationForTesting => config;

        internal DateTime? LastSeenDirectorForTesting(PerformerId id)
            => lastSeenDirector.TryGetValue(id, out var ts) ? ts : (DateTime?)null;

        internal void StopHeartbeatSenderForTesting()
            => heartbeatSenderCts?.Cancel();

        // Bug 16 diagnostics — the tests assert that the loop is alive and emitting
        // without depending on the transport (where InMemory != PortableHttps).
        internal long HeartbeatsEmittedCountForTesting => System.Threading.Interlocked.Read(ref heartbeatsEmittedCount);
        internal bool HeartbeatSenderRunningForTesting => heartbeatSenderRunning;

        // Bug 17 diagnostics — counter of CueEvent silently dropped due to a collision.
        // Tests assert that the counter rises when there is a pre-existing collision, and
        // stays at 0 on the happy path.
        internal long ReplicationDroppedOlderCountForTesting => System.Threading.Interlocked.Read(ref replicationDroppedOlderCount);

        // Snapshot of the election state machine for E2E tests.
        internal long CurrentTermForTesting => termStore?.CurrentTerm ?? 0L;
        internal PerformerId? VotedForForTesting => termStore?.VotedFor;
        internal string RoleForTesting { get { lock (electionLock) return role.ToString(); } }
        // Phase (d) — instrumentation for tests of the backoff path.
        internal int ElectionRoundTimeoutCountForTesting => electionRoundTimeoutCount;
        // Trigger an election explicitly from tests without going through watchdog/timeout.
        // The StartElection guard (isolated, role!=Follower) stays active, so
        // two parallel calls on the same stage do not produce 2 elections.
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
        // Phase 5 — generalized to StageMessage to also allow Playbill
        // cues (PlaybillSchemaCue / PlaybillCue) over the same channel and with the
        // same FIFO guarantee. The journal-vs-playbill ordering is preserved
        // within the link: the Director's writer thread invokes the journal
        // callback first and then the playbill callback (Performance.PerformCommand
        // order), so the cues enter the queue in that
        // order.
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
            // the queue has been completed (the link is shutting down). Accepts
            // any StageMessage (CueEvent, PlaybillSchemaCue, PlaybillCue).
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
