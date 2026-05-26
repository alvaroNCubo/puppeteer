using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Choreography.Transport.SimpleX
{
    // SMP v6 wire format (per simplexmq/Protocol.hs encodeTransmission_, transmissionP):
    //
    // BLOCK PAYLOAD (after SmpBlock.Pack length prefix + before padding) es un BATCH
    // (THandleParams.batch = True por default). Format:
    //   [1 byte: count of transmissions]
    //   [Large = Word16 BE length, transmission1 bytes]
    //   [Large = Word16 BE length, transmission2 bytes]
    //   ...
    //
    // Cada transmission individual (lo que va dentro del Large):
    //   signature      (1-byte len + bytes; empty = 0x00 only para no auth)
    //   sessionId      (1-byte len + 32 bytes, capturado del server hello)
    //   corrId         (1-byte len + 24 bytes random)
    //   queueId        (1-byte len + bytes; empty para NEW)
    //   command_body   (depende del comando)
    //
    // signature firma transmission_for_auth = sessionId+corrId+queueId+command_body.
    //
    // NEW v6 command body:  "NEW " + smpEncode(rKey) + smpEncode(dhKey) + auth + smpEncode(subMode)
    //   - rKey/dhKey: 1-byte len + 32 bytes (Ed25519/X25519 raw)
    //   - auth: empty si no hay BasicAuth, "A" + smpEncode(auth) si hay
    //   - subMode: 1 byte 'S' (SMSubscribe) o 'C' (SMOnlyCreate)
    //
    // Length prefix de ByteString es **1 byte** (lenEncode = w2c . fromIntegral),
    // no Word16 BE como tenia el codigo viejo. Limita fields a 255 bytes; OK para
    // keys/sigs/ids pero NO para messages largos (esos usan Tail = sin prefix, o Large = 2-byte).

    internal enum SmpResponseType : byte
    {
        IDS, OK, MSG, ERR, END
    }

    // --- Requests (client -> server) ---

    internal static class SmpCommandBuilder
    {
        public const int CorrIdSize = 24;

        // NEW v6: crea queue. Firma con la rcvSignSecretKey (recientemente generada).
        // Server responde con IDS {rcvId, sndId, srvDhPubKey}.
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
            // rKey (Ed25519) y dhKey (X25519) van como ASN.1 DER X.509 SubjectPublicKeyInfo,
            // no como raw 32 bytes. Ver simplexmq Crypto.hs encodePubKey.
            WriteByteString(ms, SmpCrypto.EncodeEd25519PublicKeyDer(rcvSignPubKey));
            WriteByteString(ms, SmpCrypto.EncodeX25519PublicKeyDer(rcvDhPubKey));
            // auth: no BasicAuth -> emit nothing (no marker, no bytes)
            ms.WriteByte((byte)'S'); // subMode = SMSubscribe (server entrega mensajes apenas llegan)
            return ms.ToArray();
        }

        // PING v6: comando trivial sin auth ni queueId. Server responde PONG.
        // Util para diagnosticar transmission/block encoding sin depender de NEW.
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
        // Para outgoing transmissions desde cliente, count siempre 1.
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

        // KEY v6: registra senderSignPubKey en queue para que el sender pueda
        // autorizar comandos posteriores (SEND). Enviado por recipient (firma con
        // recipientSignSecretKey) targeting su propio rcvId. Server responde OK.
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
            // senderSignPubKey va como ASN.1 DER X.509 SPKI (igual que rKey en NEW)
            WriteByteString(bodyMs, SmpCrypto.EncodeEd25519PublicKeyDer(senderSignPubKey));
            byte[] commandBody = bodyMs.ToArray();

            byte[] transmissionForAuth = BuildTransmissionForAuth(sessionId, corrId, recipientQueueId, commandBody);
            byte[] signature = SmpCrypto.Sign(transmissionForAuth, recipientSignSecretKey);
            byte[] transmission = PrependByteString(signature, transmissionForAuth);
            return WrapInBatch(transmission);
        }

        // SUB v6: subscribe to queue para recibir MSGs del sender.
        // Format: "SUB" (sin args). Server responde OK + entrega MSGs pendientes
        // (server-iniciados, requiere pump async = Fase 4d).
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

        // SEND v6: sender envia mensaje encriptado a queue.
        // Format: "SEND " + flags + ' ' + Tail(message). flags '0' (no first msg).
        // Tail = raw bytes hasta fin de transmission (sin length prefix).
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
            // Parser consume hasta ' ' permitiendo mas flags futuras; minimal v6 = "F".
            bodyMs.WriteByte((byte)'F');
            bodyMs.WriteByte((byte)' ');
            bodyMs.Write(encryptedMessage); // Tail: raw bytes, no length prefix
            byte[] commandBody = bodyMs.ToArray();

            byte[] transmissionForAuth = BuildTransmissionForAuth(sessionId, corrId, senderQueueId, commandBody);
            byte[] signature = SmpCrypto.Sign(transmissionForAuth, senderSignSecretKey);
            byte[] transmission = PrependByteString(signature, transmissionForAuth);
            return WrapInBatch(transmission);
        }

        // SEND unsigned v6: sender anonimo envia mensaje a queue NO secured (sin KEY emitido).
        // Usado en el handshake bidireccional para el primer envelope antes de que las pubkeys
        // esten cableadas. Format identico a SEND pero con signature vacia (length 0 prefix).
        // El server SMP v6 acepta SENDs unsigned mientras la queue no haya recibido KEY;
        // post-KEY, todo SEND requiere firma valida del sender registrado.
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

        // ACK v6: recipient confirma recepcion de message identificado por msgId.
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

    // Parser de transmissions del server. Layout v6:
    //   signature (1+bytes; usualmente empty del server)
    //   sessionId (1+32)
    //   corrId    (1+24)
    //   queueId   (1+bytes)
    //   command:  "TAG" [' ' arg1 arg2 ...]  o  "TAG" sin args
    internal static class SmpResponseParser
    {
        // Parsea TODAS las transmissions del batch. El server SimpleX puede batchear
        // varias transmissions en un mismo TLS block (Protocol.hs batchTransmissions_),
        // tipicamente cuando una respuesta OK (e.g. a un ACK del cliente) coincide en la
        // ventana de I/O con un MSG server-push pendiente para la misma queue.
        //
        // Bug historico (#2): el parser leia "count" pero solo deserializaba la primera
        // transmission y descartaba el resto, lo que tiraba silenciosamente los MSGs
        // batcheados con un OK de ACK. Como el server SMP entrega un MSG a la vez por
        // queue (gated por ACK del cliente), perder un MSG dejaba la queue bloqueada
        // sin error visible. Sintoma: el Cast recibia N entries y despues "silencio
        // total", con N intermitente segun el timing de batching del server.
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
        // Sin queueMode/linkId/serviceId/etc — esos son v9+.
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

        // ERR err: "ERR " + err encoding. Por ahora capturamos como string ASCII.
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
                    // espacio inicia args; rebobinar para que ParseXxx lo consuma
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
