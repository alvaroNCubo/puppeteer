using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;
using Choreography.Transport;
using Puppeteer;

namespace Choreography.Usher
{
    // The Usher is the ContactSecret piece that syndicates new Stages into the network. It is NOT
    // a consensus peer (D2): it does not participate in quorum, it only gatekeeps entry and appends
    // MembershipRecord to the journal via IJournalWriter.
    //
    // Per-onboarding lifecycle:
    //   1. IssueInvitationAsync -> emits a ConnectionInvitation and persists it as a
    //      PendingInvitation with a nonce and TTL. The caller renders the invitation
    //      as a QR (D3: 1 QR = 1 Stage, fresh queue each time).
    //   2. RunOnboardingLoopAsync -> listens for incoming connections on each live
    //      PendingInvitation. When a Stage connects, it receives UsherJoinRequest, requests approval
    //      (D4 manual), commits MembershipRecord, seals the journal secret to the received
    //      pubkey, sends UsherJoinResponse, closes the channel and discards the in-memory
    //      pubkey.
    //
    // Security invariants:
    //   - The QR nonce must match the corresponding PendingInvitation. Without a
    //     match, the request is rejected (without disclosing to the Stage whether it was due to an
    //     invalid-nonce or an expired-invitation — only "rejected").
    //   - The JoinRequest signature (D7) must verify against the sent pubkey. If it does not
    //     verify, reject.
    //   - The MembershipRecord StageId = StageIdDerivation.FromPublicKey(pubkey). The
    //     Usher recomputes it, it does not trust the Stage to assign its own Id.
    //   - After writing the MembershipRecord, the Usher does NOT retain the pubkey in memory
    //     (D5). The pubkey lives in the journal; the Usher state only carries the
    //     StageId (hash) in logs/audit if it wanted to.
    // Paper 7 Phase 2: promoted internal→public alongside IStageTransport and
    // StageOnboardingClient (option (a) of the original scaffold note). The CLI
    // that emits invitations and the per-Docker host that joins via Usher live
    // outside Choreography.dll; both consume this class through its public API.
    public sealed class Usher : IAsyncDisposable
    {
        private readonly IStageTransport transport;
        private readonly IUsherInvitationStore invitationStore;
        private readonly IUsherApprovalQueue approvalQueue;
        private readonly IJournalWriter journalWriter;
        private readonly IPayloadSealer payloadSealer;
        private readonly IStageSignatureVerifier signatureVerifier;
        private readonly Func<byte[]> journalSecretProvider;
        private readonly Func<ServerFingerprintWire[]> trustedSmpServersProvider;
        private readonly PerformerId localId;
        private readonly TimeSpan defaultTtl;
        private readonly IPuppeteerLogger logger;

        private readonly List<CancellationTokenSource> backgroundTasks = new();
        private readonly object backgroundTasksLock = new object();

        // Old overload (without logger): existing callers (Paper7 CLI) still use
        // it. Delegates to the new overload with a default ConsoleLogger.
        public Usher(
            PerformerId localId,
            IStageTransport transport,
            IUsherInvitationStore invitationStore,
            IUsherApprovalQueue approvalQueue,
            IJournalWriter journalWriter,
            IPayloadSealer payloadSealer,
            IStageSignatureVerifier signatureVerifier,
            Func<byte[]> journalSecretProvider,
            Func<ServerFingerprintWire[]> trustedSmpServersProvider,
            TimeSpan defaultTtl)
            : this(localId, transport, invitationStore, approvalQueue, journalWriter,
                   payloadSealer, signatureVerifier, journalSecretProvider,
                   trustedSmpServersProvider, defaultTtl, new Puppeteer.EventSourcing.ConsoleLogger())
        {
        }

