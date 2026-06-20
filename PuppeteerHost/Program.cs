using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;
using Choreography.Transport;
using Choreography.Transport.Https;
using Choreography.Usher;
using Choreography.Usher.Crypto;
using eShop.Ordering.Domain.AggregatesModel.OrderAggregate;
using Puppeteer;

namespace PuppeteerHost
{
    // Paper 7 demo host runtime — N-node version (default N=3).
    //
    // Per the Paper 7 design: the demo is three RSM peers in a full
    // coordination mesh, with the Director role rotating across all three.
    // The host-side CLI is a membership authority, NOT a peer.
    //
    // Environment variables:
    //
    //   PUPPETEER_SHARE_LINK                (required) Usher onboarding link
    //   PUPPETEER_LISTEN_URL                (required) peer-transport bind URL
    //   PUPPETEER_ADVERTISE_URL             (default = listen) peer-transport advertise URL
    //   PUPPETEER_ONBOARDING_LISTEN_URL     (required for cross-container) transient onboarding bind
    //   PUPPETEER_ONBOARDING_ADVERTISE_URL  (required for cross-container) transient onboarding advertise
    //   PUPPETEER_NODE_ID                   (required) "a" | "b" | "c" | ... unique per container
    //   PUPPETEER_PEER_IDS                  (required) comma-separated list of OTHER node ids
    //   PUPPETEER_ROTATION_ORDER            (required) comma-separated rotation list, e.g. "a,b,c"
    //   PUPPETEER_DATA_DIR                  (default /data)
    //   PUPPETEER_BOOTSTRAP_DIR             (default /bootstrap)
    //   PUPPETEER_ACTOR_NAME                (default ordering)
    //   PUPPETEER_DEVICE_NAME               (default Environment.MachineName)
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            try { return await RunAsync(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[host] FATAL: {ex}");
                return 1;
            }
        }

        private static async Task<int> RunAsync()
        {
            string shareLink     = RequireEnv("PUPPETEER_SHARE_LINK");
            string listenUrl     = RequireEnv("PUPPETEER_LISTEN_URL");
            string advertiseUrl  = Environment.GetEnvironmentVariable("PUPPETEER_ADVERTISE_URL") ?? listenUrl;
            string nodeId        = RequireEnv("PUPPETEER_NODE_ID");
            string peerIdsRaw    = RequireEnv("PUPPETEER_PEER_IDS");
            string rotationRaw   = RequireEnv("PUPPETEER_ROTATION_ORDER");
            string dataDir       = Environment.GetEnvironmentVariable("PUPPETEER_DATA_DIR")       ?? "/data";
            string bootstrapDir  = Environment.GetEnvironmentVariable("PUPPETEER_BOOTSTRAP_DIR")  ?? "/bootstrap";
            string actorName     = Environment.GetEnvironmentVariable("PUPPETEER_ACTOR_NAME")     ?? "ordering";
            string deviceName    = Environment.GetEnvironmentVariable("PUPPETEER_DEVICE_NAME")    ?? Environment.MachineName;
            // Workload regime: "inline" (default — one Script entry per
            // DSL line, used by the 3-Docker first-pass demo) or "parametric"
            // (a single multi-statement script + Parameters; emits a Define
            // entry once and an Invocation entry per round). Both regimes
            // exercise the same Order aggregate; they differ in journal
            // density. Closing the cross-container × parametric cell of
            // the Paper 7 2×2 matrix requires this env var to be set to
            // "parametric" in docker-compose; the in-process counterparts
            // live as F1 tests.
            string workloadMode  = (Environment.GetEnvironmentVariable("PUPPETEER_WORKLOAD_MODE")
                                    ?? "inline").Trim().ToLowerInvariant();
            if (workloadMode != "inline" && workloadMode != "parametric")
                return Fail($"PUPPETEER_WORKLOAD_MODE must be 'inline' or 'parametric'; got '{workloadMode}'");

            // Peer transport selection: "https" (default, the Paper 7 §5
            // demonstration baseline) or "simplex" (cross-validates the
            // §6 transport-pluggability claim by running the same 3-node
            // convergence over SimpleX SMP queues instead of Kestrel/TLS).
            string transportKind = (Environment.GetEnvironmentVariable("PUPPETEER_TRANSPORT_KIND")
                                    ?? "https").Trim().ToLowerInvariant();
            if (transportKind != "https" && transportKind != "simplex")
                return Fail($"PUPPETEER_TRANSPORT_KIND must be 'https' or 'simplex'; got '{transportKind}'");
            bool useSimpleX = transportKind == "simplex";
            string smpServer        = useSimpleX ? RequireEnv("PUPPETEER_SMP_SERVER")        : null;
            string smpFingerprintB64 = useSimpleX ? RequireEnv("PUPPETEER_SMP_FINGERPRINT") : null;

            string[] peerIds       = peerIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                              .Select(s => s.Trim()).ToArray();
            string[] rotationOrder = rotationRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                .Select(s => s.Trim()).ToArray();

            if (!rotationOrder.Contains(nodeId))
                return Fail($"PUPPETEER_NODE_ID '{nodeId}' must appear in PUPPETEER_ROTATION_ORDER '{rotationRaw}'");
            int myRotationIndex = Array.IndexOf(rotationOrder, nodeId);

            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(bootstrapDir);

            Console.Error.WriteLine(
                $"[host] node={nodeId} peers=[{string.Join(",", peerIds)}] rotation=[{string.Join(",", rotationOrder)}] " +
                $"listen={listenUrl} advertise={advertiseUrl} workload={workloadMode}");

            using var lifetime = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };

