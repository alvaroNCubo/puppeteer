using System;
using System.IO;
using System.Text;
using Choreography.StageManager;

namespace Choreography.Transport
{
    public enum ForwardCommandType : byte
    {
        Script = 0,
        Action = 1,
        CheckThenCommand = 2
    }

    public sealed class ForwardCommand : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.ForwardCommand;

        public Guid CommandId { get; private set; }
        public ForwardCommandType CommandType { get; private set; }
        public string Script { get; private set; }
        public string SerializedParameters { get; private set; }
        public DateTime OccurredAt { get; private set; }
        public string Ip { get; private set; }
        public string User { get; private set; }
        public int ActionId { get; private set; }
        public string CheckScript { get; private set; }

        public ForwardCommand(PerformerId senderId, Guid commandId, string script, string serializedParameters,
            DateTime occurredAt, string ip, string user)
            : base(senderId)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            CommandId = commandId;
            CommandType = ForwardCommandType.Script;
            Script = script;
            SerializedParameters = serializedParameters ?? string.Empty;
            OccurredAt = occurredAt;
            Ip = ip ?? string.Empty;
            User = user ?? string.Empty;
            ActionId = 0;
        }

        public ForwardCommand(PerformerId senderId, Guid commandId, int actionId, string arguments, string serializedParameters,
            DateTime occurredAt, string ip, string user)
            : base(senderId)
        {
            CommandId = commandId;
            CommandType = ForwardCommandType.Action;
            Script = arguments ?? string.Empty;
            SerializedParameters = serializedParameters ?? string.Empty;
            OccurredAt = occurredAt;
            Ip = ip ?? string.Empty;
            User = user ?? string.Empty;
            ActionId = actionId;
        }

        public ForwardCommand(PerformerId senderId, Guid commandId, string checkScript, string commandScript,
            string serializedParameters, DateTime occurredAt, string ip, string user)
            : base(senderId)
        {
            if (checkScript == null) throw new ArgumentNullException(nameof(checkScript));
            if (commandScript == null) throw new ArgumentNullException(nameof(commandScript));
            CommandId = commandId;
            CommandType = ForwardCommandType.CheckThenCommand;
            Script = commandScript;
            CheckScript = checkScript;
            SerializedParameters = serializedParameters ?? string.Empty;
            OccurredAt = occurredAt;
            Ip = ip ?? string.Empty;
            User = user ?? string.Empty;
            ActionId = 0;
        }

        internal ForwardCommand(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(CommandId.ToByteArray());
            writer.Write((byte)CommandType);

            byte[] scriptBytes = Encoding.UTF8.GetBytes(Script);
            writer.Write(scriptBytes.Length);
            writer.Write(scriptBytes);

            byte[] paramsBytes = Encoding.UTF8.GetBytes(SerializedParameters);
            writer.Write(paramsBytes.Length);
            writer.Write(paramsBytes);

            writer.Write(OccurredAt.ToBinary());

            byte[] ipBytes = Encoding.UTF8.GetBytes(Ip);
            writer.Write(ipBytes.Length);
            writer.Write(ipBytes);

            byte[] userBytes = Encoding.UTF8.GetBytes(User);
            writer.Write(userBytes.Length);
            writer.Write(userBytes);

            writer.Write(ActionId);

            if (CommandType == ForwardCommandType.CheckThenCommand)
            {
                byte[] checkScriptBytes = Encoding.UTF8.GetBytes(CheckScript);
                writer.Write(checkScriptBytes.Length);
                writer.Write(checkScriptBytes);
            }
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            CommandId = new Guid(reader.ReadBytes(16));
            CommandType = (ForwardCommandType)reader.ReadByte();

            int scriptLen = reader.ReadInt32();
            Script = Encoding.UTF8.GetString(reader.ReadBytes(scriptLen));

            int paramsLen = reader.ReadInt32();
            SerializedParameters = Encoding.UTF8.GetString(reader.ReadBytes(paramsLen));

            OccurredAt = DateTime.FromBinary(reader.ReadInt64());

            int ipLen = reader.ReadInt32();
            Ip = Encoding.UTF8.GetString(reader.ReadBytes(ipLen));

            int userLen = reader.ReadInt32();
            User = Encoding.UTF8.GetString(reader.ReadBytes(userLen));

            ActionId = reader.ReadInt32();

            if (CommandType == ForwardCommandType.CheckThenCommand)
            {
                int checkScriptLen = reader.ReadInt32();
                CheckScript = Encoding.UTF8.GetString(reader.ReadBytes(checkScriptLen));
            }
            else
            {
                CheckScript = string.Empty;
            }
        }
    }

    public sealed class CommandResult : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CommandResult;

        public Guid CommandId { get; private set; }
        public string Result { get; private set; }
        public bool Success { get; private set; }

        public CommandResult(PerformerId senderId, Guid commandId, string result, bool success)
            : base(senderId)
        {
            CommandId = commandId;
            Result = result ?? string.Empty;
            Success = success;
        }

        internal CommandResult(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(CommandId.ToByteArray());

            byte[] resultBytes = Encoding.UTF8.GetBytes(Result);
            writer.Write(resultBytes.Length);
            writer.Write(resultBytes);

            writer.Write(Success);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            CommandId = new Guid(reader.ReadBytes(16));

            int resultLen = reader.ReadInt32();
            Result = Encoding.UTF8.GetString(reader.ReadBytes(resultLen));

            Success = reader.ReadBoolean();
        }
    }
}
