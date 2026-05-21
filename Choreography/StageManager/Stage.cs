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

namespace Choreography.StageManager
{
    public enum ConnectivityStatus
    {
        Isolated,
        Connected
    }

    public abstract class Stage : IAsyncDisposable
    {
        private StageConfiguration config;
        private IStageTransport transport;
        protected StageHook hook;
        protected DatabaseType dbType;
        private string storageConnectionString;

        // Coordination bus: all Koras talk to all Koras (lightweight, membership/election)
        private readonly ConcurrentDictionary<PerformerId, IStageChannel> coordinationPeers = new();

        // Data star: Director↔Cast links (heavy, replication/commands)
        private DirectorLink directorLink;  // non-null when I am Cast
        private readonly ConcurrentDictionary<PerformerId, CastLink> castLinks = new();  // populated when I am Director

        private readonly List<CancellationTokenSource> backgroundTasks = new();
        protected readonly ConcurrentDictionary<Guid, TaskCompletionSource<CommandResult>> pendingCommands = new();
        private readonly ConcurrentDictionary<long, BufferedEntry> entryBuffer = new();

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
        public ConnectivityStatus Connectivity => connectivity;
        protected internal Assembly[] LibraryAssemblies { get; }

        public event Action<PerformerId> OnDirectorChanged;

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

        public void ConfigureTransport(TransportType transportType, string url = null, byte[] serverFingerprint = null,
            System.Security.Cryptography.X509Certificates.X509Certificate2 httpsServerCert = null,
            string httpsAdvertiseUrl = null)
        {
            if (isRunning) throw new InvalidOperationException("Cannot configure transport while running");

            this.transport = transportType switch
            {
                TransportType.InMemory => new InMemoryTransport(Id),
                TransportType.SimpleX => CreateSimplexTransport(url, serverFingerprint),
                TransportType.Https => CreateHttpsTransport(url, httpsServerCert, httpsAdvertiseUrl),
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
            string advertiseUrl)
        {
            if (listenUrl == null) throw new ArgumentNullException(nameof(listenUrl), "listenUrl is required for Https transport");
            cert ??= Transport.Https.SelfSignedCert.Generate(
                subjectCommonName: "puppeteer-stage",
                "localhost",
                System.Uri.TryCreate(listenUrl, UriKind.Absolute, out var lu) ? lu.Host : "puppeteer-stage",
                System.Uri.TryCreate(advertiseUrl, UriKind.Absolute, out var au) ? au.Host : "puppeteer-stage");
            return new Transport.Https.HttpsTransport(Id, listenUrl, cert, advertiseUrl);
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
        private IStageTransport CreateSimplexTransport(string url, byte[] serverFingerprint)
        {
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("[Choreography] SimpleX transport stubbed to InMemoryTransport on Windows. " +
                                  "Run on Android emulator or Linux for the real path.");
                return new InMemoryTransport(Id);
            }
            return new Transport.SimpleX.SimplexTransport(Id,
                url ?? throw new ArgumentNullException(nameof(url), "url is required for SimpleX transport"),
                serverFingerprint);
        }

        // --- Lifecycle ---

        public Task StartAsync(CancellationToken ct = default)
        {
            if (!storageConfigured)
                throw new InvalidOperationException("Storage not configured. Call ConfigureStorage before StartAsync.");
            if (!transportConfigured)
                throw new InvalidOperationException("Transport not configured. Call ConfigureTransport before StartAsync.");

            Directory.CreateDirectory(config.StageStateDirectory);

            hook.InitializeStorage(dbType, storageConnectionString);
            hook.OnRecordWritten = OnRecordWritten;
            // Phase 5 of the Action refactor: dropped hook.OnNewActionDefined wiring.
            // Define entries are journal records and replicate via OnRecordWritten →
            // CueEvent like any other record (firmado: cross-stage atomicity is
            // unnecessary — the director's journal already persisted the pair
            // transactionally). The follower applies the Define record via
            // ApplyReplicatedEvent which dispatches to AddKnownActionFromDefine.

            isRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            hook.GracefulExit();
            isRunning = false;
            connectivity = ConnectivityStatus.Isolated;
            foreach (var cts in backgroundTasks) cts.Cancel();
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
        //  COORDINATION BUS: all Koras ↔ all Koras (lightweight)
        //  Messages: DirectorAnnounce, MemberLeave, MemberJoin, Heartbeat, Casting
        // =====================================================================

        public async Task JoinCoordination(PerformerId peerId, IStageChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));

            coordinationPeers[peerId] = channel;
            RefreshConnectivity();

            var cts = new CancellationTokenSource();
            backgroundTasks.Add(cts);
            _ = Task.Run(async () => await ListenCoordination(peerId, channel, cts.Token), cts.Token);

            if (IsDirector)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    try { await channel.SendAsync(new DirectorAnnounce(Id, Id)); }
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
            // PromoteToDirector (linea ~430) y workaround-eado en KoraApp con
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
            link.StartReplicationSender(Id);
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

            Console.WriteLine($"[Stage {Id}] Stepping down as Director");
            directorId = null;

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

            Console.WriteLine($"[Stage {Id}] Promoting self to Director");
            directorId = Id;
            OnDirectorChanged?.Invoke(Id);

            foreach (var kvp in coordinationPeers)
            {
                if (kvp.Value.IsConnected)
                {
                    var announce = new DirectorAnnounce(Id, Id);
                    var ch = kvp.Value;
                    _ = Task.Run(async () =>
                    {
                        try { await ch.SendAsync(announce); }
                        catch { }
                    });
                }
            }
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
            Console.WriteLine($"[Stage {Id}] CatchUp for {peerId}: from {peerLastEntryId + 1} to {myMax}");

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
                    Console.WriteLine($"[Stage {Id}] CatchUp: record {eid} not in buffer");
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
                            Console.WriteLine($"[Stage {Id}] DirectorAnnounce from {announce.DirectorId}");
                            directorId = announce.DirectorId;
                            OnDirectorChanged?.Invoke(announce.DirectorId);
                            break;

                        case MemberLeave leave:
                            Console.WriteLine($"[Stage {Id}] MemberLeave from {leave.SenderId}");
                            if (directorId.HasValue && directorId.Value == leave.SenderId)
                            {
                                Console.WriteLine($"[Stage {Id}] Director left, director is now null");
                                directorId = null;
                            }
                            break;

                        case MemberJoinAck ack:
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[Stage {Id}] ListenCoordination error: {ex.Message}"); }
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
                                Console.WriteLine($"[Stage {Id}] GAP: expected {localMax + 1}, got {cue.EntryId}");
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[Stage {Id}] ListenReplication error: {ex.Message}"); }
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
            catch (Exception ex) { Console.WriteLine($"[Stage {Id}] ListenCommand error: {ex.Message}"); }
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

        public async ValueTask DisposeAsync()
        {
            hook.GracefulExit();
            isRunning = false;
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
        }

        private class DirectorLink
        {
            public IStageChannel Replication;
            public IStageChannel Command;
        }

        // Per-link replication outbox. A single worker drains a FIFO queue and
        // awaits each SendAsync before pulling the next CueEvent. This is the
        // structural guarantee that — for a given Cast — records arrive in the
        // same order in which the writer thread invoked OnRecordWritten.
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
        private class CastLink : IAsyncDisposable
        {
            public IStageChannel Replication;
            public IStageChannel Command;

            // Set when the sender worker starts; only used for diagnostic logs.
            private PerformerId stageId;

            private readonly Channel<CueEvent> outboundQueue = Channel.CreateUnbounded<CueEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });
            private CancellationTokenSource workerCts;
            private Task workerTask;

