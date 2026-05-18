using System;
using System.IO;
using Choreography.StageManager;

namespace Choreography.Transport
{
    public sealed class CastingPropose : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CastingPropose;

        public CastingPropose(PerformerId senderId) : base(senderId) { }
        internal CastingPropose(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer) { }
        protected override void ReadPayload(BinaryReader reader) { }
    }

    public sealed class CastingAccept : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CastingAccept;

        public CastingAccept(PerformerId senderId) : base(senderId) { }
        internal CastingAccept(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer) { }
        protected override void ReadPayload(BinaryReader reader) { }
    }

    public sealed class CastingReject : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CastingReject;

        public CastingReject(PerformerId senderId) : base(senderId) { }
        internal CastingReject(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer) { }
        protected override void ReadPayload(BinaryReader reader) { }
    }

    public sealed class DirectorAnnounce : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.DirectorAnnounce;

        public PerformerId DirectorId { get; private set; }

        public DirectorAnnounce(PerformerId senderId, PerformerId directorId) : base(senderId)
        {
            DirectorId = directorId;
        }

        internal DirectorAnnounce(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(DirectorId.ToBytes());
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            DirectorId = PerformerId.From(reader.ReadBytes(16));
        }
    }

    public sealed class Heartbeat : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.Heartbeat;

        public long DirectorMaxEntryId { get; private set; }

        public Heartbeat(PerformerId senderId, long directorMaxEntryId) : base(senderId)
        {
            DirectorMaxEntryId = directorMaxEntryId;
        }

        internal Heartbeat(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(DirectorMaxEntryId);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            DirectorMaxEntryId = reader.ReadInt64();
        }
    }
}