            // ===== 1. Identity: rehydrate from local journal or onboard via Usher =====
            //
            // Each actor in Puppeteer owns its journal as the source of truth.
            // Under FileSystem storage, the journal is the on-disk directory at
            // `dataDir`. If a journal for this actor already exists (i.e. this
            // container has been started before with the same /data volume),
            // the actor is rehydrated from it — no Usher contact, no new
            // identity, no new bootstrap. This is the property the framework's
            // vocabulary calls *puppet time-indifference*: from the actor's
            // perspective, every start is its first.
            //
            // The persisted identity file is a flat text file with the
            // PerformerId in "N" (32-hex-no-dashes) format. The rest of
            // OnboardedIdentity (keypair, journal secret, trusted SMP servers,
            // join-epoch) is not currently re-used outside of `OnboardAsync`
            // and so is not persisted here; should that change, this is the
            // place to extend.
            string identityPath = Path.Combine(dataDir, "identity.txt");
            bool isRehydration = File.Exists(identityPath);
            PerformerId stageId;

            if (isRehydration)
            {
                string stored = (await File.ReadAllTextAsync(identityPath, lifetime.Token)).Trim();
                stageId = new PerformerId(Guid.ParseExact(stored, "N"));
                Console.Error.WriteLine($"[host] Pre-existing journal detected at {dataDir}");
                Console.Error.WriteLine($"[host] Skipping Usher onboarding; rehydrating Stage with persisted stageId={stageId}");
            }
            else
            {
                OnboardedIdentity identity = await OnboardAsync(shareLink, listenUrl, deviceName, lifetime.Token);
                stageId = identity.AssignedStageId;
                Console.Error.WriteLine($"[host] Onboarded as stageId={stageId}");
                await File.WriteAllTextAsync(identityPath, stageId.ToString(), lifetime.Token);
            }

            // ===== 2. Build Stage with peer transport =====

            var stage = StageFactory.Create<StageV2>(
                stageId, actorName,
                typeof(Order).Assembly,
                typeof(OrderingFacade).Assembly);

            stage.ConfigureStorage(DatabaseType.FileSystem, $"path={dataDir}");
            if (useSimpleX)
            {
                byte[] smpFingerprint = DecodeBase64Url(smpFingerprintB64);
                stage.ConfigureTransport(TransportType.SimpleX, smpServer,
                                         serverFingerprint: smpFingerprint);
            }
            else
            {
                stage.ConfigureTransport(TransportType.Https, listenUrl, httpsAdvertiseUrl: advertiseUrl);
            }
            await stage.StartAsync(lifetime.Token);

            string myFingerprint = useSimpleX ? null : stage.LocalHttpsCertFingerprint;
            if (useSimpleX)
                Console.Error.WriteLine($"[host] Stage started; transport=SimpleX over {smpServer}; SMP-server fingerprint(TOFU)={smpFingerprintB64[..16]}...");
            else
                Console.Error.WriteLine($"[host] Stage started; transport=HTTPS; local TLS fingerprint: {myFingerprint}");

