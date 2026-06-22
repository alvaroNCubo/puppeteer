using System;
using Choreography.StageManager;
using Choreography.Transport;

namespace Choreography.Usher
{
    // What the StageOnboardingClient produces after a successful handshake. The Stage app
    // takes these fields to:
    //   1. Build the Stage with AssignedStageId
    //   2. Configure storage with JournalSecret as the encryption key (HKDF
    //      against contact.enc in the real implementation)
    //   3. Configure the transport with TrustedSmpServers as TOFU anchors
    //   4. Start the catch-up from JournalEpochAtJoin
    //   5. Keep StageKeyPair in secure storage (Keychain/Keystore) to sign
    //      future events and to unseal the PeerInvitationRecord that existing
    //      peers will publish in the journal (Phase 6).
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
