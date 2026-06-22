using System;
using System.IO;
using Choreography.StageManager;

namespace Choreography.Transport
{
    // UsherForward (50): Stage -> Usher. Self-introduction of the new Stage after
    // accepting the QR invitation. Carries the pubkey with which the Usher seals the
    // JournalSecret, and the request signature (D7) for non-repudiation auditing.
    public sealed class UsherJoinRequest : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.UsherForward;

        public Guid InvitationNonce { get; private set; }
        public byte[] StagePublicKey { get; private set; }
        public string DeviceName { get; private set; }
        public string DeviceFingerprint { get; private set; }
        public DateTime RequestedAt { get; private set; }
        public byte[] Signature { get; private set; }

        public UsherJoinRequest(
            PerformerId senderId,
            Guid invitationNonce,
            byte[] stagePublicKey,
            string deviceName,
            string deviceFingerprint,
            DateTime requestedAt,
            byte[] signature) : base(senderId)
        {
            if (stagePublicKey == null) throw new ArgumentNullException(nameof(stagePublicKey));
            if (stagePublicKey.Length != 32) throw new ArgumentException("StagePublicKey must be 32 bytes Ed25519", nameof(stagePublicKey));
            if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentNullException(nameof(deviceName));
            if (string.IsNullOrWhiteSpace(deviceFingerprint)) throw new ArgumentNullException(nameof(deviceFingerprint));
            if (signature == null) throw new ArgumentNullException(nameof(signature));
            if (signature.Length == 0) throw new ArgumentException("Signature cannot be empty", nameof(signature));
            if (invitationNonce == Guid.Empty) throw new ArgumentException("InvitationNonce cannot be empty", nameof(invitationNonce));

            InvitationNonce = invitationNonce;
            StagePublicKey = (byte[])stagePublicKey.Clone();
            DeviceName = deviceName;
            DeviceFingerprint = deviceFingerprint;
            RequestedAt = requestedAt;
            Signature = (byte[])signature.Clone();
        }