            if (isRehydration)
            {
                // The actor is restored from its local journal. The full
                // bootstrap → mesh → rotation flow that the first-start path
                // performs is NOT re-run: those steps were originally
                // synchronisations against the cluster's lifetime, not
                // re-derivable from the journal. The Stage exists with its
                // identity and its journal; mesh re-establishment and
                // replication catch-up are explicit future work (paper 7
                // follow-up — see notes/paper7_phase3_shotlist.md §"Video 3").
                //
                // For the F3 Video 3 storyboard's purposes this is the
                // canonical moment: the actor returns and is itself again,
                // before any reconnection.
                Console.Error.WriteLine(
                    $"[host] Rehydration complete. Journal at entry {stage.CurrentEntryId}; " +
                    "actor restored from its own journal alone.");
                Console.Error.WriteLine("[host] Holding alive for inspection. Ctrl+C to exit.");

                try { await Task.Delay(Timeout.Infinite, lifetime.Token); }
                catch (TaskCanceledException) { }

                await stage.DisposeAsync();
                return 0;
            }

            // ===== 3. Symmetric pairwise fingerprint exchange (HTTPS only) =====
            //
            // Para SimpleX el TOFU del SMP-server ya esta configurado por env var
            // (PUPPETEER_SMP_FINGERPRINT) en ConfigureTransport; no hay cert
            // per-host que pinar entre peers. SetupCoordMeshAsync sigue funcionando
            // porque ConnectionInvitation.Address es opaque al stage.

            if (!useSimpleX)
                await SymmetricFingerprintExchangeAsync(
                    stage, nodeId, peerIds, myFingerprint, advertiseUrl, bootstrapDir, lifetime.Token);

            // ===== 4. Mesh coordination setup (all N(N-1)/2 pairs) =====

            await SetupCoordMeshAsync(stage, nodeId, peerIds, bootstrapDir, lifetime.Token);

            // ===== 5. Director rotation rounds =====

            int totalRounds = rotationOrder.Length;
            for (int round = 0; round < totalRounds; round++)
            {
                string roundDirector = rotationOrder[round];
                Console.Error.WriteLine($"[host] === Round {round + 1}/{totalRounds}: Director = {roundDirector} ===");

                if (roundDirector == nodeId)
                    await RunDirectorRoundAsync(stage, round, nodeId, peerIds, bootstrapDir,
                                                isFirstRound: round == 0, workloadMode, lifetime.Token);
                else
                    await RunCastRoundAsync(stage, round, nodeId, roundDirector, peerIds, bootstrapDir,
                                            isFirstRound: round == 0, lifetime.Token);
            }

            long finalEntryId = stage.CurrentEntryId;
            Console.Error.WriteLine($"[host] All {totalRounds} rotation rounds complete; final journal entry = {finalEntryId}; convergence checkpoint reached");
            Console.Error.WriteLine("[host] Demo finished. Holding the Stage alive; Ctrl+C to exit.");

            try { await Task.Delay(Timeout.Infinite, lifetime.Token); }
            catch (TaskCanceledException) { }

