using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;
using Choreography.Transport;
using Choreography.Transport.Https;
using Choreography.Usher;
using Choreography.Usher.Crypto;
using Puppeteer;
using Puppeteer.EventSourcing.DB;
using PuppeteerCli.Mocks;

namespace PuppeteerCli
{
    // Paper 7 Phase 2 — `puppeteer issue-invitation` CLI.
    //
    // Stands in for ContactSecret (the "membership authority" of the Choreography
    // architecture) on an operator's laptop. The CLI emits one or more
    // onboarding share-links over a real-TLS HTTPS endpoint, holds the
    // Usher state machine alive while joiners (Docker containers running
    // PuppeteerHost) complete the F1-F5 handshake, and exits cleanly.
    //
    // Per the F2 briefing (project_puppeteer_paper07_thesis.md addendum
    // 2026-05-16 PM): #2 crypto is real (Ed25519 + sealed box), #3 journal
    // writer is mocked (no shared RSM journal), #4-#7 / #9 are out of scope.
    //
    // Usage:
    //   puppeteer issue-invitation --listen https://localhost:5443
    //                             [--count 2] [--ttl-minutes 10]
    //
    // The share-link is emitted to stdout in a fenced "BEGIN/END SHARE-LINK"
    // block, suitable for piping to a QR encoder (qrencode, etc.) without
    // capturing diagnostic lines.
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            string command = args[0];
            string[] rest  = args.Skip(1).ToArray();