            public void StartReplicationSender(PerformerId stageId)
            {
                this.stageId = stageId;
                this.workerCts = new CancellationTokenSource();
                var token = this.workerCts.Token;
                this.workerTask = Task.Run(() => PumpAsync(token));
            }

            // Non-blocking enqueue for the writer thread. Returns false only if
            // the queue has been completed (the link is shutting down).
            public bool EnqueueReplication(CueEvent cue) => outboundQueue.Writer.TryWrite(cue);

            // Async enqueue for callers that want backpressure-friendly
            // semantics — e.g. catch-up loops that should respect a
            // CancellationToken. For an unbounded channel this completes
            // synchronously in practice.
            public ValueTask EnqueueReplicationAsync(CueEvent cue, CancellationToken ct)
                => outboundQueue.Writer.WriteAsync(cue, ct);

            private async Task PumpAsync(CancellationToken ct)
            {
                try
                {
                    await foreach (var cue in outboundQueue.Reader.ReadAllAsync(ct))
                    {
                        var channel = Replication;
                        if (channel == null || !channel.IsConnected) continue;
                        try
                        {
                            await channel.SendAsync(cue, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Stage {stageId}] Error sending CueEvent {cue.EntryId}: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Stage {stageId}] Replication sender worker exited: {ex.Message}");
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