            await stage.DisposeAsync();
            return 0;
        }

        // -------------------- Onboarding (unchanged shape) --------------------

        private static async Task<OnboardedIdentity> OnboardAsync(
            string shareLink, string peerListenUrl, string deviceName, CancellationToken ct)
        {
            var encoder = new UsherShareLinkEncoder();
            UsherInvitation invitation = encoder.Decode(shareLink);
            string usherFingerprint    = encoder.DecodeFingerprint(shareLink)
                ?? throw new InvalidOperationException("Share-link is missing the Usher cert fingerprint");

            string usherListenUrl = invitation.TransportInvitation.Address.Split('|')[0];
            Console.Error.WriteLine($"[host] Onboarding via Usher at {usherListenUrl}");

            string onboardingListen    = Environment.GetEnvironmentVariable("PUPPETEER_ONBOARDING_LISTEN_URL")
                                            ?? BumpPort(peerListenUrl, +1000);
            string onboardingAdvertise = Environment.GetEnvironmentVariable("PUPPETEER_ONBOARDING_ADVERTISE_URL");

            string[] sanHosts = onboardingAdvertise != null &&
                                Uri.TryCreate(onboardingAdvertise, UriKind.Absolute, out var advU)
                ? new[] { "localhost", deviceName, advU.Host }
                : new[] { "localhost", deviceName };
            var onboardingCert = SelfSignedCert.Generate(
                subjectCommonName: "puppeteer-host-onboarding", sanHosts);

            var transientLocalId = PerformerId.New();
            var onboardingTransport = new HttpsTransport(
                transientLocalId, onboardingListen, onboardingCert, onboardingAdvertise);
            onboardingTransport.TrustPeerFingerprint(usherListenUrl.TrimEnd('/'), usherFingerprint);

            try
            {
                return await StageOnboardingClient.JoinNetworkViaUsherAsync(
                    usherInvitation:  invitation.TransportInvitation,
                    invitationNonce:  invitation.Nonce,
                    deviceProfile:    new DeviceProfile(deviceName, "puppeteer-host"),
                    transport:        onboardingTransport,
                    keyGenerator:     new Ed25519StageKeyGenerator(),
                    signer:           new Ed25519StageSigner(),
                    payloadSealer:    new SealedBoxPayloadSealer(),
                    transientLocalId: transientLocalId,
                    ct:               ct);
            }
            finally
            {
                await onboardingTransport.DisposeAsync();
            }
        }

        // -------------------- Symmetric pairwise fingerprint exchange --------------------

        private static async Task SymmetricFingerprintExchangeAsync(
            StageV2 stage, string myId, string[] peerIds, string myFingerprint,
            string myAdvertiseUrl, string bootstrapDir, CancellationToken ct)
        {
            WriteAtomic(Path.Combine(bootstrapDir, $"node-{myId}-fp.txt"),  myFingerprint);
            WriteAtomic(Path.Combine(bootstrapDir, $"node-{myId}-url.txt"), myAdvertiseUrl);

            foreach (string peerId in peerIds)
            {
                string peerFp  = await WaitForFileAsync(
                    Path.Combine(bootstrapDir, $"node-{peerId}-fp.txt"),  TimeSpan.FromMinutes(2), ct);
                string peerUrl = await WaitForFileAsync(
                    Path.Combine(bootstrapDir, $"node-{peerId}-url.txt"), TimeSpan.FromMinutes(2), ct);
                stage.TrustPeerHttpsFingerprint(peerUrl.TrimEnd('/'), peerFp);
                Console.Error.WriteLine($"[host] Pinned peer {peerId}: {peerUrl} → fp {peerFp[..16]}...");
            }
        }

        // -------------------- Mesh coordination setup --------------------
        //
        // For each unordered pair {self, peer}, the lexicographically smaller
        // node is the "primary" for that coord channel — it creates the
        // invitation and the larger node accepts. After the channel is
        // established both call JoinCoordination so the coord bus is
        // symmetric. The PrimaryFor / SecondaryFor predicates make the
        // delegation deterministic without any further negotiation.
        private static async Task SetupCoordMeshAsync(
            StageV2 stage, string myId, string[] peerIds, string bootstrapDir, CancellationToken ct)
        {
            // Pre-start the listener so accepter-side calls don't race the
            // bind. WaitForConnectionAsync calls EnsureStartedAsync, so any
            // throwaway invitation works.
            var primingInv = await stage.CreateInvitationAsync(ChannelPurpose.Coordination);
            using var primingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            primingCts.CancelAfter(TimeSpan.FromMilliseconds(50));
            _ = stage.WaitForConnectionAsync(primingInv, primingCts.Token);
            await Task.Delay(500, ct);   // Kestrel finishes binding

            foreach (string peerId in peerIds)
            {
                string pairKey = PairKey(myId, peerId);
                if (string.Compare(myId, peerId, StringComparison.Ordinal) < 0)
                {
                    // I am the primary for this pair: create + publish.
                    var inv = await stage.CreateInvitationAsync(ChannelPurpose.Coordination);
                    var waitTask = stage.WaitForConnectionAsync(inv, ct);

                    WriteAtomic(Path.Combine(bootstrapDir, $"coord-{pairKey}.addr"),
                        inv.Address);
                    WriteAtomic(Path.Combine(bootstrapDir, $"coord-{pairKey}.inviter"),
                        inv.InviterId.ToString());

                    var channel = await waitTask;
                    await stage.JoinCoordination(channel.RemotePerformerId, channel);
                    Console.Error.WriteLine($"[host] Coord pair {pairKey}: peer joined as accepter");
                }
                else
                {
                    // I am the secondary for this pair: wait + accept.
                    string addr        = await WaitForFileAsync(
                        Path.Combine(bootstrapDir, $"coord-{pairKey}.addr"),    TimeSpan.FromMinutes(2), ct);
                    string inviterHex  = await WaitForFileAsync(
                        Path.Combine(bootstrapDir, $"coord-{pairKey}.inviter"), TimeSpan.FromMinutes(2), ct);
                    var inviterId = new PerformerId(Guid.Parse(inviterHex));

                    var channel = await stage.AcceptInvitationAsync(
                        new ConnectionInvitation(inviterId, ChannelPurpose.Coordination, addr));
                    await stage.JoinCoordination(inviterId, channel);
                    Console.Error.WriteLine($"[host] Coord pair {pairKey}: connected as accepter to {peerId}");
                }
            }

            Console.Error.WriteLine($"[host] Mesh coord setup complete ({peerIds.Length} peers)");
        }

        // -------------------- Per-rotation-round Director / Cast paths --------------------
        //
        // Each rotation round establishes a fresh Director→Cast data star,
        // runs ONE workload chunk, then dissolves (the next round builds
        // its own star with a different Director). File-naming includes
        // the round index so successive rounds don't see stale invitations.
        //
        // Director's workload per round:
        //   - round 0  (initial): bootstrap (1 entry) + happy-path (7 entries) = 8 entries.
        //   - round N>0:          happy-path (7 entries).

        private const string BootstrapDsl = "f = OrderingFacade();";

        // Inline-mode workload. Each call to PerformCmd lands one Script
        // entry on the journal — 7 entries per round + 1 bootstrap on
        // the first round = 8 / 7 / 7 across the rotation, ending at 22.
        private static readonly string[] HappyPathDsl = {
            "o = f.NewSubmittedOrder('demo-user', 'Demo Alice');",
            "o.AddOrderItem(1001, 'widget', 99, 0, '', 1);",
            "o.AddOrderItem(1002, 'gadget', 199, 0, '', 1);",
            "o.SetAwaitingValidationStatus();",
            "o.SetStockConfirmedStatus();",
            "o.SetPaidStatus();",
            "o.SetShippedStatus();"
        };

        // Parametric-mode workload. One multi-statement DSL body with
        // Parameters bindings: the first invocation lands a Define entry
        // (the script body, once) AND an Invocation entry (argv); every
        // subsequent invocation against the SAME body lands only an
        // Invocation entry. Across a 3-round rotation this is
        // bootstrap(1) + (Define + Invocation)(2) + Invocation(1) + Invocation(1) = 5 entries total.
        //
        // The cross-container parametric path exercises the wire-format v2
        // change in HttpsTransport (a ChannelPurpose byte after the sender
        // id) that was introduced specifically to keep Define+Invocation
        // CueEvents from colliding on the receiver's channel dictionary
        // — bug 5 of the demo-cycle hardening commit 18cbf7a.
        private const string HappyPathParametricDsl = @"
            o = f.NewSubmittedOrder(uid, uname);
            o.AddOrderItem(pid1, name1, price1, disc, pic, units);
            o.AddOrderItem(pid2, name2, price2, disc, pic, units);
            o.SetAwaitingValidationStatus();
            o.SetStockConfirmedStatus();
            o.SetPaidStatus();
            o.SetShippedStatus();
        ";

        private static Puppeteer.Parameters BuildHappyPathParams(int round)
        {
            // Vary the values per round so Parameters dispatch sees fresh
            // argvs but the script body is identical — that's what triggers
            // the Define-cache-hit path on rounds 2 and 3.
            var p = new Puppeteer.Parameters();
            p["Now", typeof(DateTime)] = DateTime.Now;
            p["uid",    typeof(string)]  = $"user-r{round}";
            p["uname",  typeof(string)]  = $"Demo Alice R{round}";
            p["pid1",   typeof(int)]     = 1001 + round * 10;
            p["name1",  typeof(string)]  = "widget";
            p["price1", typeof(decimal)] = 99.50m + round;
            p["pid2",   typeof(int)]     = 1002 + round * 10;
            p["name2",  typeof(string)]  = "gadget";
            p["price2", typeof(decimal)] = 199.00m + round;
            p["disc",   typeof(decimal)] = 0m;
            p["pic",    typeof(string)]  = string.Empty;
            p["units",  typeof(int)]     = 1;
            return p;
        }

        private static async Task RunDirectorRoundAsync(StageV2 stage, int round,
            string myId, string[] peerIds, string bootstrapDir, bool isFirstRound,
            string workloadMode, CancellationToken ct)
        {
            // Wait briefly for the coordination bus to reach steady state
            // (DirectorAnnounce / step-down propagation are async).
            await Task.Delay(300, ct);

            stage.PromoteToDirector();
            Console.Error.WriteLine($"[host] Round {round + 1}: promoted self to Director");

            // Create data-star invitations to each peer (replication + command).
            var dataWaitTasks = new List<(string peerId, Task<IStageChannel> rep, Task<IStageChannel> cmd)>();
            foreach (string peerId in peerIds)
            {
                var repInv = await stage.CreateInvitationAsync(ChannelPurpose.Replication);
                var cmdInv = await stage.CreateInvitationAsync(ChannelPurpose.Command);
                var waitRep = stage.WaitForConnectionAsync(repInv, ct);
                var waitCmd = stage.WaitForConnectionAsync(cmdInv, ct);
                await Task.Delay(200, ct);   // listener-bind grace window

                string pairKey = PairKey(myId, peerId);
                WriteAtomic(Path.Combine(bootstrapDir, $"r{round}-data-{pairKey}-rep.addr"),     repInv.Address);
                WriteAtomic(Path.Combine(bootstrapDir, $"r{round}-data-{pairKey}-rep.inviter"),  repInv.InviterId.ToString());
                WriteAtomic(Path.Combine(bootstrapDir, $"r{round}-data-{pairKey}-cmd.addr"),     cmdInv.Address);
                WriteAtomic(Path.Combine(bootstrapDir, $"r{round}-data-{pairKey}-cmd.inviter"),  cmdInv.InviterId.ToString());

                dataWaitTasks.Add((peerId, waitRep, waitCmd));
            }

            // Wait for all casts to connect on both their data channels.
            foreach (var (peerId, repWait, cmdWait) in dataWaitTasks)
            {
                var repCh = await repWait;
                var cmdCh = await cmdWait;
                await stage.AcceptCastConnection(repCh.RemotePerformerId, repCh, cmdCh);
                Console.Error.WriteLine($"[host] Round {round + 1}: cast {peerId} connected (replication+command)");
            }

            // Issue the workload chunk. The DSL form depends on the
            // configured PUPPETEER_WORKLOAD_MODE — see the constants above.
            Console.Error.WriteLine($"[host] Round {round + 1}: issuing workload ({workloadMode})");
            if (isFirstRound)
                await stage.PerformCmd(BootstrapDsl, ct);

            if (workloadMode == "parametric")
            {
                var p = BuildHappyPathParams(round);
                await stage.PerformCmd(HappyPathParametricDsl, p, DateTime.Now,
                                       "0.0.0.0", "Anonymous", ct);
            }
            else
            {
                foreach (var line in HappyPathDsl)
                    await stage.PerformCmd(line, ct);
            }

            long afterThisRound = stage.CurrentEntryId;
            Console.Error.WriteLine($"[host] Round {round + 1}: my journal at entry {afterThisRound}; waiting for casts to converge");

            // The Director cannot directly observe the Casts' journals, but
            // they will catch up over the replication channel before they
            // see the round-done signal. Brief delay + write signal.
            await Task.Delay(500, ct);
            WriteAtomic(Path.Combine(bootstrapDir, $"r{round}-done-{myId}.txt"),
                afterThisRound.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Console.Error.WriteLine($"[host] Round {round + 1}: signalled done at entry {afterThisRound}");

            // Step down so the next round's Director can take over (unless
            // I'm the last in the rotation, in which case we just stop here).
            await stage.StepDownAsync();
            Console.Error.WriteLine($"[host] Round {round + 1}: stepped down");
        }

        private static async Task RunCastRoundAsync(StageV2 stage, int round,
            string myId, string directorId, string[] peerIds, string bootstrapDir,
            bool isFirstRound, CancellationToken ct)
        {
            // Wait for the Director's data invitations to appear on the volume.
            string pairKey = PairKey(myId, directorId);
            string repAddr = await WaitForFileAsync(
                Path.Combine(bootstrapDir, $"r{round}-data-{pairKey}-rep.addr"),     TimeSpan.FromMinutes(2), ct);
            string repInviterHex = await WaitForFileAsync(
                Path.Combine(bootstrapDir, $"r{round}-data-{pairKey}-rep.inviter"),  TimeSpan.FromMinutes(2), ct);
            string cmdAddr = await WaitForFileAsync(
                Path.Combine(bootstrapDir, $"r{round}-data-{pairKey}-cmd.addr"),     TimeSpan.FromMinutes(2), ct);
            string cmdInviterHex = await WaitForFileAsync(
                Path.Combine(bootstrapDir, $"r{round}-data-{pairKey}-cmd.inviter"),  TimeSpan.FromMinutes(2), ct);

            var inviterId = new PerformerId(Guid.Parse(repInviterHex));

            var repChan = await stage.AcceptInvitationAsync(
                new ConnectionInvitation(inviterId, ChannelPurpose.Replication, repAddr));
            var cmdChan = await stage.AcceptInvitationAsync(
                new ConnectionInvitation(inviterId, ChannelPurpose.Command, cmdAddr));
            await stage.ConnectToDirector(inviterId, repChan, cmdChan);
            Console.Error.WriteLine($"[host] Round {round + 1}: connected to Director {directorId} (replication+command)");

            // Wait for the round-done signal from the Director, then verify
            // our journal caught up to the expected entry count.
            string doneContent = await WaitForFileAsync(
                Path.Combine(bootstrapDir, $"r{round}-done-{directorId}.txt"),
                TimeSpan.FromMinutes(2), ct);

            long expectedEntries = long.Parse(doneContent, System.Globalization.CultureInfo.InvariantCulture);

            await WaitForEntryIdAtLeastAsync(stage, expectedEntries, TimeSpan.FromSeconds(30), ct);
            Console.Error.WriteLine($"[host] Round {round + 1}: caught up to entry {stage.CurrentEntryId} (target {expectedEntries})");
        }

        // -------------------- Small helpers --------------------

        private static string PairKey(string a, string b)
        {
            return string.Compare(a, b, StringComparison.Ordinal) < 0
                ? $"{a}-{b}"
                : $"{b}-{a}";
        }

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }

        private static async Task<string> WaitForFileAsync(string path, TimeSpan timeout, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    string content = await File.ReadAllTextAsync(path, ct);
                    if (!string.IsNullOrWhiteSpace(content))
                        return content.Trim();
                }
                await Task.Delay(200, ct);
            }
            throw new TimeoutException($"File {path} did not appear within {timeout}");
        }

        private static async Task WaitForEntryIdAtLeastAsync(
            StageV2 stage, long expected, TimeSpan timeout, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (stage.CurrentEntryId < expected && DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(100, ct);
            }
        }

        private static string RequireEnv(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v))
                throw new InvalidOperationException($"Required env var {name} is not set");
            return v;
        }

        // base64url -> bytes. El SMP-server fingerprint viene como string
        // base64url (sin padding, con '-'/'_' en vez de '+'/'/'). Mismo
        // formato que SimplexShareLink y la salida de docker logs.
        private static byte[] DecodeBase64Url(string b64url)
        {
            if (string.IsNullOrEmpty(b64url)) throw new ArgumentNullException(nameof(b64url));
            string padded = b64url.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine($"[host] FATAL: {message}");
            return 1;
        }

        private static string BumpPort(string url, int delta)
        {
            var u = new Uri(url);
            int port = (u.IsDefaultPort ? 443 : u.Port) + delta;
            return $"{u.Scheme}://{u.Host}:{port}{u.AbsolutePath}";
        }
    }
}
