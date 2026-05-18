using System;
using System.IO;
using Choreography.StageManager;

namespace Choreography.Transport
{
    public enum RehearsalMode : byte
    {
        Partial = 0,
        Full = 1
    }

    public enum RehearsalChunkType : byte
    {
        Journal = 0,
        ActionDefs = 1
    }

    public sealed class RehearsalRequest : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.RehearsalRequest;

        public long LastKnownEntryId { get; private set; }
        public RehearsalMode Mode { get; private set; }

        public RehearsalRequest(PerformerId senderId, long lastKnownEntryId, RehearsalMode mode)
            : base(senderId)
        {
            LastKnownEntryId = lastKnownEntryId;
            Mode = mode;
        }

        internal RehearsalRequest(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(LastKnownEntryId);
            writer.Write((byte)Mode);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LastKnownEntryId = reader.ReadInt64();
            Mode = (RehearsalMode)reader.ReadByte();
        }
    }

    public sealed class RehearsalBegin : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.RehearsalBegin;

        public long DirectorMaxEntryId { get; private set; }

        public RehearsalBegin(PerformerId senderId, long directorMaxEntryId) : base(senderId)
        {
            DirectorMaxEntryId = directorMaxEntryId;
        }

        internal RehearsalBegin(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(DirectorMaxEntryId);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            DirectorMaxEntryId = reader.ReadInt64();
        }
    }

    public sealed class RehearsalChunk : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.RehearsalChunk;

        public RehearsalChunkType ChunkType { get; private set; }
        public long FirstEntryId { get; private set; }
        public long LastEntryId { get; private set; }
        public byte[] Data { get; private set; }

        public RehearsalChunk(PerformerId senderId, RehearsalChunkType chunkType,
            long firstEntryId, long lastEntryId, byte[] data)
            : base(senderId)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            ChunkType = chunkType;
            FirstEntryId = firstEntryId;
            LastEntryId = lastEntryId;
            Data = data;
        }

        internal RehearsalChunk(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write((byte)ChunkType);
            writer.Write(FirstEntryId);
            writer.Write(LastEntryId);
            writer.Write(Data.Length);
            writer.Write(Data);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            ChunkType = (RehearsalChunkType)reader.ReadByte();
            FirstEntryId = reader.ReadInt64();
            LastEntryId = reader.ReadInt64();
            int length = reader.ReadInt32();
            Data = reader.ReadBytes(length);
        }
    }

    public sealed class RehearsalEnd : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.RehearsalEnd;

        public RehearsalEnd(PerformerId senderId) : base(senderId) { }
        internal RehearsalEnd(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer) { }
        protected override void ReadPayload(BinaryReader reader) { }
    }

    public sealed class RehearsalAck : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.RehearsalAck;

        public long LastEntryId { get; private set; }

        public RehearsalAck(PerformerId senderId, long lastEntryId) : base(senderId)
        {
            LastEntryId = lastEntryId;
        }

        internal RehearsalAck(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(LastEntryId);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LastEntryId = reader.ReadInt64();
        }
    }
}