        internal UsherJoinRequest(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        // Signed payload (D7): nonce || pubkey || requestedAt(binary). The request
        // side recomputes it before Sign and the verifier side before Verify. It does
        // not include DeviceName/Fingerprint so that a future change in those UX fields
        // does not break existing signatures.
        public byte[] BuildSignedPayload()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(InvitationNonce.ToByteArray());
            w.Write(StagePublicKey);
            w.Write(RequestedAt.ToBinary());
            return ms.ToArray();
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(InvitationNonce.ToByteArray());
            writer.Write((short)StagePublicKey.Length);
            writer.Write(StagePublicKey);
            writer.Write(DeviceName);
            writer.Write(DeviceFingerprint);
            writer.Write(RequestedAt.ToBinary());
            writer.Write((short)Signature.Length);
            writer.Write(Signature);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            InvitationNonce = new Guid(reader.ReadBytes(16));
            int pubKeyLen = reader.ReadInt16();
            StagePublicKey = reader.ReadBytes(pubKeyLen);
            DeviceName = reader.ReadString();
            DeviceFingerprint = reader.ReadString();
            RequestedAt = DateTime.FromBinary(reader.ReadInt64());
            int sigLen = reader.ReadInt16();
            Signature = reader.ReadBytes(sigLen);
        }
    }

    // UsherResponse (51): Usher -> Stage. Enrollment payload after operator
    // approval and commit of the MembershipRecord to the journal. Does NOT include
    // PeerDirectory (D1): existing peers learn of the new Stage by replicating the
    // journal and publish their own invitations via PeerInvitationRecord (Phase 6, not
    // implemented in this scaffold). Does NOT include DataStar yet: the Director
    // election and the catch-up start later, once the Stage has its identity and
    // infrastructure.
    public sealed class UsherJoinResponse : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.UsherResponse;

        public bool Accepted { get; private set; }
        public string RejectionReason { get; private set; }
        public PerformerId AssignedStageId { get; private set; }
        public byte[] SealedJournalSecret { get; private set; }
        public ServerFingerprintWire[] TrustedSmpServers { get; private set; }
        public long JournalEpochAtJoin { get; private set; }

        public UsherJoinResponse(
            PerformerId senderId,
            PerformerId assignedStageId,
            byte[] sealedJournalSecret,
            ServerFingerprintWire[] trustedSmpServers,
            long journalEpochAtJoin) : base(senderId)
        {
            if (sealedJournalSecret == null) throw new ArgumentNullException(nameof(sealedJournalSecret));
            if (trustedSmpServers == null) throw new ArgumentNullException(nameof(trustedSmpServers));
            if (journalEpochAtJoin < 0) throw new ArgumentException("JournalEpochAtJoin must be non-negative", nameof(journalEpochAtJoin));

            Accepted = true;
            RejectionReason = string.Empty;
            AssignedStageId = assignedStageId;
            SealedJournalSecret = (byte[])sealedJournalSecret.Clone();
            TrustedSmpServers = (ServerFingerprintWire[])trustedSmpServers.Clone();
            JournalEpochAtJoin = journalEpochAtJoin;
        }

        public static UsherJoinResponse Rejected(PerformerId senderId, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));
            var r = new UsherJoinResponse(senderId, DateTime.UtcNow)
            {
                Accepted = false,
                RejectionReason = reason,
                AssignedStageId = default,
                SealedJournalSecret = Array.Empty<byte>(),
                TrustedSmpServers = Array.Empty<ServerFingerprintWire>(),
                JournalEpochAtJoin = 0
            };
            return r;
        }

        internal UsherJoinResponse(PerformerId senderId, DateTime ts) : base(senderId, ts)
        {
            RejectionReason = string.Empty;
            SealedJournalSecret = Array.Empty<byte>();
            TrustedSmpServers = Array.Empty<ServerFingerprintWire>();
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(Accepted);
            writer.Write(RejectionReason ?? string.Empty);
            writer.Write(AssignedStageId.ToBytes());
            writer.Write(SealedJournalSecret.Length);
            writer.Write(SealedJournalSecret);
            writer.Write(TrustedSmpServers.Length);
            foreach (var fp in TrustedSmpServers)
            {
                writer.Write(fp.Host);
                writer.Write(fp.Port);
                writer.Write((short)fp.Sha256.Length);
                writer.Write(fp.Sha256);
            }
            writer.Write(JournalEpochAtJoin);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            Accepted = reader.ReadBoolean();
            RejectionReason = reader.ReadString();
            AssignedStageId = PerformerId.From(reader.ReadBytes(16));
            int sealedLen = reader.ReadInt32();
            SealedJournalSecret = reader.ReadBytes(sealedLen);
            int fpCount = reader.ReadInt32();
            TrustedSmpServers = new ServerFingerprintWire[fpCount];
            for (int i = 0; i < fpCount; i++)
            {
                string host = reader.ReadString();
                int port = reader.ReadInt32();
                int sha256Len = reader.ReadInt16();
                byte[] sha256 = reader.ReadBytes(sha256Len);
                TrustedSmpServers[i] = new ServerFingerprintWire(host, port, sha256);
            }
            JournalEpochAtJoin = reader.ReadInt64();
        }
    }

    // Flat representation of ServerFingerprint for wire serialization. It lives
    // here (in Transport) instead of in the Usher namespace to keep the separation:
    // the wire is Transport, the semantics are Usher.
    public sealed class ServerFingerprintWire
    {
        public string Host { get; }
        public int Port { get; }
        public byte[] Sha256 { get; }

        public ServerFingerprintWire(string host, int port, byte[] sha256)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentNullException(nameof(host));
            if (port <= 0 || port > 65535) throw new ArgumentException("Port out of range", nameof(port));
            if (sha256 == null) throw new ArgumentNullException(nameof(sha256));
            if (sha256.Length != 32) throw new ArgumentException("Sha256 fingerprint must be 32 bytes", nameof(sha256));

            Host = host;
            Port = port;
            Sha256 = (byte[])sha256.Clone();
        }
    }
}
