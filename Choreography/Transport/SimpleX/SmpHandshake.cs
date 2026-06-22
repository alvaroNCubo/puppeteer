using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Puppeteer;

namespace Choreography.Transport.SimpleX
{
    // Server hello format (post-block-unpack, modo ALPN smp/1):
    //   bytes 0-1   minVersion (Word16 BE)
    //   bytes 2-3   maxVersion (Word16 BE)
    //   byte  4     sessionIdLen (uint8, 32 en servers actuales)
    //   bytes 5..   sessionId (32 bytes)
    //   bytes 5+sl. cert chain X.509 + signed server key (formato v8+, ignorado en v6)
    //
    // Client hello format v6 (per simplexmq Transport.hs `instance Encoding SMPClientHandshake`):
    //   bytes 0-1   smpVersion (Word16 BE, negotiated version)
    //   byte  2     keyHashLen (1 byte = 32; spec: ByteString uses a 1-byte length prefix)
    //   bytes 3-34  keyHash (SHA-256 of the server CA cert, TOFU anchor)
    // For v6, encodeAuthEncryptCmds and the proxy/service flags are NOT emitted (v < v7/v12).
    // Total: 35 bytes. Versions >= v7 append optional fields afterward.
    internal static class SmpHandshake
    {
        public const int MinVersion = 6;
        // Target v6 (simple format, bug report). A server with ALPN offers v[6-18];
        // [6-6] negotiates v6. Raise this when Choreography uses modern features
        // (queueReqData, ntfCreds, shortLinks).
        public const int MaxVersion = 6;

        public sealed class HandshakeResult
        {
            public int NegotiatedVersion { get; init; }
            public byte[] SessionId { get; init; }
        }

        public static async Task<HandshakeResult> PerformAsync(Stream tlsStream, byte[] keyHash, IPuppeteerLogger logger, CancellationToken ct)
        {
            if (tlsStream == null) throw new ArgumentNullException(nameof(tlsStream));
            if (keyHash == null || keyHash.Length != 32)
                throw new ArgumentException("keyHash must be 32 bytes (SHA-256 of CA cert DER)");
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            byte[] serverHello = await SmpBlock.ReadBlockAsync(tlsStream, ct);
            ParseServerHello(serverHello, out int serverMinVersion, out int serverMaxVersion, out byte[] sessionId);

            logger.Debug($"[SmpHandshake] serverHello payload {serverHello.Length}B, " +
                         $"v[{serverMinVersion}-{serverMaxVersion}], sessId.Len={sessionId.Length}, " +
                         $"first 40B: {BitConverter.ToString(serverHello, 0, Math.Min(40, serverHello.Length))}");

            int negotiated = Math.Min(MaxVersion, serverMaxVersion);
            int requiredMin = Math.Max(MinVersion, serverMinVersion);
            if (negotiated < requiredMin)
                throw new InvalidOperationException(
                    $"SMP version mismatch: server [{serverMinVersion}-{serverMaxVersion}], client [{MinVersion}-{MaxVersion}]");

            byte[] clientHello = BuildClientHello(negotiated, keyHash);
            logger.Debug($"[SmpHandshake] clientHello {clientHello.Length}B, negotiated v={negotiated}, " +
                         $"keyHash[0..8]={BitConverter.ToString(keyHash, 0, 8)}, " +
                         $"full: {BitConverter.ToString(clientHello)}");
            await SmpBlock.WriteBlockAsync(tlsStream, clientHello, ct);
            logger.Debug($"[SmpHandshake] clientHello sent ok, returning HandshakeResult");

            return new HandshakeResult { NegotiatedVersion = negotiated, SessionId = sessionId };
        }

        private static void ParseServerHello(byte[] data, out int minVersion, out int maxVersion, out byte[] sessionId)
        {
            if (data.Length < 5)
                throw new InvalidOperationException($"Server hello too short: {data.Length} bytes");

            minVersion = (data[0] << 8) | data[1];
            maxVersion = (data[2] << 8) | data[3];
            int sessionIdLen = data[4];

            if (data.Length < 5 + sessionIdLen)
                throw new InvalidOperationException($"Server hello truncated: claimed sessionIdLen={sessionIdLen}, available={data.Length - 5}");

            sessionId = new byte[sessionIdLen];
            Buffer.BlockCopy(data, 5, sessionId, 0, sessionIdLen);
        }

        private static byte[] BuildClientHello(int negotiatedVersion, byte[] keyHash)
        {
            byte[] hello = new byte[2 + 1 + 32];
            hello[0] = (byte)(negotiatedVersion >> 8);
            hello[1] = (byte)(negotiatedVersion & 0xFF);
            hello[2] = 32; // ByteString length prefix (1 byte)
            Buffer.BlockCopy(keyHash, 0, hello, 3, 32);
            return hello;
        }
    }
}
