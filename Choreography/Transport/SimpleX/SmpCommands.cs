using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Choreography.Transport.SimpleX
{
    // SMP v6 wire format (per simplexmq/Protocol.hs encodeTransmission_, transmissionP):
    //
    // BLOCK PAYLOAD (after SmpBlock.Pack length prefix + before padding) is a BATCH
    // (THandleParams.batch = True by default). Format:
    //   [1 byte: count of transmissions]
    //   [Large = Word16 BE length, transmission1 bytes]
    //   [Large = Word16 BE length, transmission2 bytes]
    //   ...
    //
    // Each individual transmission (what goes inside the Large):
    //   signature      (1-byte len + bytes; empty = 0x00 only for no auth)
    //   sessionId      (1-byte len + 32 bytes, captured from the server hello)
    //   corrId         (1-byte len + 24 bytes random)
    //   queueId        (1-byte len + bytes; empty for NEW)
    //   command_body   (depends on the command)
    //
    // signature signs transmission_for_auth = sessionId+corrId+queueId+command_body.
    //
    // NEW v6 command body:  "NEW " + smpEncode(rKey) + smpEncode(dhKey) + auth + smpEncode(subMode)
    //   - rKey/dhKey: 1-byte len + 32 bytes (Ed25519/X25519 raw)
    //   - auth: empty if there is no BasicAuth, "A" + smpEncode(auth) if there is
    //   - subMode: 1 byte 'S' (SMSubscribe) or 'C' (SMOnlyCreate)
    //
    // The ByteString length prefix is **1 byte** (lenEncode = w2c . fromIntegral),
    // not Word16 BE as the old code had. It limits fields to 255 bytes; OK for
    // keys/sigs/ids but NOT for long messages (those use Tail = no prefix, or Large = 2-byte).

    internal enum SmpResponseType : byte
    {
        IDS, OK, MSG, ERR, END
    }

    // --- Requests (client -> server) ---

    internal static class SmpCommandBuilder
    {
        public const int CorrIdSize = 24;

        // NEW v6: creates queue. Signs with the rcvSignSecretKey (freshly generated).
        // Server responds with IDS {rcvId, sndId, srvDhPubKey}.
        public static byte[] BuildNew(byte[] sessionId, byte[] corrId,
            byte[] rcvSignPubKey, byte[] rcvDhPubKey, byte[] rcvSignSecretKey)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (corrId == null || corrId.Length != CorrIdSize) throw new ArgumentException($"corrId must be {CorrIdSize} bytes");
            if (rcvSignPubKey == null) throw new ArgumentNullException(nameof(rcvSignPubKey));
            if (rcvDhPubKey == null) throw new ArgumentNullException(nameof(rcvDhPubKey));
            if (rcvSignSecretKey == null) throw new ArgumentNullException(nameof(rcvSignSecretKey));

            byte[] commandBody = BuildNewBody(rcvSignPubKey, rcvDhPubKey);
            byte[] transmissionForAuth = BuildTransmissionForAuth(sessionId, corrId, queueId: Array.Empty<byte>(), commandBody);
            byte[] signature = SmpCrypto.Sign(transmissionForAuth, rcvSignSecretKey);
            byte[] transmission = PrependByteString(signature, transmissionForAuth);

            return WrapInBatch(transmission);
        }

        private static byte[] BuildNewBody(byte[] rcvSignPubKey, byte[] rcvDhPubKey)
        {
            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("NEW"));
            ms.WriteByte((byte)' ');
            // rKey (Ed25519) and dhKey (X25519) go as ASN.1 DER X.509 SubjectPublicKeyInfo,
            // not as raw 32 bytes. See simplexmq Crypto.hs encodePubKey.
            WriteByteString(ms, SmpCrypto.EncodeEd25519PublicKeyDer(rcvSignPubKey));
            WriteByteString(ms, SmpCrypto.EncodeX25519PublicKeyDer(rcvDhPubKey));
            // auth: no BasicAuth -> emit nothing (no marker, no bytes)
            ms.WriteByte((byte)'S'); // subMode = SMSubscribe (server delivers messages as soon as they arrive)
            return ms.ToArray();
        }

        // PING v6: trivial command with no auth and no queueId. Server responds PONG.
        // Useful to diagnose transmission/block encoding without depending on NEW.
        // Format: signature_empty(0x00) + sessionId + corrId + queueId_empty(0x00) + "PING"
        public static byte[] BuildPing(byte[] sessionId, byte[] corrId)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (corrId == null || corrId.Length != CorrIdSize) throw new ArgumentException($"corrId must be {CorrIdSize} bytes");

            using var bodyMs = new MemoryStream();
            bodyMs.Write(Encoding.ASCII.GetBytes("PING"));
            byte[] commandBody = bodyMs.ToArray();
            byte[] transmissionForAuth = BuildTransmissionForAuth(sessionId, corrId, queueId: Array.Empty<byte>(), commandBody);
            // No signature: prepend empty byteString (0x00 byte)
            byte[] transmission = PrependByteString(Array.Empty<byte>(), transmissionForAuth);
            return WrapInBatch(transmission);
        }

        // Batch envelope per simplexmq Protocol.hs batchTransmissions_:
        //   [1 byte: count] [Word16 BE: len_t1] [t1 bytes] [Word16 BE: len_t2] [t2 bytes] ...
        // For outgoing transmissions from the client, count is always 1.
        private static byte[] WrapInBatch(byte[] transmission)
        {
            int len = transmission.Length;
            if (len > ushort.MaxValue)
                throw new ArgumentException($"Transmission too large for batch Large prefix: {len}");
            byte[] result = new byte[1 + 2 + len];
            result[0] = 1; // count of transmissions
            result[1] = (byte)(len >> 8);   // Word16 BE high
            result[2] = (byte)(len & 0xFF); // Word16 BE low
            Buffer.BlockCopy(transmission, 0, result, 3, len);
            return result;
        }

        // KEY v6: registers senderSignPubKey in the queue so the sender can
        // authorize later commands (SEND). Sent by the recipient (signs with
        // recipientSignSecretKey) targeting its own rcvId. Server responds OK.
        // Format: "KEY " + smpEncode(senderSignPubKey).
        public static byte[] BuildKey(byte[] sessionId, byte[] corrId, byte[] recipientQueueId,
            byte[] senderSignPubKey, byte[] recipientSignSecretKey)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (corrId == null || corrId.Length != CorrIdSize) throw new ArgumentException($"corrId must be {CorrIdSize} bytes");
            if (recipientQueueId == null || recipientQueueId.Length == 0) throw new ArgumentException("recipientQueueId required for KEY");
            if (senderSignPubKey == null) throw new ArgumentNullException(nameof(senderSignPubKey));
            if (recipientSignSecretKey == null) throw new ArgumentNullException(nameof(recipientSignSecretKey));

            using var bodyMs = new MemoryStream();
            bodyMs.Write(Encoding.ASCII.GetBytes("KEY"));
            bodyMs.WriteByte((byte)' ');
            // senderSignPubKey goes as ASN.1 DER X.509 SPKI (same as rKey in NEW)
            WriteByteString(bodyMs, SmpCrypto.EncodeEd25519PublicKeyDer(senderSignPubKey));
            byte[] commandBody = bodyMs.ToArray();

            byte[] transmissionForAuth = BuildTransmissionForAuth(sessionId, corrId, recipientQueueId, commandBody);
            byte[] signature = SmpCrypto.Sign(transmissionForAuth, recipientSignSecretKey);
            byte[] transmission = PrependByteString(signature, transmissionForAuth);
            return WrapInBatch(transmission);
        }

        // SUB v6: subscribe to queue to receive MSGs from the sender.
        // Format: "SUB" (no args). Server responds OK + delivers pending MSGs
        // (server-initiated, requires async pump = Phase 4d).
        public static byte[] BuildSub(byte[] sessionId, byte[] corrId, byte[] recipientQueueId,
            byte[] recipientSignSecretKey)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (corrId == null || corrId.Length != CorrIdSize) throw new ArgumentException($"corrId must be {CorrIdSize} bytes");
            if (recipientQueueId == null || recipientQueueId.Length == 0) throw new ArgumentException("recipientQueueId required for SUB");
            if (recipientSignSecretKey == null) throw new ArgumentNullException(nameof(recipientSignSecretKey));

            byte[] commandBody = Encoding.ASCII.GetBytes("SUB");
            byte[] transmissionForAuth = BuildTransmissionForAuth(sessionId, corrId, recipientQueueId, commandBody);
            byte[] signature = SmpCrypto.Sign(transmissionForAuth, recipientSignSecretKey);
            byte[] transmission = PrependByteString(signature, transmissionForAuth);
            return WrapInBatch(transmission);
        }

        // SEND v6: sender sends an encrypted message to the queue.
        // Format: "SEND " + flags + ' ' + Tail(message). flags '0' (no first msg).
        // Tail = raw bytes until the end of the transmission (no length prefix).
        public static byte[] BuildSend(byte[] sessionId, byte[] corrId, byte[] senderQueueId,
            byte[] encryptedMessage, byte[] senderSignSecretKey)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (corrId == null || corrId.Length != CorrIdSize) throw new ArgumentException($"corrId must be {CorrIdSize} bytes");
            if (senderQueueId == null || senderQueueId.Length == 0) throw new ArgumentException("senderQueueId required for SEND");
            if (encryptedMessage == null) throw new ArgumentNullException(nameof(encryptedMessage));
            if (senderSignSecretKey == null) throw new ArgumentNullException(nameof(senderSignSecretKey));

            using var bodyMs = new MemoryStream();
            bodyMs.Write(Encoding.ASCII.GetBytes("SEND"));
            bodyMs.WriteByte((byte)' ');
            // MsgFlags{notification=False} encoded as "F" per simplexmq Encoding Bool.
            // Parser consumes up to ' ' allowing more future flags; minimal v6 = "F".
            bodyMs.WriteByte((byte)'F');
            bodyMs.WriteByte((byte)' ');
            bodyMs.Write(encryptedMessage); // Tail: raw bytes, no length prefix
            byte[] commandBody = bodyMs.ToArray();

            byte[] transmissionForAuth = BuildTransmissionForAuth(sessionId, corrId, senderQueueId, commandBody);
            byte[] signature = SmpCrypto.Sign(transmissionForAuth, senderSignSecretKey);
            byte[] transmission = PrependByteString(signature, transmissionForAuth);
            return WrapInBatch(transmission);
        }

        // SEND unsigned v6: anonymous sender sends a message to a NOT-secured queue (no KEY issued).
        // Used in the bidirectional handshake for the first envelope before the pubkeys
        // are wired up. Format identical to SEND but with an empty signature (length 0 prefix).
        // The SMP v6 server accepts unsigned SENDs while the queue has not received KEY;
        // post-KEY, every SEND requires a valid signature from the registered sender.
        public static byte[] BuildSendUnsigned(byte[] sessionId, byte[] corrId, byte[] senderQueueId,
            byte[] encryptedMessage)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (corrId == null || corrId.Length != CorrIdSize) throw new ArgumentException($"corrId must be {CorrIdSize} bytes");
            if (senderQueueId == null || senderQueueId.Length == 0) throw new ArgumentException("senderQueueId required for SEND");
            if (encryptedMessage == null) throw new ArgumentNullException(nameof(encryptedMessage));

            using var bodyMs = new MemoryStream();
            bodyMs.Write(Encoding.ASCII.GetBytes("SEND"));
            bodyMs.WriteByte((byte)' ');
            bodyMs.WriteByte((byte)'F');
            bodyMs.WriteByte((byte)' ');
            bodyMs.Write(encryptedMessage);
            byte[] commandBody = bodyMs.ToArray();

            byte[] transmissionForAuth = BuildTransmissionForAuth(sessionId, corrId, senderQueueId, commandBody);
            byte[] transmission = PrependByteString(Array.Empty<byte>(), transmissionForAuth);
            return WrapInBatch(transmission);
        }

        // ACK v6: recipient confirms reception of the message identified by msgId.
        // Format: "ACK " + smpEncode(msgId).
        public static byte[] BuildAck(byte[] sessionId, byte[] corrId, byte[] recipientQueueId,
            byte[] msgId, byte[] recipientSignSecretKey)
        {
            if (sessionId == null) throw new ArgumentNullException(nameof(sessionId));
            if (corrId == null || corrId.Length != CorrIdSize) throw new ArgumentException($"corrId must be {CorrIdSize} bytes");
            if (recipientQueueId == null || recipientQueueId.Length == 0) throw new ArgumentException("recipientQueueId required for ACK");
            if (msgId == null) throw new ArgumentNullException(nameof(msgId));
            if (recipientSignSecretKey == null) throw new ArgumentNullException(nameof(recipientSignSecretKey));

            using var bodyMs = new MemoryStream();
            bodyMs.Write(Encoding.ASCII.GetBytes("ACK"));
            bodyMs.WriteByte((byte)' ');
            WriteByteString(bodyMs, msgId);
            byte[] commandBody = bodyMs.ToArray();

            byte[] transmissionForAuth = BuildTransmissionForAuth(sessionId, corrId, recipientQueueId, commandBody);
            byte[] signature = SmpCrypto.Sign(transmissionForAuth, recipientSignSecretKey);
            byte[] transmission = PrependByteString(signature, transmissionForAuth);
            return WrapInBatch(transmission);
        }

        // --- Helpers ---

        private static byte[] BuildTransmissionForAuth(byte[] sessionId, byte[] corrId, byte[] queueId, byte[] commandBody)
        {
            using var ms = new MemoryStream();
            WriteByteString(ms, sessionId);
            WriteByteString(ms, corrId);
            WriteByteString(ms, queueId);
            ms.Write(commandBody);
            return ms.ToArray();
        }

        private static byte[] PrependByteString(byte[] prefix, byte[] rest)
        {
            byte[] result = new byte[1 + prefix.Length + rest.Length];
            result[0] = (byte)prefix.Length;
            Buffer.BlockCopy(prefix, 0, result, 1, prefix.Length);
            Buffer.BlockCopy(rest, 0, result, 1 + prefix.Length, rest.Length);
            return result;
        }

        // SMP ByteString: 1-byte length prefix + raw bytes (max 255).
        private static void WriteByteString(Stream stream, byte[] data)
        {
            if (data.Length > 255)
                throw new ArgumentException($"ByteString too large for 1-byte length prefix: {data.Length}");
            stream.WriteByte((byte)data.Length);
            if (data.Length > 0)
                stream.Write(data, 0, data.Length);
        }
    }

    // --- Responses (server -> client) ---

    internal abstract class SmpResponse
    {
        public byte[] CorrelationId { get; set; }
        public byte[] QueueId { get; set; }
    }

    internal sealed class SmpIds : SmpResponse
    {
        public byte[] RecipientId { get; set; }
        public byte[] SenderId { get; set; }
        public byte[] ServerDhPublicKey { get; set; }
    }

    internal sealed class SmpOk : SmpResponse { }

    internal sealed class SmpMsg : SmpResponse
    {
        public byte[] MsgId { get; set; }
        public DateTime Timestamp { get; set; }
        public byte[] EncryptedBody { get; set; }
    }

    internal sealed class SmpErr : SmpResponse
    {
        public string ErrorType { get; set; }
    }

    internal sealed class SmpEnd : SmpResponse { }

    internal sealed class SmpPong : SmpResponse { }

    // Parser for server transmissions. Layout v6:
    //   signature (1+bytes; usually empty from the server)
    //   sessionId (1+32)
    //   corrId    (1+24)
    //   queueId   (1+bytes)
    //   command:  "TAG" [' ' arg1 arg2 ...]  or  "TAG" with no args
    internal static class SmpResponseParser
    {
        // Parses ALL transmissions of the batch. The SimpleX server may batch
        // several transmissions in the same TLS block (Protocol.hs batchTransmissions_),
        // typically when an OK response (e.g. to a client ACK) coincides in the
        // I/O window with a pending server-push MSG for the same queue.
        //
        // Historical bug (#2): the parser read "count" but only deserialized the first
        // transmission and discarded the rest, which silently dropped the MSGs
        // batched with an ACK's OK. Since the SMP server delivers one MSG at a time per
        // queue (gated by the client's ACK), losing a MSG left the queue blocked
        // with no visible error. Symptom: the Cast received N entries and then "total
        // silence", with N intermittent depending on the server's batching timing.
        public static IReadOnlyList<SmpResponse> ParseAll(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                throw new ArgumentException("Empty response");

            if (payload.Length < 3) throw new IOException("Batch payload too short");
            int count = payload[0];
            if (count < 1) throw new IOException($"Batch count={count} unexpected");

            var responses = new List<SmpResponse>(count);
            int offset = 1;
            for (int i = 0; i < count; i++)
            {
                if (offset + 2 > payload.Length)
                    throw new IOException($"Batch transmission {i} length prefix truncated at offset {offset} (payload {payload.Length}B)");
                int tLen = (payload[offset] << 8) | payload[offset + 1];
                offset += 2;
                if (tLen < 1 || offset + tLen > payload.Length)
                    throw new IOException($"Batch transmission {i} length {tLen} invalid (offset {offset}, payload {payload.Length}B)");

                responses.Add(ParseSingleTransmission(payload, offset, tLen));
                offset += tLen;
            }
            return responses;
        }

        private static SmpResponse ParseSingleTransmission(byte[] payload, int offset, int tLen)
        {
            using var ms = new MemoryStream(payload, offset, tLen);

            byte[] _signature = ReadByteString(ms);
            byte[] _sessionId = ReadByteString(ms);
            byte[] corrId = ReadByteString(ms);
            byte[] queueId = ReadByteString(ms);

            string tag = ReadCommandTag(ms);

            SmpResponse response = tag switch
            {
                "IDS" => ParseIds(ms),
                "OK" => new SmpOk(),
                "MSG" => ParseMsg(ms),
                "ERR" => ParseErr(ms),
                "END" => new SmpEnd(),
                "PONG" => new SmpPong(),
                _ => throw new InvalidOperationException($"Unknown SMP response tag: '{tag}' (raw transmission prefix: {BitConverter.ToString(payload, offset, Math.Min(40, tLen))})")
            };

            response.CorrelationId = corrId;
            response.QueueId = queueId;
            return response;
        }

        // IDS v6: "IDS " + smpEncode(rcvId) + smpEncode(sndId) + smpEncode(srvDh).
        // No queueMode/linkId/serviceId/etc — those are v9+.
        private static SmpIds ParseIds(MemoryStream ms)
        {
            ConsumeSpace(ms);
            byte[] rcvId = ReadByteString(ms);
            byte[] sndId = ReadByteString(ms);
            byte[] srvDh = ReadByteString(ms);
            return new SmpIds { RecipientId = rcvId, SenderId = sndId, ServerDhPublicKey = srvDh };
        }

        // MSG v6: "MSG " + smpEncode(msgId) + Tail(encrypted body).
        private static SmpMsg ParseMsg(MemoryStream ms)
        {
            ConsumeSpace(ms);
            byte[] msgId = ReadByteString(ms);
            byte[] body = new byte[ms.Length - ms.Position];
            int read = ms.Read(body, 0, body.Length);
            if (read < body.Length) throw new IOException("Truncated MSG body");
            return new SmpMsg { MsgId = msgId, Timestamp = DateTime.UtcNow, EncryptedBody = body };
        }

        // ERR err: "ERR " + err encoding. For now we capture it as an ASCII string.
        private static SmpErr ParseErr(MemoryStream ms)
        {
            ConsumeSpace(ms);
            int remaining = (int)(ms.Length - ms.Position);
            byte[] errBytes = new byte[remaining];
            ms.Read(errBytes, 0, remaining);
            return new SmpErr { ErrorType = Encoding.ASCII.GetString(errBytes).TrimEnd('\0', ' ') };
        }

        private static byte[] ReadByteString(MemoryStream ms)
        {
            int len = ms.ReadByte();
            if (len < 0) throw new IOException("Unexpected end of stream reading length prefix");
            if (len == 0) return Array.Empty<byte>();
            byte[] data = new byte[len];
            int read = ms.Read(data, 0, len);
            if (read < len) throw new IOException($"Truncated byte string: expected {len}, got {read}");
            return data;
        }

        private static string ReadCommandTag(MemoryStream ms)
        {
            var sb = new StringBuilder();
            while (ms.Position < ms.Length)
            {
                int b = ms.ReadByte();
                if (b < 0) break;
                if (b == ' ')
                {
                    // space starts args; rewind so ParseXxx consumes it
                    ms.Position--;
                    break;
                }
                if (!char.IsAsciiLetterUpper((char)b)) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static void ConsumeSpace(MemoryStream ms)
        {
            if (ms.Position < ms.Length && ms.ReadByte() != ' ')
                ms.Position--;
        }
    }
}
