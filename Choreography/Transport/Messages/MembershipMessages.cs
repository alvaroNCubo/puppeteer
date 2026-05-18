using System;
using System.IO;
using System.Text;
using Choreography.StageManager;

namespace Choreography.Transport
{
    public sealed class MemberJoin : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.MemberJoin;

        public MemberJoin(PerformerId senderId) : base(senderId) { }
        internal MemberJoin(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer) { }
        protected override void ReadPayload(BinaryReader reader) { }
    }

    public sealed class MemberJoinAck : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.MemberJoinAck;

        public bool Accepted { get; private set; }

        public MemberJoinAck(PerformerId senderId, bool accepted) : base(senderId)
        {
            Accepted = accepted;
        }

        internal MemberJoinAck(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(Accepted);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            Accepted = reader.ReadBoolean();
        }
    }

    public sealed class MemberListMessage : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.MemberList;

        public byte[] SerializedMembers { get; private set; }

        public MemberListMessage(PerformerId senderId, byte[] serializedMembers) : base(senderId)
        {
            if (serializedMembers == null) throw new ArgumentNullException(nameof(serializedMembers));
            SerializedMembers = serializedMembers;
        }

        internal MemberListMessage(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(SerializedMembers.Length);
            writer.Write(SerializedMembers);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            SerializedMembers = reader.ReadBytes(length);
        }
    }

    public sealed class MemberExpel : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.MemberExpel;

        public PerformerId TargetId { get; private set; }

        public MemberExpel(PerformerId senderId, PerformerId targetId) : base(senderId)
        {
            TargetId = targetId;
        }

        internal MemberExpel(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(TargetId.ToBytes());
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            TargetId = PerformerId.From(reader.ReadBytes(16));
        }
    }

    public sealed class MemberLeave : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.MemberLeave;

        public MemberLeave(PerformerId senderId) : base(senderId) { }
        internal MemberLeave(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer) { }
        protected override void ReadPayload(BinaryReader reader) { }
    }
}