        public Usher(
            PerformerId localId,
            IStageTransport transport,
            IUsherInvitationStore invitationStore,
            IUsherApprovalQueue approvalQueue,
            IJournalWriter journalWriter,
            IPayloadSealer payloadSealer,
            IStageSignatureVerifier signatureVerifier,
            Func<byte[]> journalSecretProvider,
            Func<ServerFingerprintWire[]> trustedSmpServersProvider,
            TimeSpan defaultTtl,
            IPuppeteerLogger logger)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            if (invitationStore == null) throw new ArgumentNullException(nameof(invitationStore));
            if (approvalQueue == null) throw new ArgumentNullException(nameof(approvalQueue));
            if (journalWriter == null) throw new ArgumentNullException(nameof(journalWriter));
            if (payloadSealer == null) throw new ArgumentNullException(nameof(payloadSealer));
            if (signatureVerifier == null) throw new ArgumentNullException(nameof(signatureVerifier));
            if (journalSecretProvider == null) throw new ArgumentNullException(nameof(journalSecretProvider));
            if (trustedSmpServersProvider == null) throw new ArgumentNullException(nameof(trustedSmpServersProvider));
            if (defaultTtl <= TimeSpan.Zero) throw new ArgumentException("DefaultTtl must be positive", nameof(defaultTtl));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            this.localId = localId;
            this.transport = transport;
            this.invitationStore = invitationStore;
            this.approvalQueue = approvalQueue;
            this.journalWriter = journalWriter;
            this.payloadSealer = payloadSealer;
            this.signatureVerifier = signatureVerifier;
            this.journalSecretProvider = journalSecretProvider;
            this.trustedSmpServersProvider = trustedSmpServersProvider;
            this.defaultTtl = defaultTtl;
            this.logger = logger;
        }

        // F1: emits a fresh invitation. Each call creates a nonce and a dedicated queue
        // (D3). The TTL bounds how long the operator can keep the QR displayed on
        // screen before it is invalidated.
        public async Task<UsherInvitation> IssueInvitationAsync(
            OperatorId issuedBy,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var transportInvitation = await transport.CreateInvitationAsync(ChannelPurpose.Usher);
            var nonce = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var pending = new PendingInvitation(
                nonce: nonce,
                issuedBy: issuedBy,
                issuedAt: now,
                expiresAt: now.Add(defaultTtl),
                transportInvitation: transportInvitation);

            await invitationStore.SaveAsync(pending, ct);

            // Starts the listener for this invitation in the background. Each invitation
            // waits for ONE single connection (D3); when it arrives or when the TTL expires, the
            // listener ends.
            StartListenerForInvitation(pending);

            return new UsherInvitation(nonce, transportInvitation, pending.ExpiresAt);
        }

        private void StartListenerForInvitation(PendingInvitation pending)
        {
            var cts = new CancellationTokenSource(pending.ExpiresAt - DateTime.UtcNow);
            lock (backgroundTasksLock) backgroundTasks.Add(cts);
            _ = Task.Run(async () =>
            {
                try
                {
                    IStageChannel channel = await transport.WaitForConnectionAsync(pending.TransportInvitation, cts.Token);
                    await HandleSingleOnboardingAsync(channel, pending, cts.Token);
                }
                catch (OperationCanceledException) { /* expired with no joiner */ }
                catch (Exception ex)
                {
                    logger.Error($"[Usher] Onboarding listener failed for nonce {pending.Nonce}", ex);
                }
            }, cts.Token);
        }

