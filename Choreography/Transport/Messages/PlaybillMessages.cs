using System;
using System.IO;
using System.Text;
using Choreography.StageManager;
using Puppeteer;

namespace Choreography.Transport
{
    // Phase 5 — Playbill cross-pod replication.
    //
    // Parallel to CueEvent (which replicates a journal record). The Director broadcasts
    // a PlaybillSchemaCue when a new schema is registered or an existing schema is
    // re-registered (idempotent), and a PlaybillCue when a new PlaybillRecord is
    // persisted. The Cast applies it to the local PlaybillStore.

    public sealed class PlaybillSchemaCue : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.PlaybillSchemaCue;

        public string SchemaName { get; private set; }
        public string Declarations { get; private set; }

        public PlaybillSchemaCue(PerformerId senderId, string schemaName, string declarations) : base(senderId)
        {
            if (string.IsNullOrWhiteSpace(schemaName)) throw new ArgumentNullException(nameof(schemaName));
            if (declarations == null) throw new ArgumentNullException(nameof(declarations));
            SchemaName = schemaName;
            Declarations = declarations;
        }

        internal PlaybillSchemaCue(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(SchemaName);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);

            byte[] declBytes = Encoding.UTF8.GetBytes(Declarations);
            writer.Write(declBytes.Length);
            writer.Write(declBytes);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            int nameLen = reader.ReadInt32();
            SchemaName = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));

            int declLen = reader.ReadInt32();
            Declarations = Encoding.UTF8.GetString(reader.ReadBytes(declLen));
        }
    }

    public sealed class PlaybillCue : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.PlaybillCue;

        public long EntryId { get; private set; }
        public string SchemaName { get; private set; }
        public string SerializedParameters { get; private set; }

        public PlaybillCue(PerformerId senderId, long entryId, string schemaName, string serializedParameters) : base(senderId)
        {
            if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
            if (string.IsNullOrWhiteSpace(schemaName)) throw new ArgumentNullException(nameof(schemaName));
            if (serializedParameters == null) throw new ArgumentNullException(nameof(serializedParameters));
            EntryId = entryId;
            SchemaName = schemaName;
            SerializedParameters = serializedParameters;
        }

        internal PlaybillCue(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(EntryId);

            byte[] nameBytes = Encoding.UTF8.GetBytes(SchemaName);
            writer.Write(nameBytes.Length);
            writer.Write(nameBytes);

            byte[] paramsBytes = Encoding.UTF8.GetBytes(SerializedParameters);
            writer.Write(paramsBytes.Length);
            writer.Write(paramsBytes);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            EntryId = reader.ReadInt64();

            int nameLen = reader.ReadInt32();
            SchemaName = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));

            int paramsLen = reader.ReadInt32();
            SerializedParameters = Encoding.UTF8.GetString(reader.ReadBytes(paramsLen));
        }
    }
}