            return command switch
            {
                "attach"           => AttachCommand.Run(rest),
                "show"             => ShowCommand(rest),
                "issue-invitation" => await IssueInvitationAsync(rest),
                "chronicle"        => ChronicleNotImplemented(),
                "--help" or "-h" or "help" => (PrintUsage(), 0).Item2,
                _ => (PrintUsageErr(command), 1).Item2
            };
        }

        // chronicle is the human supervision surface — reserved namespace,
        // not yet implemented. Distinct abstraction from the AI-facing CLI;
        // shares the binary as a door (see notes for the framing).
        private static int ChronicleNotImplemented()
        {
            Console.Error.WriteLine("'chronicle' is the human supervision surface (consumes narratives from");
            Console.Error.WriteLine("the PromptBook journal). Reserved verb namespace; not yet implemented.");
            return 1;
        }

        private static int PrintUsage()
        {
            Console.WriteLine("puppeteer — AI-native CLI for Puppeteer (operate, supervise, onboard)");
            Console.WriteLine();
            Console.WriteLine("Operate (AI-facing):");
            Console.WriteLine("  attach --primary <conn> --actor-name <name> --snapshot");
            Console.WriteLine("         [--libraries <dll[,dll]>] [--prompt-book <dir>]");
            Console.WriteLine("       Open a hydrated session against a primary's journal in shadow mode.");
            Console.WriteLine("       --snapshot is required (only mode today; --live arrives later).");
            Console.WriteLine("       --libraries loads domain DLLs needed to parse/execute DSL.");
            Console.WriteLine("                  Pass the assemblies that define the actor's classes;");
            Console.WriteLine("                  without them only meta-verbs (exit/help/chronicle) work.");
            Console.WriteLine("       --prompt-book overrides the default PromptBook journal location.");
            Console.WriteLine("                  Default: %LOCALAPPDATA%/PuppeteerCli/PromptBook/ (Windows)");
            Console.WriteLine("                  or ~/.local/share/PuppeteerCli/PromptBook/ (Unix).");
            Console.WriteLine("                  One PromptBook per user by default — sessions across all");
            Console.WriteLine("                  targets share one memory. Override per-project if needed.");
            Console.WriteLine();
            Console.WriteLine("  show entry <id> --journal <path=DIR> --actor-name <name>");
            Console.WriteLine("       Print the journal entry with EntryId == <id> as TOON.");
            Console.WriteLine("       Read-only. Works on a FileSystem journal directory without");
            Console.WriteLine("       loading domain libraries (no rehydration).");
            Console.WriteLine();
            Console.WriteLine("  show action <actionId> --journal <path=DIR> --actor-name <name>");
            Console.WriteLine("       Print the active Define entry for <actionId> as TOON.");
            Console.WriteLine("       Latest Define wins when the journal contains redefinitions.");
            Console.WriteLine();
            Console.WriteLine("Supervise (human-facing, placeholder):");
            Console.WriteLine("  chronicle ...");
            Console.WriteLine("       Reserved namespace for the human supervision surface (consumes");
            Console.WriteLine("       narratives from the PromptBook journal). Not yet implemented.");
            Console.WriteLine();
            Console.WriteLine("Onboard:");
            Console.WriteLine("  issue-invitation --listen <https-url> [--advertise <https-url>]");
            Console.WriteLine("                  [--count N] [--ttl-minutes M]");
            Console.WriteLine("       Paper 7 Phase 2 — emit onboarding share-links over real-TLS HTTPS.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  puppeteer attach --primary \"path=C:/journals\" --actor-name banco --snapshot");
            Console.WriteLine("  puppeteer show entry 42 --journal \"path=C:\\journals\" --actor-name banco");
            Console.WriteLine("  puppeteer show action 42 --journal \"path=C:\\journals\" --actor-name banco");
            Console.WriteLine("  puppeteer issue-invitation --listen https://localhost:5443");
            return 0;
        }

        private static int PrintUsageErr(string unknown)
        {
            Console.Error.WriteLine($"Unknown command: {unknown}");
            Console.Error.WriteLine();
            PrintUsage();
            return 1;
        }

        // ------------------------------ issue-invitation ----------------------

        private static async Task<int> IssueInvitationAsync(string[] args)
        {
            string listenUrl    = null;
            string advertiseUrl = null;
            int count           = 1;
            int ttlMinutes      = 10;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--listen":
                        if (++i >= args.Length) return Fail("--listen requires a URL");
                        listenUrl = args[i];
                        break;
                    case "--advertise":
                        if (++i >= args.Length) return Fail("--advertise requires a URL");
                        advertiseUrl = args[i];
                        break;
                    case "--count":
                        if (++i >= args.Length) return Fail("--count requires N");
                        if (!int.TryParse(args[i], out count) || count < 1)
                            return Fail($"--count must be a positive integer; got {args[i]}");
                        break;
                    case "--ttl-minutes":
                        if (++i >= args.Length) return Fail("--ttl-minutes requires M");
                        if (!int.TryParse(args[i], out ttlMinutes) || ttlMinutes < 1)
                            return Fail($"--ttl-minutes must be a positive integer; got {args[i]}");
                        break;
                    default:
                        return Fail($"Unknown flag: {args[i]}");
                }
            }

            if (string.IsNullOrWhiteSpace(listenUrl))
                return Fail("--listen <https-url> is required");
            if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https")
                return Fail($"--listen must be a https:// URL; got: {listenUrl}");
            if (advertiseUrl == null)
                advertiseUrl = listenUrl;
            else if (!Uri.TryCreate(advertiseUrl, UriKind.Absolute, out var advUri) ||
                     advUri.Scheme != "https")
                return Fail($"--advertise must be a https:// URL; got: {advertiseUrl}");

            // --- Build Usher infrastructure with real crypto ---

            var operatorId      = OperatorId.New();
            var usherTransportId = PerformerId.New();
            string advertiseHost = new Uri(advertiseUrl).Host;
            var cert             = SelfSignedCert.Generate(
                                       subjectCommonName: "puppeteer-usher",
                                       "localhost", uri.Host, advertiseHost);
            var fingerprint      = SelfSignedCert.Fingerprint(cert);

            var transport       = new HttpsTransport(usherTransportId, listenUrl, cert, advertiseUrl);
            var invitationStore = new InMemoryInvitationStore();
            var approvalQueue   = new AutoApprovingApprovalQueue(operatorId);
            // The journal writer pushes each completed onboarding into a
            // queue so the main loop can wait for N consecutive handshakes
            // against a single Usher instance.
            var completionQueue = new System.Collections.Concurrent.BlockingCollection<MembershipRecord>(
                new System.Collections.Concurrent.ConcurrentQueue<MembershipRecord>());
            var journalWriter   = new QueueingJournalWriter(completionQueue);
            var verifier        = new Ed25519StageSignatureVerifier();
            var sealer          = new SealedBoxPayloadSealer();
            var encoder         = new UsherShareLinkEncoder();

            // The Usher's "journal secret" stub — sealed and shipped inside
            // UsherJoinResponse, but never used to encrypt anything at rest
            // in F2 (Docker journals are plain FileSystem). Fresh per run.
            byte[] journalSecret = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(journalSecret);

            await using var usher = new Usher(
                localId: usherTransportId,
                transport: transport,
                invitationStore: invitationStore,
                approvalQueue: approvalQueue,
                journalWriter: journalWriter,
                payloadSealer: sealer,
                signatureVerifier: verifier,
                journalSecretProvider: () => journalSecret,
                trustedSmpServersProvider: () => Array.Empty<ServerFingerprintWire>(),
                defaultTtl: TimeSpan.FromMinutes(ttlMinutes));

            using var lifetime = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };

            // --- Issue N invitations ---

            Console.Error.WriteLine($"[puppeteer] Starting issuer on {listenUrl}");
            Console.Error.WriteLine($"[puppeteer] Usher cert fingerprint (SHA-256): {fingerprint}");
            Console.Error.WriteLine($"[puppeteer] TTL per invitation: {ttlMinutes} min");
            Console.Error.WriteLine();

            var invitationDeadlines = new List<DateTime>();
            for (int i = 1; i <= count; i++)
            {
                var invitation = await usher.IssueInvitationAsync(operatorId, lifetime.Token);
                invitationDeadlines.Add(invitation.ExpiresAt);
                string shareLink = encoder.Encode(invitation, fingerprint);

                Console.Error.WriteLine($"[puppeteer] Invitation {i}/{count} issued, expires at {invitation.ExpiresAt:o}");
                Console.WriteLine();
                Console.WriteLine($"----- BEGIN SHARE-LINK {i}/{count} -----");
                Console.WriteLine(shareLink);
                Console.WriteLine($"----- END SHARE-LINK {i}/{count} -----");
                Console.WriteLine();
            }

            Console.Error.WriteLine($"[puppeteer] Waiting for {count} handshake(s) to complete (Ctrl+C to abort)...");

            // --- Wait for completions ---

            DateTime latestDeadline = invitationDeadlines.Max();
            for (int i = 1; i <= count; i++)
            {
                TimeSpan budget = latestDeadline - DateTime.UtcNow;
                if (budget <= TimeSpan.Zero) budget = TimeSpan.FromSeconds(1);

                using var perWait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                perWait.CancelAfter(budget);

                try
                {
                    MembershipRecord rec = await Task.Run(() =>
                        completionQueue.Take(perWait.Token), perWait.Token);
                    Console.Error.WriteLine(
                        $"[puppeteer] Handshake {i}/{count} completed: stageId={rec.StageId} device='{rec.DeviceName}'");
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine($"[puppeteer] Aborted while waiting for handshake {i}/{count}.");
                    return 2;
                }
            }

            // The completion signal fires at the journal-commit point inside
            // Usher.HandleSingleOnboardingAsync — but the F5 UsherJoinResponse
            // is sent AFTER that, a few lines later. If we exit immediately
            // here, `await using var usher` disposes the Usher and cancels
            // its background listener tasks before they finish sending F5
            // to the Stage, leaving the joiner hung forever waiting for the
            // response that was already journaled.
            //
            // Drain for a few seconds so in-flight F5 sends complete. A
            // localhost-mapped POST inside Docker is sub-second; 5 s is a
            // generous safety margin.
            Console.Error.WriteLine("[puppeteer] All handshakes journal-committed. Draining F5 responses...");
            await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
            Console.Error.WriteLine("[puppeteer] Exiting.");
            return 0;
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine($"Error: {message}");
            return 1;
        }

        // ------------------------------ show entry ----------------------------
        //
        // Lee el journal de un actor de disco SIN cargar las LibraryAssemblies
        // de dominio (no rehidrata). Solo el path read-only de
        // IActorIntrospection.ShowEntry se ejercita. La salida es TOON puro a
        // stdout, diagnostics a stderr — apto para pipear a otra herramienta o
        // a un chat con la IA.
        //
        // Naming: `show entry <id>` (no `show-entry`) para que future verbs
        // bajo `show` (range, find, describe, branch ...) escalen sin renaming.

        private static int ShowCommand(string[] args)
        {
            if (args.Length == 0)
                return Fail("show requires a sub-verb. Today: 'entry' | 'action'. Usage: puppeteer show <verb> <id> --journal <conn> --actor-name <name>");

            switch (args[0])
            {
                case "entry":
                    return ShowEntry(args.Skip(1).ToArray());
                case "action":
                    return ShowAction(args.Skip(1).ToArray());
                default:
                    return Fail($"Unknown 'show' sub-verb: {args[0]}");
            }
        }

        private static int ShowEntry(string[] args)
        {
            if (args.Length == 0)
                return Fail("show entry requires <id>");
            if (!long.TryParse(args[0], out long entryId) || entryId <= 0)
                return Fail($"<id> must be a positive integer; got {args[0]}");

            string journalConnection = null;
            string actorName         = null;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--journal":
                        if (++i >= args.Length) return Fail("--journal requires a connection string");
                        journalConnection = args[i];
                        break;
                    case "--actor-name":
                        if (++i >= args.Length) return Fail("--actor-name requires a name");
                        actorName = args[i];
                        break;
                    default:
                        return Fail($"Unknown flag: {args[i]}");
                }
            }

            if (string.IsNullOrWhiteSpace(journalConnection))
                return Fail("--journal <connection> is required (e.g. \"path=C:\\journals\")");
            if (string.IsNullOrWhiteSpace(actorName))
                return Fail("--actor-name <name> is required");

            try
            {
                // ActorV1 sin librerias — solo abrimos storage para introspeccion.
                // El path read-only no rehidrata, asi que el dominio del actor
                // puede ser desconocido para el binario puppeteer-cli.
                var actor = new ActorV1(actorName);
                actor.ConfigureStorageForIntrospection(DatabaseType.FileSystem, journalConnection);

                string toon = actor.Introspection.ShowEntry(entryId);
                Console.Write(toon);
                return 0;
            }
            catch (LanguageException ex)
            {
                return Fail(ex.Message);
            }
        }

        // ------------------------------ show action ---------------------------
        //
        // Resuelve un actionId al Define entry vigente del journal y lo devuelve
        // como TOON. Misma plumbing read-only que show entry (no rehidrata,
        // no requiere librerias de dominio). Workflow tipico de la IA:
        //   1) show entry 200    -> ve actionId: 42
        //   2) show action 42    -> ve define action 42 (...) as ... end;

        private static int ShowAction(string[] args)
        {
            if (args.Length == 0)
                return Fail("show action requires <actionId>");
            if (!int.TryParse(args[0], out int actionId) || actionId <= 0)
                return Fail($"<actionId> must be a positive integer; got {args[0]}");

            string journalConnection = null;
            string actorName         = null;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--journal":
                        if (++i >= args.Length) return Fail("--journal requires a connection string");
                        journalConnection = args[i];
                        break;
                    case "--actor-name":
                        if (++i >= args.Length) return Fail("--actor-name requires a name");
                        actorName = args[i];
                        break;
                    default:
                        return Fail($"Unknown flag: {args[i]}");
                }
            }

            if (string.IsNullOrWhiteSpace(journalConnection))
                return Fail("--journal <connection> is required (e.g. \"path=C:\\journals\")");
            if (string.IsNullOrWhiteSpace(actorName))
                return Fail("--actor-name <name> is required");

            try
            {
                var actor = new ActorV1(actorName);
                actor.ConfigureStorageForIntrospection(DatabaseType.FileSystem, journalConnection);

                string toon = actor.Introspection.ShowAction(actionId);
                Console.Write(toon);
                return 0;
            }
            catch (LanguageException ex)
            {
                return Fail(ex.Message);
            }
        }
    }

    // Alternate journal writer that pushes each completed onboarding into a
    // shared queue. Lets the CLI wait for N consecutive handshakes against
    // a single Usher instance.
    internal sealed class QueueingJournalWriter : IJournalWriter
    {
        private readonly System.Collections.Concurrent.BlockingCollection<MembershipRecord> sink;
        private long lastEpoch;

        public QueueingJournalWriter(System.Collections.Concurrent.BlockingCollection<MembershipRecord> sink)
        {
            this.sink = sink;
        }

        public Task<long> AppendMembershipAsync(MembershipRecord record, CancellationToken ct)
        {
            long epoch = Interlocked.Increment(ref lastEpoch);
            sink.Add(record, ct);
            return Task.FromResult(epoch);
        }
    }
}
