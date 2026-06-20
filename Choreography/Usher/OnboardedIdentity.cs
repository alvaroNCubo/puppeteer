using System;
using Choreography.StageManager;
using Choreography.Transport;

namespace Choreography.Usher
{
    // Lo que el StageOnboardingClient produce tras un handshake exitoso. La app Stage
    // toma estos campos para:
    //   1. Construir el Stage con AssignedStageId
    //   2. Configurar el storage con JournalSecret como clave de encripcion (HKDF
    //      contra contact.enc en la implementacion real)
    //   3. Configurar el transporte con TrustedSmpServers como anclas TOFU
    //   4. Arrancar el catch-up desde JournalEpochAtJoin
    //   5. Conservar StageKeyPair en secure storage (Keychain/Keystore) para firmar
    //      futuros eventos y para desellar los PeerInvitationRecord que peers
    //      existentes publicaran en el journal (Fase 6).
    public sealed class OnboardedIdentity
    {
        public PerformerId AssignedStageId { get; }
        public StageKeyPair KeyPair { get; }
        public byte[] JournalSecret { get; }
        public ServerFingerprintWire[] TrustedSmpServers { get; }
        public long JournalEpochAtJoin { get; }

        public OnboardedIdentity(
            PerformerId assignedStageId,
            StageKeyPair keyPair,
            byte[] journalSecret,
            ServerFingerprintWire[] trustedSmpServers,
            long journalEpochAtJoin)
        {
            if (keyPair == null) throw new ArgumentNullException(nameof(keyPair));
            if (journalSecret == null) throw new ArgumentNullException(nameof(journalSecret));
            if (journalSecret.Length == 0) throw new ArgumentException("JournalSecret cannot be empty", nameof(journalSecret));
            if (trustedSmpServers == null) throw new ArgumentNullException(nameof(trustedSmpServers));
            if (journalEpochAtJoin < 0) throw new ArgumentException("JournalEpochAtJoin must be non-negative", nameof(journalEpochAtJoin));

            AssignedStageId = assignedStageId;
            KeyPair = keyPair;
            JournalSecret = (byte[])journalSecret.Clone();
            TrustedSmpServers = (ServerFingerprintWire[])trustedSmpServers.Clone();
            JournalEpochAtJoin = journalEpochAtJoin;
        }
    }
}