        private async Task HandleSingleOnboardingAsync(
            IStageChannel channel,
            PendingInvitation pending,
            CancellationToken ct)
        {
            try
            {
                UsherJoinRequest request = await ReceiveJoinRequestAsync(channel, ct);

                // Request validations before requesting approval: nonce match,
                // invitation not expired, valid signature.
                if (request.InvitationNonce != pending.Nonce)
                {
                    await RejectAsync(channel, "Nonce mismatch", pending, ct);
                    return;
                }
                if (pending.IsExpired(DateTime.UtcNow))
                {
                    await RejectAsync(channel, "Invitation expired", pending, ct);
                    return;
                }
                bool sigOk = signatureVerifier.Verify(
                    request.StagePublicKey, request.BuildSignedPayload(), request.Signature);
                if (!sigOk)
                {
                    await RejectAsync(channel, "Signature invalid", pending, ct);
                    return;
                }

                // F4: human approval in ContactSecret.
                UsherApprovalDecision decision = await approvalQueue.RequestApprovalAsync(request, pending, ct);
                if (!decision.IsApproved)
                {
                    await RejectAsync(channel, decision.RejectionReason, pending, ct);
                    return;
                }

                // Commit of the MembershipRecord. The StageId is derived from the pubkey (D5).
                PerformerId assignedId = StageIdDerivation.FromPublicKey(request.StagePublicKey);
                var membership = new MembershipRecord(
                    stageId: assignedId,
                    stagePublicKey: request.StagePublicKey,
                    deviceName: request.DeviceName,
                    approvedBy: decision.ApprovedBy,
                    approvedAt: DateTime.UtcNow);

                long epoch = await journalWriter.AppendMembershipAsync(membership, ct);

                // F5: response signed with the journal secret sealed to the pubkey.
                byte[] sealed_ = payloadSealer.Seal(journalSecretProvider(), request.StagePublicKey);
                ServerFingerprintWire[] fingerprints = trustedSmpServersProvider();
                var response = new UsherJoinResponse(
                    senderId: localId,
                    assignedStageId: assignedId,
                    sealedJournalSecret: sealed_,
                    trustedSmpServers: fingerprints,
                    journalEpochAtJoin: epoch);

                // Mark Consumed BEFORE the SendAsync. Reasons:
                //
                // (a) Point of no return: the MembershipRecord was already committed
                //     to the journal in AppendMembershipAsync. The Stage identity
                //     exists in the cluster — the local "invitation
                //     consumed" mark is Usher bookkeeping, it does not affect the Stage.
                //
                // (b) Avoids the race observable by the caller: if the Usher does
                //     SendAsync first, the Stage receives F5 OK and JoinNetworkViaUsherAsync
                //     returns. If the Usher-side caller queries the store
                //     (test, operator panel, audit log) before MarkConsumedAsync
                //     finishes, it sees the invitation as Pending — this was what
                //     broke UsherOnboardingTests.EndToEnd_RealCryptoOverRealTls_RoundsTripIdentity
                //     in CI (slow VM). With this order, when F5 goes out on the wire the
                //     invitation is already Consumed; there is no window.
                //
                // (c) Trade-off: if MarkConsumedAsync fails here (e.g. DB down),
                //     the F5 is never sent; the Stage times out and retries with
                //     another invitation. It is the same failure-mode as the
                //     previous order when SendAsync failed — the invitation was left
                //     in an intermediate state. The new variant makes the test race
                //     disappear without worsening the delivery contract.
                pending.MarkConsumed();
                await invitationStore.MarkConsumedAsync(pending.Nonce, ct);

                await channel.SendAsync(response, ct);

                // D5: discard the pubkey from the local scope. The local variable goes out of
                // scope on return; we do not persist it in any Usher field.
            }
            finally
            {
                try { await channel.DisposeAsync(); } catch { }
            }
        }

        private static async Task<UsherJoinRequest> ReceiveJoinRequestAsync(IStageChannel channel, CancellationToken ct)
        {
            await foreach (var msg in channel.Receive(ct))
            {
                if (msg is UsherJoinRequest req) return req;
                throw new InvalidOperationException($"Expected UsherJoinRequest, got {msg.MessageType}");
            }
            throw new InvalidOperationException("Channel closed before UsherJoinRequest arrived");
        }

        private async Task RejectAsync(IStageChannel channel, string reason, PendingInvitation pending, CancellationToken ct)
        {
            var response = UsherJoinResponse.Rejected(localId, reason);
            try { await channel.SendAsync(response, ct); } catch { }
            try { pending.MarkRejected(reason); } catch { }
            try { await invitationStore.MarkRejectedAsync(pending.Nonce, reason, ct); } catch { }
        }

        public ValueTask DisposeAsync()
        {
            lock (backgroundTasksLock)
            {
                foreach (var cts in backgroundTasks)
                {
                    try { cts.Cancel(); cts.Dispose(); } catch { }
                }
                backgroundTasks.Clear();
            }
            return ValueTask.CompletedTask;
        }
    }
}
