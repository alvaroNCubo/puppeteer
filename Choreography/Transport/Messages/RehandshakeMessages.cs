using System;
using System.IO;
using System.Text;
using Choreography.StageManager;

namespace Choreography.Transport
{
    // Bug 18 — Failover replication gap after role rotation.
    //
    // Tras una rotacion de roles por failover (Director silent + auto-eleccion del
    // Cast + term-first demote del ex-Director), el state machine reconcilia roles
    // sobre el bus de Coordination (sobreviviente) pero los data channels
    // Replication/Command quedan muertos: el nuevo Director no tiene CastLink hacia
    // el nuevo Cast, y el nuevo Cast no tiene DirectorLink hacia el nuevo Director.
    // En produccion cada Stage vive en un proceso/device distinto, asi que el host
    // no puede repetir el pairing out-of-band (no hay como cruzar las nuevas
    // ConnectionInvitation entre procesos).
    //
    // El re-handshake in-band usa el unico canal vivo (Coordination) para
    // transportar las Address de las nuevas invitaciones:
    //
    //   1) El nuevo Cast, al adoptar un nuevo Director (rotacion detectada), envia
    //      RehandshakeRequest(LastKnownEntryId) al Director sobre Coordination.
    //   2) El Director crea invitaciones Replication+Command, responde con
    //      RehandshakeProposal(replicationAddress, commandAddress) sobre Coordination,
    //      espera la conexion, hace AcceptCastConnection y un catch-up desde
    //      LastKnownEntryId.
    //   3) El Cast reconstruye las ConnectionInvitation desde las Address, las
    //      acepta y hace ConnectToDirector.
    //
    // ConnectionInvitation es reconstruible por completo desde (InviterId, Purpose,
    // Address) — InviterId es el SenderId del proposal, Purpose es fija por campo, y
    // Address encapsula todo lo que AcceptInvitationAsync necesita (verificado en
    // InMemoryTransport y HttpsTransport). Por eso solo viajan las dos Address.

    public sealed class RehandshakeRequest : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.RehandshakeRequest;

        // EntryId mas alto del journal del Cast al momento de pedir el re-handshake.
        // El Director lo usa como peerLastEntryId para el catch-up: envia las entries
        // > LastKnownEntryId que el Cast aun no tiene tras la rotacion.
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

        // Address de las invitaciones recien creadas por el Director. El Cast
        // reconstruye ConnectionInvitation(SenderId, Replication/Command, Address)
        // y las acepta para abrir los nuevos data channels.
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
