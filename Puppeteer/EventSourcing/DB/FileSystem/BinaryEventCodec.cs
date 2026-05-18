using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Puppeteer;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal enum EventRecordType : byte
	{
		Script = 0,
		Action = 1,
		// Phase 3 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// the Define record carries actionId + canonical DSL sentence + first-invocation
		// arguments. Phase 4 flips the live caller to emit it; Phase 5 wires it into
		// RehydrateFromEvent (today the FileSystem replay silently skips it).
		Define = 2
	}

	internal enum PayloadCompression : byte
	{
		None = 0,
		GZip = 1
	}

	internal enum EncryptionMode : byte
	{
		None = 0,
		Aes256Gcm = 1
	}

	internal static class BinaryEventCodec
	{
		private const byte COMPRESSION_BIT = 0x80;
		private const byte ENCRYPTION_BIT = 0x40;
		private const byte TYPE_MASK = 0x3F;

		internal static byte[] EncodeScriptEvent(long entryId, DateTime occurredAt, string ip, string user, string script,
			PayloadCompression compression = PayloadCompression.None, EncryptionMode encryption = EncryptionMode.None, byte[] encryptionKey = null,
			string exposeData = null)
		{
			if (ip == null) throw new ArgumentNullException(nameof(ip));
			if (user == null) throw new ArgumentNullException(nameof(user));
			if (script == null) throw new ArgumentNullException(nameof(script));

			byte[] ipBytes = ip == IpAddress.DEFAULT.Ip ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(ip);
			if (ipBytes.Length > 255) throw new ArgumentException("IP string exceeds 255 bytes when UTF8 encoded.", nameof(ip));

			byte[] userBytes = user == UserInLog.ANONYMOUS.Id ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(user);
			if (userBytes.Length > 65535) throw new ArgumentException("User string exceeds 65535 bytes when UTF8 encoded.", nameof(user));

			byte[] scriptBytes = Encoding.UTF8.GetBytes(script);
			byte[] payloadBytes = ApplyTransformations(scriptBytes, compression, encryption, encryptionKey);

			byte[] exposeBytes = Array.Empty<byte>();
			if (exposeData != null)
			{
				byte[] rawExposeBytes = Encoding.UTF8.GetBytes(exposeData);
				exposeBytes = ApplyTransformations(rawExposeBytes, compression, encryption, encryptionKey);
			}

			byte typeByte = (byte)EventRecordType.Script;
			if (compression != PayloadCompression.None) typeByte |= COMPRESSION_BIT;
			if (encryption != EncryptionMode.None) typeByte |= ENCRYPTION_BIT;

			int bodySize = 1 + 8 + 8 + 1 + ipBytes.Length + 2 + userBytes.Length + 4 + payloadBytes.Length + 4 + exposeBytes.Length;
			int totalSize = 4 + bodySize + 4;

			byte[] buffer = new byte[totalSize];
			int offset = 0;

			WriteInt32(buffer, ref offset, bodySize + 4);
			int bodyStart = offset;

			buffer[offset++] = typeByte;
			WriteInt64(buffer, ref offset, entryId);
			WriteInt64(buffer, ref offset, occurredAt.ToBinary());
			buffer[offset++] = (byte)ipBytes.Length;
			Buffer.BlockCopy(ipBytes, 0, buffer, offset, ipBytes.Length);
			offset += ipBytes.Length;
			WriteUInt16(buffer, ref offset, (ushort)userBytes.Length);
			Buffer.BlockCopy(userBytes, 0, buffer, offset, userBytes.Length);
			offset += userBytes.Length;
			WriteInt32(buffer, ref offset, payloadBytes.Length);
			Buffer.BlockCopy(payloadBytes, 0, buffer, offset, payloadBytes.Length);
			offset += payloadBytes.Length;
			WriteInt32(buffer, ref offset, exposeBytes.Length);
			if (exposeBytes.Length > 0)
			{
				Buffer.BlockCopy(exposeBytes, 0, buffer, offset, exposeBytes.Length);
				offset += exposeBytes.Length;
			}

			uint crc = ComputeCrc32(buffer, bodyStart, offset - bodyStart);
			WriteUInt32(buffer, ref offset, crc);

			return buffer;
		}

		internal static byte[] EncodeActionEvent(long entryId, DateTime occurredAt, string ip, string user, int actionId, string arguments,
			PayloadCompression compression = PayloadCompression.None, EncryptionMode encryption = EncryptionMode.None, byte[] encryptionKey = null,
			string exposeData = null)
		{
			if (ip == null) throw new ArgumentNullException(nameof(ip));
			if (user == null) throw new ArgumentNullException(nameof(user));
			if (arguments == null) throw new ArgumentNullException(nameof(arguments));

			byte[] ipBytes = ip == IpAddress.DEFAULT.Ip ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(ip);
			if (ipBytes.Length > 255) throw new ArgumentException("IP string exceeds 255 bytes when UTF8 encoded.", nameof(ip));

			byte[] userBytes = user == UserInLog.ANONYMOUS.Id ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(user);
			if (userBytes.Length > 65535) throw new ArgumentException("User string exceeds 65535 bytes when UTF8 encoded.", nameof(user));

			byte[] argsBytes = Encoding.UTF8.GetBytes(arguments);
			byte[] payloadBytes = ApplyTransformations(argsBytes, compression, encryption, encryptionKey);

			byte[] exposeBytes = Array.Empty<byte>();
			if (exposeData != null)
			{
				byte[] rawExposeBytes = Encoding.UTF8.GetBytes(exposeData);
				exposeBytes = ApplyTransformations(rawExposeBytes, compression, encryption, encryptionKey);
			}

			byte typeByte = (byte)EventRecordType.Action;
			if (compression != PayloadCompression.None) typeByte |= COMPRESSION_BIT;
			if (encryption != EncryptionMode.None) typeByte |= ENCRYPTION_BIT;

			int bodySize = 1 + 8 + 8 + 1 + ipBytes.Length + 2 + userBytes.Length + 4 + 4 + payloadBytes.Length + 4 + exposeBytes.Length;
			int totalSize = 4 + bodySize + 4;

			byte[] buffer = new byte[totalSize];
			int offset = 0;

			WriteInt32(buffer, ref offset, bodySize + 4);
			int bodyStart = offset;

			buffer[offset++] = typeByte;
			WriteInt64(buffer, ref offset, entryId);
			WriteInt64(buffer, ref offset, occurredAt.ToBinary());
			buffer[offset++] = (byte)ipBytes.Length;
			Buffer.BlockCopy(ipBytes, 0, buffer, offset, ipBytes.Length);
			offset += ipBytes.Length;
			WriteUInt16(buffer, ref offset, (ushort)userBytes.Length);
			Buffer.BlockCopy(userBytes, 0, buffer, offset, userBytes.Length);
			offset += userBytes.Length;
			WriteInt32(buffer, ref offset, actionId);
			WriteInt32(buffer, ref offset, payloadBytes.Length);
			Buffer.BlockCopy(payloadBytes, 0, buffer, offset, payloadBytes.Length);
			offset += payloadBytes.Length;
			WriteInt32(buffer, ref offset, exposeBytes.Length);
			if (exposeBytes.Length > 0)
			{
				Buffer.BlockCopy(exposeBytes, 0, buffer, offset, exposeBytes.Length);
				offset += exposeBytes.Length;
			}

			uint crc = ComputeCrc32(buffer, bodyStart, offset - bodyStart);
			WriteUInt32(buffer, ref offset, crc);

			return buffer;
		}

		internal static bool TryDecode(byte[] recordBuffer, int recordLength,
			out EventRecordType eventType, out long entryId, out DateTime occurredAt,
			out string ip, out string user, out string scriptOrArguments, out int actionId,
			PayloadCompression compression = PayloadCompression.None, EncryptionMode encryption = EncryptionMode.None, byte[] encryptionKey = null)
		{
			return TryDecode(recordBuffer, recordLength, out eventType, out entryId, out occurredAt,
				out ip, out user, out scriptOrArguments, out actionId, out _, compression, encryption, encryptionKey);
		}

		internal static bool TryDecode(byte[] recordBuffer, int recordLength,
			out EventRecordType eventType, out long entryId, out DateTime occurredAt,
			out string ip, out string user, out string scriptOrArguments, out int actionId,
			out string exposeData,
			PayloadCompression compression = PayloadCompression.None, EncryptionMode encryption = EncryptionMode.None, byte[] encryptionKey = null)
		{
			if (recordBuffer == null) throw new ArgumentNullException(nameof(recordBuffer));

			eventType = EventRecordType.Script;
			entryId = 0;
			occurredAt = default;
			ip = null;
			user = null;
			scriptOrArguments = null;
			actionId = 0;
			exposeData = null;

			if (recordLength < 22) return false;

			int crcOffset = recordLength - 4;
			uint storedCrc = ReadUInt32(recordBuffer, crcOffset);
			uint computedCrc = ComputeCrc32(recordBuffer, 0, crcOffset);
			if (storedCrc != computedCrc) return false;

			int offset = 0;

			byte typeByte = recordBuffer[offset++];
			bool isCompressed = (typeByte & COMPRESSION_BIT) != 0;
			bool isEncrypted = (typeByte & ENCRYPTION_BIT) != 0;
			eventType = (EventRecordType)(typeByte & TYPE_MASK);

			// Phase 3 of the Action refactor: Define records have a distinct two-payload
			// layout (actionId + defineStatementText + arguments) and must be decoded via
			// TryDecodeDefine. Returning false here makes JournalReader silently skip them
			// during replay (firmado Q5 — Phase 5 wires the real dispatch once the live
			// caller emits Define entries).
			if (eventType == EventRecordType.Define) return false;

			entryId = ReadInt64(recordBuffer, ref offset);
			occurredAt = DateTime.FromBinary(ReadInt64(recordBuffer, ref offset));

			int ipLen = recordBuffer[offset++];
			if (offset + ipLen > crcOffset) return false;
			ip = ipLen == 0 ? IpAddress.DEFAULT.Ip : Encoding.UTF8.GetString(recordBuffer, offset, ipLen);
			offset += ipLen;

			if (offset + 2 > crcOffset) return false;
			int userLen = ReadUInt16(recordBuffer, ref offset);
			if (offset + userLen > crcOffset) return false;
			user = userLen == 0 ? UserInLog.ANONYMOUS.Id : Encoding.UTF8.GetString(recordBuffer, offset, userLen);
			offset += userLen;

			if (eventType == EventRecordType.Action)
			{
				if (offset + 4 > crcOffset) return false;
				actionId = ReadInt32(recordBuffer, ref offset);
			}

			if (offset + 4 > crcOffset) return false;
			int payloadLen = ReadInt32(recordBuffer, ref offset);
			if (offset + payloadLen > crcOffset) return false;

			byte[] payloadBytes = new byte[payloadLen];
			Buffer.BlockCopy(recordBuffer, offset, payloadBytes, 0, payloadLen);
			offset += payloadLen;

			byte[] decodedPayload = ReverseTransformations(payloadBytes, isCompressed, isEncrypted, encryption, encryptionKey);
			scriptOrArguments = Encoding.UTF8.GetString(decodedPayload);

			if (offset + 4 <= crcOffset)
			{
				int exposeLen = ReadInt32(recordBuffer, ref offset);
				if (exposeLen > 0 && offset + exposeLen <= crcOffset)
				{
					byte[] exposeBytes = new byte[exposeLen];
					Buffer.BlockCopy(recordBuffer, offset, exposeBytes, 0, exposeLen);

					byte[] decodedExpose = ReverseTransformations(exposeBytes, isCompressed, isEncrypted, encryption, encryptionKey);
					exposeData = Encoding.UTF8.GetString(decodedExpose);
				}
			}

			return true;
		}

		// Phase 3 of the Action refactor: encodes a Define record carrying
		// (actionId, defineStatementText). Phase 4 split-model firmado (2026-05-09):
		// Define records do NOT carry arguments — the first invocation lives in a
		// separate Invocation record written immediately after. Layout mirrors
		// EncodeActionEvent but the body payload is the canonical DSL sentence
		// (`define action <id> (params) as <body> end;`) instead of the
		// invocation arguments.
		internal static byte[] EncodeDefineEvent(long entryId, DateTime occurredAt, string ip, string user, int actionId, string defineStatementText,
			PayloadCompression compression = PayloadCompression.None, EncryptionMode encryption = EncryptionMode.None, byte[] encryptionKey = null,
			string exposeData = null)
		{
			if (ip == null) throw new ArgumentNullException(nameof(ip));
			if (user == null) throw new ArgumentNullException(nameof(user));
			if (defineStatementText == null) throw new ArgumentNullException(nameof(defineStatementText));

			byte[] ipBytes = ip == IpAddress.DEFAULT.Ip ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(ip);
			if (ipBytes.Length > 255) throw new ArgumentException("IP string exceeds 255 bytes when UTF8 encoded.", nameof(ip));

			byte[] userBytes = user == UserInLog.ANONYMOUS.Id ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(user);
			if (userBytes.Length > 65535) throw new ArgumentException("User string exceeds 65535 bytes when UTF8 encoded.", nameof(user));

			byte[] defineBytes = Encoding.UTF8.GetBytes(defineStatementText);
			byte[] definePayload = ApplyTransformations(defineBytes, compression, encryption, encryptionKey);

			byte[] exposeBytes = Array.Empty<byte>();
			if (exposeData != null)
			{
				byte[] rawExposeBytes = Encoding.UTF8.GetBytes(exposeData);
				exposeBytes = ApplyTransformations(rawExposeBytes, compression, encryption, encryptionKey);
			}

			byte typeByte = (byte)EventRecordType.Define;
			if (compression != PayloadCompression.None) typeByte |= COMPRESSION_BIT;
			if (encryption != EncryptionMode.None) typeByte |= ENCRYPTION_BIT;

			int bodySize = 1 + 8 + 8 + 1 + ipBytes.Length + 2 + userBytes.Length + 4 + 4 + definePayload.Length + 4 + exposeBytes.Length;
			int totalSize = 4 + bodySize + 4;

			byte[] buffer = new byte[totalSize];
			int offset = 0;

			WriteInt32(buffer, ref offset, bodySize + 4);
			int bodyStart = offset;

			buffer[offset++] = typeByte;
			WriteInt64(buffer, ref offset, entryId);
			WriteInt64(buffer, ref offset, occurredAt.ToBinary());
			buffer[offset++] = (byte)ipBytes.Length;
			Buffer.BlockCopy(ipBytes, 0, buffer, offset, ipBytes.Length);
			offset += ipBytes.Length;
			WriteUInt16(buffer, ref offset, (ushort)userBytes.Length);
			Buffer.BlockCopy(userBytes, 0, buffer, offset, userBytes.Length);
			offset += userBytes.Length;
			WriteInt32(buffer, ref offset, actionId);
			WriteInt32(buffer, ref offset, definePayload.Length);
			Buffer.BlockCopy(definePayload, 0, buffer, offset, definePayload.Length);
			offset += definePayload.Length;
			WriteInt32(buffer, ref offset, exposeBytes.Length);
			if (exposeBytes.Length > 0)
			{
				Buffer.BlockCopy(exposeBytes, 0, buffer, offset, exposeBytes.Length);
				offset += exposeBytes.Length;
			}

			uint crc = ComputeCrc32(buffer, bodyStart, offset - bodyStart);
			WriteUInt32(buffer, ref offset, crc);

			return buffer;
		}

		// Phase 3 of the Action refactor: decodes a Define record. Returns false if the
		// record's type byte is not Define, mirroring the negative-result contract of
		// the existing TryDecode. Phase 4 split-model firmado: Define records do not
		// carry arguments — first invocation is a separate record.
		internal static bool TryDecodeDefine(byte[] recordBuffer, int recordLength,
			out long entryId, out DateTime occurredAt, out string ip, out string user,
			out int actionId, out string defineStatementText, out string exposeData,
			PayloadCompression compression = PayloadCompression.None, EncryptionMode encryption = EncryptionMode.None, byte[] encryptionKey = null)
		{
			if (recordBuffer == null) throw new ArgumentNullException(nameof(recordBuffer));

			entryId = 0;
			occurredAt = default;
			ip = null;
			user = null;
			actionId = 0;
			defineStatementText = null;
			exposeData = null;

			if (recordLength < 22) return false;

			int crcOffset = recordLength - 4;
			uint storedCrc = ReadUInt32(recordBuffer, crcOffset);
			uint computedCrc = ComputeCrc32(recordBuffer, 0, crcOffset);
			if (storedCrc != computedCrc) return false;

			int offset = 0;

			byte typeByte = recordBuffer[offset++];
			bool isCompressed = (typeByte & COMPRESSION_BIT) != 0;
			bool isEncrypted = (typeByte & ENCRYPTION_BIT) != 0;
			EventRecordType recordType = (EventRecordType)(typeByte & TYPE_MASK);
			if (recordType != EventRecordType.Define) return false;

			entryId = ReadInt64(recordBuffer, ref offset);
			occurredAt = DateTime.FromBinary(ReadInt64(recordBuffer, ref offset));

			int ipLen = recordBuffer[offset++];
			if (offset + ipLen > crcOffset) return false;
			ip = ipLen == 0 ? IpAddress.DEFAULT.Ip : Encoding.UTF8.GetString(recordBuffer, offset, ipLen);
			offset += ipLen;

			if (offset + 2 > crcOffset) return false;
			int userLen = ReadUInt16(recordBuffer, ref offset);
			if (offset + userLen > crcOffset) return false;
			user = userLen == 0 ? UserInLog.ANONYMOUS.Id : Encoding.UTF8.GetString(recordBuffer, offset, userLen);
			offset += userLen;

			if (offset + 4 > crcOffset) return false;
			actionId = ReadInt32(recordBuffer, ref offset);

			if (offset + 4 > crcOffset) return false;
			int definePayloadLen = ReadInt32(recordBuffer, ref offset);
			if (offset + definePayloadLen > crcOffset) return false;
			byte[] definePayload = new byte[definePayloadLen];
			Buffer.BlockCopy(recordBuffer, offset, definePayload, 0, definePayloadLen);
			offset += definePayloadLen;
			byte[] decodedDefine = ReverseTransformations(definePayload, isCompressed, isEncrypted, encryption, encryptionKey);
			defineStatementText = Encoding.UTF8.GetString(decodedDefine);

			if (offset + 4 <= crcOffset)
			{
				int exposeLen = ReadInt32(recordBuffer, ref offset);
				if (exposeLen > 0 && offset + exposeLen <= crcOffset)
				{
					byte[] exposeBytes = new byte[exposeLen];
					Buffer.BlockCopy(recordBuffer, offset, exposeBytes, 0, exposeLen);

					byte[] decodedExpose = ReverseTransformations(exposeBytes, isCompressed, isEncrypted, encryption, encryptionKey);
					exposeData = Encoding.UTF8.GetString(decodedExpose);
				}
			}

			return true;
		}

		// Phase 3 of the Action refactor: peek the record type byte without decoding
		// the full body. Used by RehydrateFromEvent in FileSystem to silently skip
		// Define records (Phase 5 turns this into a real dispatch).
		internal static EventRecordType PeekRecordType(byte[] recordBuffer)
		{
			if (recordBuffer == null) throw new ArgumentNullException(nameof(recordBuffer));
			if (recordBuffer.Length < 1) throw new ArgumentException("Record buffer too small to contain type byte.");
			return (EventRecordType)(recordBuffer[0] & TYPE_MASK);
		}

		internal static long PeekEntryId(byte[] recordBuffer)
		{
			if (recordBuffer == null) throw new ArgumentNullException(nameof(recordBuffer));
			if (recordBuffer.Length < 9) throw new ArgumentException("Record buffer too small to contain entryId.");

			int offset = 1;
			return ReadInt64(recordBuffer, ref offset);
		}

		internal static bool ValidateCrc(byte[] recordBuffer, int recordLength)
		{
			if (recordBuffer == null) throw new ArgumentNullException(nameof(recordBuffer));
			if (recordLength < 5) return false;

			int crcOffset = recordLength - 4;
			uint storedCrc = ReadUInt32(recordBuffer, crcOffset);
			uint computedCrc = ComputeCrc32(recordBuffer, 0, crcOffset);
			return storedCrc == computedCrc;
		}

		private static byte[] ApplyTransformations(byte[] data, PayloadCompression compression, EncryptionMode encryption, byte[] encryptionKey)
		{
			byte[] result = data;

			if (compression == PayloadCompression.GZip)
			{
				result = CompressGZip(result);
			}

			if (encryption == EncryptionMode.Aes256Gcm)
			{
				if (encryptionKey == null) throw new ArgumentNullException(nameof(encryptionKey), "Encryption key is required when encryption is enabled.");
				if (encryptionKey.Length != 32) throw new ArgumentException("Encryption key must be 32 bytes for AES-256.", nameof(encryptionKey));
				result = EncryptAes256Gcm(result, encryptionKey);
			}

			return result;
		}

		private static byte[] ReverseTransformations(byte[] data, bool isCompressed, bool isEncrypted, EncryptionMode encryption, byte[] encryptionKey)
		{
			byte[] result = data;

			if (isEncrypted)
			{
				if (encryptionKey == null) throw new ArgumentException("Encryption key is required to decrypt data.");
				if (encryptionKey.Length != 32) throw new ArgumentException("Encryption key must be 32 bytes for AES-256.");
				result = DecryptAes256Gcm(result, encryptionKey);
			}

			if (isCompressed)
			{
				result = DecompressGZip(result);
			}

			return result;
		}

		private static byte[] CompressGZip(byte[] data)
		{
			using (var output = new MemoryStream())
			{
				using (var gzip = new GZipStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
				{
					gzip.Write(data, 0, data.Length);
				}
				return output.ToArray();
			}
		}

		private static byte[] DecompressGZip(byte[] data)
		{
			using (var input = new MemoryStream(data))
			using (var gzip = new GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
			using (var output = new MemoryStream())
			{
				gzip.CopyTo(output);
				return output.ToArray();
			}
		}

		private static byte[] EncryptAes256Gcm(byte[] data, byte[] key)
		{
			byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
			RandomNumberGenerator.Fill(nonce);

			byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];
			byte[] ciphertext = new byte[data.Length];

			using (var aes = new AesGcm(key, tag.Length))
			{
				aes.Encrypt(nonce, data, ciphertext, tag);
			}

			byte[] result = new byte[nonce.Length + tag.Length + ciphertext.Length];
			Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
			Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
			Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);

			return result;
		}

		private static byte[] DecryptAes256Gcm(byte[] data, byte[] key)
		{
			int nonceSize = AesGcm.NonceByteSizes.MaxSize;
			int tagSize = AesGcm.TagByteSizes.MaxSize;

			if (data.Length < nonceSize + tagSize)
				throw new ArgumentException("Encrypted data is too short.");

			byte[] nonce = new byte[nonceSize];
			byte[] tag = new byte[tagSize];
			int ciphertextLen = data.Length - nonceSize - tagSize;
			byte[] ciphertext = new byte[ciphertextLen];

			Buffer.BlockCopy(data, 0, nonce, 0, nonceSize);
			Buffer.BlockCopy(data, nonceSize, tag, 0, tagSize);
			Buffer.BlockCopy(data, nonceSize + tagSize, ciphertext, 0, ciphertextLen);

			byte[] plaintext = new byte[ciphertextLen];
			using (var aes = new AesGcm(key, tagSize))
			{
				aes.Decrypt(nonce, ciphertext, tag, plaintext);
			}

			return plaintext;
		}

		private static uint ComputeCrc32(byte[] data, int offset, int length)
		{
			var crc = new Crc32();
			crc.Append(data.AsSpan(offset, length));
			byte[] hash = crc.GetCurrentHash();
			return BitConverter.ToUInt32(hash, 0);
		}

		private static void WriteInt32(byte[] buffer, ref int offset, int value)
		{
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), value);
			offset += 4;
		}

		private static void WriteInt64(byte[] buffer, ref int offset, long value)
		{
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 8), value);
			offset += 8;
		}

		private static void WriteUInt16(byte[] buffer, ref int offset, ushort value)
		{
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 2), value);
			offset += 2;
		}

		private static void WriteUInt32(byte[] buffer, ref int offset, uint value)
		{
			BitConverter.TryWriteBytes(buffer.AsSpan(offset, 4), value);
			offset += 4;
		}

		private static int ReadInt32(byte[] buffer, ref int offset)
		{
			int val = BitConverter.ToInt32(buffer, offset);
			offset += 4;
			return val;
		}

		private static long ReadInt64(byte[] buffer, ref int offset)
		{
			long val = BitConverter.ToInt64(buffer, offset);
			offset += 8;
			return val;
		}

		private static ushort ReadUInt16(byte[] buffer, ref int offset)
		{
			ushort val = BitConverter.ToUInt16(buffer, offset);
			offset += 2;
			return val;
		}

		private static uint ReadUInt32(byte[] buffer, int offset)
		{
			return BitConverter.ToUInt32(buffer, offset);
		}
	}
}
