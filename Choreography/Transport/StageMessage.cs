using System;
using System.IO;
using System.IO.Hashing;
using System.Text;
using Choreography.StageManager;

namespace Choreography.Transport
{
    public enum StageMessageType : byte
    {
        CastingPropose = 1,
        CastingAccept = 2,
        CastingReject = 3,
        DirectorAnnounce = 4,
        Heartbeat = 5,

        CueEvent = 10,
        CueAck = 11,

        ForwardCommand = 20,
        CommandResult = 21,

        RehearsalRequest = 30,
        RehearsalBegin = 31,
        RehearsalChunk = 32,
        RehearsalEnd = 33,
        RehearsalAck = 34,

        MemberJoin = 40,
        MemberJoinAck = 41,
        MemberList = 42,
        MemberExpel = 43,
        MemberLeave = 44,

        // 45 retired (was ActionDefinition; dropped in Phase 6 of the Action
        // refactor — Define records now ride the journal via CueEvent like any
        // other journal entry).

        UsherForward = 50,
        UsherResponse = 51,

        // Fase 5 — Playbill cross-pod replication. Paralelo a CueEvent pero
        // sobre el PlaybillStore del actor en lugar del journal.
        PlaybillSchemaCue = 60,
        PlaybillCue = 61,

        // Bug 18 — Failover replication gap. Re-handshake in-band de los data
        // channels sobre el bus de Coordination tras una rotacion de roles.
        // Ver Choreography/Transport/Messages/RehandshakeMessages.cs.
        RehandshakeRequest = 70,
        RehandshakeProposal = 71
    }

    public abstract class StageMessage
    {
        public PerformerId SenderId { get; }
        public DateTime Timestamp { get; }

        protected StageMessage(PerformerId senderId, DateTime timestamp)
        {
            SenderId = senderId;
            Timestamp = timestamp;
        }

        protected StageMessage(PerformerId senderId) : this(senderId, DateTime.UtcNow) { }

        public abstract StageMessageType MessageType { get; }

        protected abstract void WritePayload(BinaryWriter writer);
        protected abstract void ReadPayload(BinaryReader reader);

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            writer.Write((byte)MessageType);
            writer.Write(SenderId.ToBytes());
            writer.Write(Timestamp.ToBinary());

            long payloadLengthPos = ms.Position;
            writer.Write((int)0);

            long payloadStart = ms.Position;
            WritePayload(writer);
            long payloadEnd = ms.Position;

            int payloadLength = (int)(payloadEnd - payloadStart);
            ms.Position = payloadLengthPos;
            writer.Write(payloadLength);
            ms.Position = payloadEnd;

            byte[] dataForCrc = ms.ToArray();
            var crc = new Crc32();
            crc.Append(dataForCrc);
            writer.Write(crc.GetCurrentHashAsUInt32());

            return ms.ToArray();
        }

        public static StageMessage Deserialize(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < 29 + 4) throw new ArgumentException("Message too short");

            var crc = new Crc32();
            crc.Append(data.AsSpan(0, data.Length - 4));
            uint expectedCrc = crc.GetCurrentHashAsUInt32();

            uint actualCrc = BitConverter.ToUInt32(data, data.Length - 4);
            if (expectedCrc != actualCrc)
                throw new InvalidOperationException("CRC mismatch in StageMessage");

            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            var messageType = (StageMessageType)reader.ReadByte();
            var senderId = PerformerId.From(reader.ReadBytes(16));
            var timestamp = DateTime.FromBinary(reader.ReadInt64());
            int payloadLength = reader.ReadInt32();

            StageMessage message = CreateMessage(messageType, senderId, timestamp);
            message.ReadPayload(reader);

            return message;
        }

        private static StageMessage CreateMessage(StageMessageType type, PerformerId senderId, DateTime timestamp)
        {
            return type switch
            {
                StageMessageType.CastingPropose => new CastingPropose(senderId, timestamp),
                StageMessageType.CastingAccept => new CastingAccept(senderId, timestamp),
                StageMessageType.CastingReject => new CastingReject(senderId, timestamp),
                StageMessageType.DirectorAnnounce => new DirectorAnnounce(senderId, timestamp),
                StageMessageType.Heartbeat => new Heartbeat(senderId, timestamp),
                StageMessageType.CueEvent => new CueEvent(senderId, timestamp),
                StageMessageType.CueAck => new CueAck(senderId, timestamp),
                StageMessageType.ForwardCommand => new ForwardCommand(senderId, timestamp),
                StageMessageType.CommandResult => new CommandResult(senderId, timestamp),
                StageMessageType.RehearsalRequest => new RehearsalRequest(senderId, timestamp),
                StageMessageType.RehearsalBegin => new RehearsalBegin(senderId, timestamp),
                StageMessageType.RehearsalChunk => new RehearsalChunk(senderId, timestamp),
                StageMessageType.RehearsalEnd => new RehearsalEnd(senderId, timestamp),
                StageMessageType.RehearsalAck => new RehearsalAck(senderId, timestamp),
                StageMessageType.MemberJoin => new MemberJoin(senderId, timestamp),
                StageMessageType.MemberJoinAck => new MemberJoinAck(senderId, timestamp),
                StageMessageType.MemberList => new MemberListMessage(senderId, timestamp),
                StageMessageType.MemberExpel => new MemberExpel(senderId, timestamp),
                StageMessageType.MemberLeave => new MemberLeave(senderId, timestamp),
                StageMessageType.UsherForward => new UsherJoinRequest(senderId, timestamp),
                StageMessageType.UsherResponse => new UsherJoinResponse(senderId, timestamp),
                StageMessageType.PlaybillSchemaCue => new PlaybillSchemaCue(senderId, timestamp),
                StageMessageType.PlaybillCue => new PlaybillCue(senderId, timestamp),
                StageMessageType.RehandshakeRequest => new RehandshakeRequest(senderId, timestamp),
                StageMessageType.RehandshakeProposal => new RehandshakeProposal(senderId, timestamp),
                _ => throw new InvalidOperationException($"Unknown message type: {type}")
            };
        }
    }
}
