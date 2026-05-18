using System;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;
using Choreography.Transport;

namespace Choreography.Usher
{
    // Cliente que corre del lado de la app Kora ANTES de instanciar el Stage. La
    // razon: el AssignedStageId viene determinado por el handshake con el Usher
    // (deriva de la pubkey local pero solo se hace efectivo cuando el Usher lo
    // commitea en el journal). El Stage se construye despues con esa identidad ya
    // fija.
    //
    // Necesita:
    //   - Un transporte temporal con un PerformerId placeholder (para que el SMP/
    //     InMemory tenga algo que identificar el endpoint mientras dura el
    //     handshake). El Id placeholder se descarta al terminar.
    //   - Un IStageKeyGenerator y un IStageSigner: ambos pueden ser provistos por el
    //     mismo backend crypto (BouncyCastle Ed25519 en produccion, fakes en tests).
    //   - Un IPayloadSealer para abrir el sealed journal secret de la respuesta.
    // Paper 7 Phase 2: promoted internal→public alongside IStageTransport and
    // Usher. The per-Docker host calls JoinNetworkViaUsherAsync to syndicate
    // before constructing its Stage; lives outside Choreography.dll.
    public static class KoraOnboardingClient
    {
        public static async Task<OnboardedIdentity> JoinNetworkViaUsherAsync(
            ConnectionInvitation usherInvitation,
            Guid invitationNonce,
            DeviceProfile deviceProfile,
            IStageTransport transport,
            IStageKeyGenerator keyGenerator,
            IStageSigner signer,
            IPayloadSealer payloadSealer,
            PerformerId transientLocalId,
            CancellationToken ct)
        {
            if (usherInvitation == null) throw new ArgumentNullException(nameof(usherInvitation));
            if (usherInvitation.Purpose != ChannelPurpose.Usher)
                throw new ArgumentException($"Invitation purpose must be Usher, got {usherInvitation.Purpose}", nameof(usherInvitation));
            if (invitationNonce == Guid.Empty) throw new ArgumentException("InvitationNonce cannot be empty", nameof(invitationNonce));
            if (deviceProfile == null) throw new ArgumentNullException(nameof(deviceProfile));
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            if (keyGenerator == null) throw new ArgumentNullException(nameof(keyGenerator));
            if (signer == null) throw new ArgumentNullException(nameof(signer));
            if (payloadSealer == null) throw new ArgumentNullException(nameof(payloadSealer));

            ct.ThrowIfCancellationRequested();

            StageKeyPair keys = keyGenerator.Generate();
            IStageChannel channel = await transport.AcceptInvitationAsync(usherInvitation);

            try
            {
                // F3: construir y enviar UsherJoinRequest firmado (D7).
                DateTime requestedAt = DateTime.UtcNow;
                var unsigned = new UsherJoinRequest(
                    senderId: transientLocalId,
                    invitationNonce: invitationNonce,
                    stagePublicKey: keys.PublicKey,
                    deviceName: deviceProfile.Name,
                    deviceFingerprint: deviceProfile.Fingerprint,
                    requestedAt: requestedAt,
                    signature: new byte[] { 0 }); // placeholder para que el constructor pase
                byte[] payload = unsigned.BuildSignedPayload();
                byte[] signature = signer.Sign(keys.PrivateKey, payload);

                var request = new UsherJoinRequest(
                    senderId: transientLocalId,
                    invitationNonce: invitationNonce,
                    stagePublicKey: keys.PublicKey,
                    deviceName: deviceProfile.Name,
                    deviceFingerprint: deviceProfile.Fingerprint,
                    requestedAt: requestedAt,
                    signature: signature);

                await channel.SendAsync(request, ct);

                // F5: esperar UsherJoinResponse.
                UsherJoinResponse response = await ReceiveResponseAsync(channel, ct);

                if (!response.Accepted)
                    throw new InvalidOperationException($"Usher rejected join request: {response.RejectionReason}");

                // El Usher derivo el StageId con StageIdDerivation.FromPublicKey. Aqui
                // recomputamos para verificar que coincide con lo que recibimos.
                PerformerId expectedId = StageIdDerivation.FromPublicKey(keys.PublicKey);
                if (response.AssignedStageId != expectedId)
                    throw new InvalidOperationException(
                        $"AssignedStageId mismatch: expected {expectedId}, got {response.AssignedStageId}");

                // Abrir el sealed journal secret.
                byte[] journalSecret = payloadSealer.Open(
                    response.SealedJournalSecret, keys.PublicKey, keys.PrivateKey);

                return new OnboardedIdentity(
                    assignedStageId: response.AssignedStageId,
                    keyPair: keys,
                    journalSecret: journalSecret,
                    trustedSmpServers: response.TrustedSmpServers,
                    journalEpochAtJoin: response.JournalEpochAtJoin);
            }
            finally
            {
                await channel.DisposeAsync();
            }
        }

        private static async Task<UsherJoinResponse> ReceiveResponseAsync(IStageChannel channel, CancellationToken ct)
        {
            await foreach (var msg in channel.Receive(ct))
            {
                if (msg is UsherJoinResponse resp) return resp;
                throw new InvalidOperationException($"Expected UsherJoinResponse, got {msg.MessageType}");
            }
            throw new InvalidOperationException("Channel closed before UsherJoinResponse arrived");
        }
    }
}
