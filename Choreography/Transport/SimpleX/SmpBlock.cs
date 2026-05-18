using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Choreography.Transport.SimpleX
{
    internal static class SmpBlock
    {
        public const int BlockSize = 16_384;
        public const int LengthPrefixSize = 2; // uint16 big-endian
        public const int MaxPayloadSize = BlockSize - LengthPrefixSize;

        public static byte[] Pack(ReadOnlySpan<byte> payload)
        {
            if (payload.Length > MaxPayloadSize)
                throw new ArgumentException($"Payload too large: {payload.Length} > {MaxPayloadSize}");

            byte[] block = new byte[BlockSize];

            // 2-byte big-endian length prefix
            block[0] = (byte)(payload.Length >> 8);
            block[1] = (byte)(payload.Length & 0xFF);

            // Payload
            payload.CopyTo(block.AsSpan(LengthPrefixSize));

            // Padding con caracter '#' (0x23). La spec SMP (simplexmq Crypto.hs `pad`)
            // exige este byte especifico, no random — el server compara con mascara fija.
            if (payload.Length < MaxPayloadSize)
            {
                block.AsSpan(LengthPrefixSize + payload.Length).Fill((byte)'#');
            }

            return block;
        }

        public static byte[] Unpack(ReadOnlySpan<byte> block)
        {
            if (block.Length < LengthPrefixSize)
                throw new ArgumentException("Block too short");

            int payloadLength = (block[0] << 8) | block[1];

            if (payloadLength > MaxPayloadSize)
                throw new ArgumentException($"Invalid payload length: {payloadLength}");
            if (payloadLength > block.Length - LengthPrefixSize)
                throw new ArgumentException("Block too short for declared payload");

            return block.Slice(LengthPrefixSize, payloadLength).ToArray();
        }

        public static async Task<byte[]> ReadBlockAsync(Stream stream, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            byte[] block = new byte[BlockSize];
            int totalRead = 0;

            while (totalRead < BlockSize)
            {
                int read = await stream.ReadAsync(block.AsMemory(totalRead, BlockSize - totalRead), ct);
                if (read == 0)
                    throw new IOException("Connection closed while reading SMP block");
                totalRead += read;
            }

            return Unpack(block);
        }

        public static async Task WriteBlockAsync(Stream stream, byte[] payload, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            byte[] block = Pack(payload);
            await stream.WriteAsync(block, ct);
            await stream.FlushAsync(ct);
        }
    }
}
