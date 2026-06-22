using System;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;
using Choreography.Transport;

namespace Choreography.Usher
{
    // Client that runs on the Stage app side BEFORE instantiating the Stage. The
    // reason: the AssignedStageId is determined by the handshake with the Usher
    // (it derives from the local pubkey but only becomes effective once the Usher
    // commits it to the journal). The Stage is constructed afterward with that identity
    // already fixed.
    //
    // It needs:
    //   - A temporary transport with a placeholder PerformerId (so the SMP/
    //     InMemory has something to identify the endpoint while the handshake
    //     lasts). The placeholder Id is discarded when it finishes.
    //   - An IStageKeyGenerator and an IStageSigner: both can be provided by the
    //     same crypto backend (BouncyCastle Ed25519 in production, fakes in tests).
    //   - An IPayloadSealer to open the sealed journal secret from the response.
    // Paper 7 Phase 2: promoted internal→public alongside IStageTransport and
    // Usher. The per-Docker host calls JoinNetworkViaUsherAsync to syndicate
    // before constructing its Stage; lives outside Choreography.dll.
    public static class StageOnboardingClient
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
                // F3: build and send a signed UsherJoinRequest (D7).
                DateTime requestedAt = DateTime.UtcNow;
                var unsigned = new UsherJoinRequest(
                    senderId: transientLocalId,
                    invitationNonce: invitationNonce,
                    stagePublicKey: keys.PublicKey,
                    deviceName: deviceProfile.Name,
                    deviceFingerprint: deviceProfile.Fingerprint,
                    requestedAt: requestedAt,
                    signature: new byte[] { 0 }); // placeholder so the constructor passes
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

                // F5: wait for UsherJoinResponse.
                UsherJoinResponse response = await ReceiveResponseAsync(channel, ct);

                if (!response.Accepted)
                    throw new InvalidOperationException($"Usher rejected join request: {response.RejectionReason}");

                // The Usher derived the StageId with StageIdDerivation.FromPublicKey. Here
                // we recompute it to verify it matches what we received.
                PerformerId expectedId = StageIdDerivation.FromPublicKey(keys.PublicKey);
                if (response.AssignedStageId != expectedId)
                    throw new InvalidOperationException(
                        $"AssignedStageId mismatch: expected {expectedId}, got {response.AssignedStageId}");

                // Open the sealed journal secret.
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
