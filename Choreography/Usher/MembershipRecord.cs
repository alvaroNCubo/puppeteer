using System;
using Choreography.StageManager;

namespace Choreography.Usher
{
    // Record that the Usher injects into the journal when a new Stage passes approval.
    // It is the source of truth for membership: existing peers replicate it and from
    // it they know that a new Stage exists with this pubkey, and they emit their
    // peer-to-peer invitations toward it (Phase 6).
    //
    // IMPORTANT (D5): the StagePublicKey is included because peers need it to
    // seal PeerInvitationRecord. The Usher writes it here and discards its
    // in-memory copy; after the commit the Usher does not retain the pubkey. The "source" of
    // the pubkey becomes the replicated journal, not the Usher.
    public sealed class MembershipRecord
    {
        public PerformerId StageId { get; }
        public byte[] StagePublicKey { get; }
        public string DeviceName { get; }
        public OperatorId ApprovedBy { get; }
        public DateTime ApprovedAt { get; }

        public MembershipRecord(
            PerformerId stageId,
            byte[] stagePublicKey,
            string deviceName,
            OperatorId approvedBy,
            DateTime approvedAt)
        {
            if (stagePublicKey == null) throw new ArgumentNullException(nameof(stagePublicKey));
            if (stagePublicKey.Length != 32) throw new ArgumentException("StagePublicKey must be 32 bytes", nameof(stagePublicKey));
            if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentNullException(nameof(deviceName));

            StageId = stageId;
            StagePublicKey = (byte[])stagePublicKey.Clone();
            DeviceName = deviceName;
            ApprovedBy = approvedBy;
            ApprovedAt = approvedAt;
        }
    }
}
