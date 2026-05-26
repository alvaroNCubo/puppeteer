using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;
using Choreography.Transport;

namespace Choreography.Usher
{
    // El Usher es la pieza de Rubicon que sindicaliza Koras nuevos a la red. NO es
    // peer de consenso (D2): no participa en quorum, solo gatekeepea entry y appendea
    // MembershipRecord al journal via IJournalWriter.
    //
    // Lifecycle por onboarding:
    //   1. IssueInvitationAsync -> emite ConnectionInvitation y la persiste como
    //      PendingInvitation con un nonce y TTL. El caller renderiza la invitacion
    //      como QR (D3: 1 QR = 1 Kora, fresh queue cada vez).
    //   2. RunOnboardingLoopAsync -> escucha conexiones entrantes en cada PendingInvitation
    //      vigente. Cuando un Kora se conecta, recibe UsherJoinRequest, pide aprobacion
    //      (D4 manual), commitea MembershipRecord, sella el journal secret a la pubkey
    //      recibida, manda UsherJoinResponse, cierra el canal y descarta la pubkey
    //      in-memory.
    //
    // Invariantes de seguridad:
    //   - Nonce del QR debe matchear con el PendingInvitation correspondiente. Sin
    //     match, se rechaza el request (sin filtrar a Kora si fue por nonce-invalido o
    //     invitacion-expirada — solo "rejected").
    //   - Firma del JoinRequest (D7) debe verificar contra la pubkey enviada. Si no
    //     verifica, rechazo.
    //   - StageId del MembershipRecord = StageIdDerivation.FromPublicKey(pubkey). El
    //     Usher recomputa, no confia en el Kora para asignarse Id.
    //   - Tras escribir el MembershipRecord, el Usher NO conserva el pubkey en memoria
    //     (D5). El pubkey vive en el journal; el estado del Usher solo lleva el
    //     StageId (hash) en logs/auditoria si quisiera.
    // Paper 7 Phase 2: promoted internal→public alongside IStageTransport and
    // KoraOnboardingClient (option (a) of the original scaffold note). The CLI
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

        private readonly List<CancellationTokenSource> backgroundTasks = new();
        private readonly object backgroundTasksLock = new object();

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
        }

        // F1: emite invitacion fresca. Cada llamada crea un nonce y una queue dedicada
        // (D3). El TTL acota cuanto tiempo el operador puede tener el QR mostrado en
        // pantalla antes de que se invalide.
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

            // Arranca el listener para esta invitacion en background. Cada invitacion
            // espera UNA sola conexion (D3); cuando llega o cuando expira el TTL, el
            // listener termina.
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
                    Console.WriteLine($"[Usher] Onboarding listener failed for nonce {pending.Nonce}: {ex.Message}");
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

                // Validaciones del request antes de pedir aprobacion: nonce match,
                // invitacion no expirada, firma valida.
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

                // F4: aprobacion humana en Rubicon.
                UsherApprovalDecision decision = await approvalQueue.RequestApprovalAsync(request, pending, ct);
                if (!decision.IsApproved)
                {
                    await RejectAsync(channel, decision.RejectionReason, pending, ct);
                    return;
                }

                // Commit del MembershipRecord. El StageId se deriva del pubkey (D5).
                PerformerId assignedId = StageIdDerivation.FromPublicKey(request.StagePublicKey);
                var membership = new MembershipRecord(
                    stageId: assignedId,
                    stagePublicKey: request.StagePublicKey,
                    deviceName: request.DeviceName,
                    approvedBy: decision.ApprovedBy,
                    approvedAt: DateTime.UtcNow);

                long epoch = await journalWriter.AppendMembershipAsync(membership, ct);

                // F5: respuesta firmada con el journal secret sellado a la pubkey.
                byte[] sealed_ = payloadSealer.Seal(journalSecretProvider(), request.StagePublicKey);
                ServerFingerprintWire[] fingerprints = trustedSmpServersProvider();
                var response = new UsherJoinResponse(
                    senderId: localId,
                    assignedStageId: assignedId,
                    sealedJournalSecret: sealed_,
                    trustedSmpServers: fingerprints,
                    journalEpochAtJoin: epoch);

                // Marcar Consumed ANTES del SendAsync. Razones:
                //
                // (a) Punto de no retorno: el MembershipRecord ya fue commit-eado
                //     al journal en AppendMembershipAsync. La identidad del Kora
                //     existe en el cluster — la marca local de "invitation
                //     consumed" es bookkeeping del Usher, no afecta al Kora.
                //
                // (b) Evita el race observable por el caller: si el Usher hace
                //     SendAsync primero, el Kora recibe F5 OK y JoinNetworkViaUsherAsync
                //     retorna. Si el caller del lado Usher consulta el store
                //     (test, panel de operador, audit log) antes de que termine
                //     MarkConsumedAsync, ve la invitacion como Pending — fue lo
                //     que rompio UsherOnboardingTests.EndToEnd_RealCryptoOverRealTls_RoundsTripIdentity
                //     en CI (VM lento). Con este orden, cuando F5 sale al wire la
                //     invitacion ya esta Consumed; no hay ventana.
                //
                // (c) Trade-off: si MarkConsumedAsync falla aqui (ej. DB caida),
                //     el F5 nunca se envia; el Kora hace timeout y reintenta con
                //     otra invitacion. Es el mismo failure-mode que el orden
                //     anterior cuando SendAsync fallaba — la invitacion quedaba
                //     en estado intermedio. La nueva variante hace el race del
                //     test desaparecer sin empeorar el contrato de delivery.
                pending.MarkConsumed();
                await invitationStore.MarkConsumedAsync(pending.Nonce, ct);

                await channel.SendAsync(response, ct);

                // D5: descartar el pubkey del scope local. La variable local sale del
                // scope al retornar; no la persistimos en ningun campo del Usher.
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
