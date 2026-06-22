using System;
using System.IO;
using System.Text;
using Choreography.StageManager;

namespace Choreography.Transport
{
    // Bug 18 — Failover replication gap after role rotation.
    //
    // After a role rotation due to failover (Director silent + auto-election of the
    // Cast + term-first demote of the ex-Director), the state machine reconciles roles
    // over the (surviving) Coordination bus but the Replication/Command data channels
    // are left dead: the new Director has no CastLink toward the new Cast, and the
    // new Cast has no DirectorLink toward the new Director. In production each Stage
    // lives in a distinct process/device, so the host cannot repeat the pairing
    // out-of-band (there is no way to cross the new ConnectionInvitation between
    // processes).
    //
    // The in-band re-handshake uses the only live channel (Coordination) to
    // carry the Address of the new invitations:
    //
    //   1) The new Cast, upon adopting a new Director (rotation detected), sends
    //      RehandshakeRequest(LastKnownEntryId) to the Director over Coordination.
    //   2) The Director creates Replication+Command invitations, responds with
    //      RehandshakeProposal(replicationAddress, commandAddress) over Coordination,
    //      waits for the connection, does AcceptCastConnection and a catch-up from
    //      LastKnownEntryId.
    //   3) The Cast reconstructs the ConnectionInvitation from the Address, accepts
    //      them and does ConnectToDirector.
    //
    // ConnectionInvitation is fully reconstructible from (InviterId, Purpose,
    // Address) — InviterId is the SenderId of the proposal, Purpose is fixed per field, and
    // Address encapsulates everything AcceptInvitationAsync needs (verified in
    // InMemoryTransport and HttpsTransport). That is why only the two Address travel.

    public sealed class RehandshakeRequest : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.RehandshakeRequest;

        // Highest journal EntryId of the Cast at the time of requesting the re-handshake.
        // The Director uses it as peerLastEntryId for the catch-up: it sends the entries
        // > LastKnownEntryId that the Cast does not yet have after the rotation.
        public long LastKnownEntryId { get; private set; }

        public RehandshakeRequest(PerformerId senderId, long lastKnownEntryId) : base(senderId)
        {
            LastKnownEntryId = lastKnownEntryId;
        }

        internal RehandshakeRequest(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(LastKnownEntryId);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LastKnownEntryId = reader.ReadInt64();
        }
    }

    public sealed class RehandshakeProposal : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.RehandshakeProposal;

        // Address of the invitations just created by the Director. The Cast
        // reconstructs ConnectionInvitation(SenderId, Replication/Command, Address)
        // and accepts them to open the new data channels.
        public string ReplicationAddress { get; private set; }
        public string CommandAddress { get; private set; }

        public RehandshakeProposal(PerformerId senderId, string replicationAddress, string commandAddress)
            : base(senderId)
        {
            if (string.IsNullOrWhiteSpace(replicationAddress)) throw new ArgumentNullException(nameof(replicationAddress));
            if (string.IsNullOrWhiteSpace(commandAddress)) throw new ArgumentNullException(nameof(commandAddress));
            ReplicationAddress = replicationAddress;
            CommandAddress = commandAddress;
        }

        internal RehandshakeProposal(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(ReplicationAddress);
            writer.Write(CommandAddress);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            ReplicationAddress = reader.ReadString();
            CommandAddress = reader.ReadString();
        }
    }
}
