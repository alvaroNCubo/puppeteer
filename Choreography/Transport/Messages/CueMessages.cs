using System;
using System.IO;
using Choreography.StageManager;

namespace Choreography.Transport
{
    public sealed class CueEvent : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CueEvent;

        public long EntryId { get; private set; }
        public byte[] JournalRecord { get; private set; }

        public CueEvent(PerformerId senderId, long entryId, byte[] journalRecord) : base(senderId)
        {
            if (journalRecord == null) throw new ArgumentNullException(nameof(journalRecord));
            EntryId = entryId;
            JournalRecord = journalRecord;
        }

        internal CueEvent(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(EntryId);
            writer.Write(JournalRecord.Length);
            writer.Write(JournalRecord);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            EntryId = reader.ReadInt64();
            int length = reader.ReadInt32();
            JournalRecord = reader.ReadBytes(length);
        }
    }

    public sealed class CueAck : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CueAck;

        public long EntryId { get; private set; }

        public CueAck(PerformerId senderId, long entryId) : base(senderId)
        {
            EntryId = entryId;
        }

        internal CueAck(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(EntryId);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            EntryId = reader.ReadInt64();
        }
    }
}
