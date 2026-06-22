using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.X509;

namespace Choreography.Transport.SimpleX
{
    // Replaces Sodium.Core so Choreography runs on MAUI Android/iOS where
    // Sodium provides no native binaries. All crypto is managed via BouncyCastle
    // or System.Security.Cryptography (native .NET SHA256).
    //
    // crypto_box (X25519 + XSalsa20-Poly1305) is implemented over BC primitives:
    //   - X25519Agreement for the shared secret
    //   - manual HSalsa20 to derive shared_key (BC does not expose HSalsa20 separately)
    //   - XSalsa20Engine + Poly1305 for secretbox
    //
    // The correctness of HSalsa20 + crypto_box is validated in tests via cross-check
    // against Chaos.NaCl (NetSparkleUpdater fork) — it produces the same byte-exact output.
    internal static class SmpCrypto
    {
        public const int NonceSize = 24;
        public const int MacSize = 16;

        private static readonly Org.BouncyCastle.Security.SecureRandom Rng = new();

        // --- Ed25519 signing ---
        // Sodium exposes the secret key as 64 bytes (32-byte seed || 32-byte public).
        // BC uses only the 32-byte seed. We keep the Sodium-style format in the API
        // so as not to break persisted data; internally we extract the first 32 bytes.

        public static (byte[] PublicKey, byte[] SecretKey) GenerateSigningKeyPair()
        {
            var gen = new Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(Rng));
            var pair = gen.GenerateKeyPair();
            byte[] pub = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
            byte[] seed = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();

            byte[] sodiumStyleSecret = new byte[64];
            Buffer.BlockCopy(seed, 0, sodiumStyleSecret, 0, 32);
            Buffer.BlockCopy(pub, 0, sodiumStyleSecret, 32, 32);
            return (pub, sodiumStyleSecret);
        }

        public static byte[] Sign(ReadOnlySpan<byte> message, byte[] ed25519SecretKey)
        {
            if (ed25519SecretKey == null) throw new ArgumentNullException(nameof(ed25519SecretKey));
            if (ed25519SecretKey.Length != 64)
                throw new ArgumentException($"Ed25519 secret key must be 64 bytes (Sodium-style: seed||pub), got {ed25519SecretKey.Length}");

            var priv = new Ed25519PrivateKeyParameters(ed25519SecretKey, 0);
            var signer = new Ed25519Signer();
            signer.Init(true, priv);
            byte[] msg = message.ToArray();
            signer.BlockUpdate(msg, 0, msg.Length);
            return signer.GenerateSignature();
        }

        public static bool Verify(ReadOnlySpan<byte> message, byte[] signature, byte[] ed25519PublicKey)
        {
            if (signature == null) throw new ArgumentNullException(nameof(signature));
            if (ed25519PublicKey == null) throw new ArgumentNullException(nameof(ed25519PublicKey));
            if (ed25519PublicKey.Length != 32)
                throw new ArgumentException($"Ed25519 public key must be 32 bytes, got {ed25519PublicKey.Length}");

            var pub = new Ed25519PublicKeyParameters(ed25519PublicKey, 0);
            var signer = new Ed25519Signer();
            signer.Init(false, pub);
            byte[] msg = message.ToArray();
            signer.BlockUpdate(msg, 0, msg.Length);
            return signer.VerifySignature(signature);
        }

        // --- X25519 DH ---

        public static (byte[] PublicKey, byte[] SecretKey) GenerateDhKeyPair()
        {
            var gen = new X25519KeyPairGenerator();
            gen.Init(new X25519KeyGenerationParameters(Rng));
            var pair = gen.GenerateKeyPair();
            byte[] pub = ((X25519PublicKeyParameters)pair.Public).GetEncoded();
            byte[] sec = ((X25519PrivateKeyParameters)pair.Private).GetEncoded();
            return (pub, sec);
        }

        // --- crypto_box (X25519 + XSalsa20-Poly1305) ---

        public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] nonce,
            byte[] senderSecretKey, byte[] receiverPublicKey)
        {
            if (nonce == null || nonce.Length != NonceSize)
                throw new ArgumentException($"nonce must be {NonceSize} bytes");
            if (senderSecretKey == null || senderSecretKey.Length != 32)
                throw new ArgumentException("X25519 secret key must be 32 bytes");
            if (receiverPublicKey == null || receiverPublicKey.Length != 32)
                throw new ArgumentException("X25519 public key must be 32 bytes");

            byte[] sharedKey = DeriveSharedKey(senderSecretKey, receiverPublicKey);
            return SecretBox(plaintext.ToArray(), nonce, sharedKey);
        }

        public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, byte[] nonce,
            byte[] receiverSecretKey, byte[] senderPublicKey)
        {
            if (nonce == null || nonce.Length != NonceSize)
                throw new ArgumentException($"nonce must be {NonceSize} bytes");
            if (receiverSecretKey == null || receiverSecretKey.Length != 32)
                throw new ArgumentException("X25519 secret key must be 32 bytes");
            if (senderPublicKey == null || senderPublicKey.Length != 32)
                throw new ArgumentException("X25519 public key must be 32 bytes");

            byte[] sharedKey = DeriveSharedKey(receiverSecretKey, senderPublicKey);
            return SecretBoxOpen(ciphertext.ToArray(), nonce, sharedKey);
        }

        // --- C2S (server-to-recipient) layer decryption ---
        //
        // simplexmq encrypts MSG bodies for delivery to the recipient with NaCl crypto_box
        // (XSalsa20-Poly1305 secretbox preceded by HSalsa20 with a zero-16 nonce over the raw
        // X25519 shared, per NaCl crypto_box_beforenm). The nonce is the msgId padded
        // to 24 bytes with zeros on the right. Wire layout:
        //
        //   EncryptedBody = [16-byte Poly1305 tag][N-byte XSalsa20 ciphertext]
        //
        // Plaintext (post-secretbox_open) is a paddedString:
        //
        //   [Word16 BE bodyLen][bodyLen bytes rcvMsgBody]['#' padding up to a fixed size]
        //
        // rcvMsgBody contains the server meta + the original body the sender sent:
        //
        //   [Word64 BE SystemTime (8B)][MsgFlags (1B 'F'/'T' + extras)][' '][... msgBody ...]
        //
        // For handshake bootstrap (unsigned SEND), msgBody is the raw serialized envelope.
        // Post-handshake (signed SEND), msgBody is in turn [24B sender-nonce][crypto_box(...)].
        //
        // Clarification on simplexmq's cryptoBox: at first glance it appears to use the raw X25519
        // shared as the direct secretbox key; in reality simplexmq/src/Simplex/Messaging/Crypto.hs
        // xSalsa20 does XSalsa.initialize 20 secret (16zeros++iv0) followed by XSalsa.derive iv1
        // — two consecutive cryptonite_xsalsa_derive. The first (with an all-zero nonce)
        // equals HSalsa20(secret, 16zeros) = crypto_box_beforenm. The second is the
        // standard derivation HSalsa20(subkey, nonce[0..16]). Result: cryptoBox secret nonce
        // = NaCl crypto_box(msg, nonce, recipientPub, senderSec) with secret = X25519 raw shared.
        // That is why we reuse Decrypt() (which already does DeriveSharedKey = HSalsa20 + SecretBoxOpen).
        //
        // simplexmq references:
        //   Server.hs:2030-2036  encryptMsg = cbEncryptMaxLenBS rcvDhSecret (cbNonce msgId)
        //   Crypto.hs:1314-1316  cbEncryptMaxLenBS = cryptoBox secret nonce . padMaxLenBS
        //   Crypto.hs            cryptoBox secret nonce s = tag <> c (no nonce prepended)
        //   Crypto.hs            cbNonce pads msgId to 24 with zeros on the right if shorter
        //   Protocol.hs:313      MaxRcvMessageLen = 16104; padded size = 16106
        //   Protocol.hs          encodeRcvMsgBody = [SystemTime 8B][MsgFlags + ' '][msgBody Tail]
        public static byte[] DecryptC2SEnvelope(byte[] wireBody, byte[] msgId,
            byte[] recipientDhSecretRaw, byte[] serverDhPublicKeyDer)
        {
            if (wireBody == null) throw new ArgumentNullException(nameof(wireBody));
            if (msgId == null) throw new ArgumentNullException(nameof(msgId));
            if (recipientDhSecretRaw == null) throw new ArgumentNullException(nameof(recipientDhSecretRaw));
            if (recipientDhSecretRaw.Length != 32)
                throw new ArgumentException($"recipient DH secret must be 32 bytes, got {recipientDhSecretRaw.Length}");
            if (serverDhPublicKeyDer == null) throw new ArgumentNullException(nameof(serverDhPublicKeyDer));
            if (wireBody.Length < MacSize + 2)
                throw new ArgumentException($"wireBody demasiado corto ({wireBody.Length}B) para tag+lenPrefix");

            // 1. Pad msgId to 24 bytes with zeros on the right (simplexmq's cbNonce).
            byte[] nonce = new byte[NonceSize];
            int copyLen = Math.Min(msgId.Length, NonceSize);
            Buffer.BlockCopy(msgId, 0, nonce, 0, copyLen);

            // 2. NaCl crypto_box_open with X25519 shared + HSalsa20(zeros) wrap. Reuses the
            //    existing Decrypt(), which is bit-equivalent to Chaos.NaCl/libsodium per the
            //    SmpCrypto_*_ByteEqualsChaosNaCl_* test suite.
            byte[] serverDhPubRaw = DecodeX25519PublicKeyDer(serverDhPublicKeyDer);
            byte[] padded = Decrypt(wireBody, nonce, recipientDhSecretRaw, serverDhPubRaw);

            // 3. UnPad: [Word16 BE len][body][# padding]  ->  body of "len" bytes.
            if (padded.Length < 2)
                throw new InvalidOperationException("padded plaintext demasiado corto para Word16 length prefix");
            int bodyLen = (padded[0] << 8) | padded[1];
            if (bodyLen < 0 || bodyLen > padded.Length - 2)
                throw new InvalidOperationException(
                    $"paddedString bodyLen {bodyLen} excede plaintext disponible {padded.Length - 2}");
            byte[] rcvMsgBody = new byte[bodyLen];
            Buffer.BlockCopy(padded, 2, rcvMsgBody, 0, bodyLen);
            return rcvMsgBody;
        }

        // Extracts the msgBody (what the sender actually sent) from the rcvMsgBody that the server
        // encodes (simplexmq Protocol.hs encodeRcvMsgBody + clientRcvMsgBodyP):
        //
        //   rcvMsgBody = [SystemTime 8B][MsgFlags (1B boolean + 0..6B extras)][' '][... msgBody ...]
        //
        // Quota messages carry a different prefix ("QUOTA "); for handshake bootstrap the server
        // delivers normal Messages, so we treat QUOTA as an explicit error.
        public static byte[] ExtractMsgBodyFromRcvMsgBody(byte[] rcvMsgBody)
        {
            if (rcvMsgBody == null) throw new ArgumentNullException(nameof(rcvMsgBody));
            if (rcvMsgBody.Length >= 6
                && rcvMsgBody[0] == (byte)'Q' && rcvMsgBody[1] == (byte)'U'
                && rcvMsgBody[2] == (byte)'O' && rcvMsgBody[3] == (byte)'T'
                && rcvMsgBody[4] == (byte)'A' && rcvMsgBody[5] == (byte)' ')
                throw new InvalidOperationException("Server entrego MsgQuota, no Message");

            const int SystemTimeBytes = 8;
            if (rcvMsgBody.Length < SystemTimeBytes + 2)
                throw new InvalidOperationException(
                    $"rcvMsgBody demasiado corto ({rcvMsgBody.Length}B) para meta block");

            int idx = SystemTimeBytes + 1;
            while (idx < rcvMsgBody.Length && rcvMsgBody[idx] != (byte)' ') idx++;
            if (idx >= rcvMsgBody.Length)
                throw new InvalidOperationException("rcvMsgBody sin space separador entre MsgFlags y msgBody");
            idx++;

            int len = rcvMsgBody.Length - idx;
            byte[] msgBody = new byte[len];
            Buffer.BlockCopy(rcvMsgBody, idx, msgBody, 0, len);
            return msgBody;
        }

        // --- Random ---

        public static byte[] GenerateNonce()
        {
            byte[] n = new byte[NonceSize];
            Rng.NextBytes(n);
            return n;
        }

        public static byte[] RandomBytes(int count)
        {
            if (count <= 0) throw new ArgumentException("count must be positive", nameof(count));
            byte[] b = new byte[count];
            Rng.NextBytes(b);
            return b;
        }

        // --- Public key DER (X.509 SubjectPublicKeyInfo) ---
        // simplexmq Crypto.hs encodePubKey = encodeASNObj . publicToX509 — the SMP wire
        // keys do NOT go as 32 raw bytes, they are wrapped in ASN.1 DER X.509 SPKI.
        // For Ed25519: ~44 bytes (OID 1.3.101.112). For X25519: ~44 bytes (OID 1.3.101.110).

        public static byte[] EncodeEd25519PublicKeyDer(byte[] rawPubKey)
        {
            if (rawPubKey == null || rawPubKey.Length != 32)
                throw new ArgumentException("Ed25519 raw public key must be 32 bytes");
            var param = new Ed25519PublicKeyParameters(rawPubKey, 0);
            return Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(param).GetEncoded();
        }

        public static byte[] EncodeX25519PublicKeyDer(byte[] rawPubKey)
        {
            if (rawPubKey == null || rawPubKey.Length != 32)
                throw new ArgumentException("X25519 raw public key must be 32 bytes");
            var param = new X25519PublicKeyParameters(rawPubKey, 0);
            return Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(param).GetEncoded();
        }

        // Decodes an X25519 pubkey wrapped in ASN.1 DER X.509 SubjectPublicKeyInfo
        // (format the SMP server sends for ServerDhPublicKey in IDS) and returns
        // the 32 raw bytes suitable for crypto_box.
        public static byte[] DecodeX25519PublicKeyDer(byte[] derBytes)
        {
            if (derBytes == null) throw new ArgumentNullException(nameof(derBytes));
            // If it already comes as raw 32 bytes (degenerate case), return it as-is.
            if (derBytes.Length == 32) return derBytes;

            var spki = Org.BouncyCastle.Asn1.X509.SubjectPublicKeyInfo.GetInstance(derBytes);
            var keyParam = Org.BouncyCastle.Security.PublicKeyFactory.CreateKey(spki);
            if (keyParam is X25519PublicKeyParameters x25519)
                return x25519.GetEncoded();
            throw new ArgumentException(
                $"DER no contiene una pubkey X25519 valida: {keyParam.GetType().Name}");
        }

        // --- Hash + encoding helpers (native managed, unchanged) ---

        public static byte[] ComputeFingerprint(byte[] publicKeyBytes)
        {
            if (publicKeyBytes == null) throw new ArgumentNullException(nameof(publicKeyBytes));
            return SHA256.HashData(publicKeyBytes);
        }

        public static string ToBase64Url(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return Convert.ToBase64String(data)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        public static byte[] FromBase64Url(string base64Url)
        {
            if (base64Url == null) throw new ArgumentNullException(nameof(base64Url));
            string padded = base64Url.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Convert.FromBase64String(padded);
        }

        // --- Internals ---

        private static byte[] X25519Agree(byte[] secKey, byte[] pubKey)
        {
            var agree = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
            agree.Init(new X25519PrivateKeyParameters(secKey, 0));
            byte[] shared = new byte[agree.AgreementSize];
            agree.CalculateAgreement(new X25519PublicKeyParameters(pubKey, 0), shared, 0);
            return shared;
        }

        private static byte[] DeriveSharedKey(byte[] secKey, byte[] pubKey)
        {
            byte[] x25519Shared = X25519Agree(secKey, pubKey);
            return HSalsa20(x25519Shared, new byte[16]);
        }

        // crypto_secretbox(m, n, k): keystream = XSalsa20(k, n) over 32 zeros || m.
        // The first 32 bytes are the mac_key, the rest = ciphertext. tag = Poly1305(mac_key, ct).
        // Output = tag(16) || ciphertext.
        internal static byte[] SecretBox(byte[] plaintext, byte[] nonce, byte[] key)
        {
            int n = plaintext.Length;
            byte[] zerosThenPlain = new byte[32 + n];
            Buffer.BlockCopy(plaintext, 0, zerosThenPlain, 32, n);

            byte[] xored = new byte[32 + n];
            var engine = new XSalsa20Engine();
            engine.Init(true, new ParametersWithIV(new KeyParameter(key), nonce));
            engine.ProcessBytes(zerosThenPlain, 0, 32 + n, xored, 0);

            byte[] macKey = new byte[32];
            Buffer.BlockCopy(xored, 0, macKey, 0, 32);

            byte[] ciphertext = new byte[n];
            Buffer.BlockCopy(xored, 32, ciphertext, 0, n);

            byte[] tag = new byte[16];
            var poly = new Poly1305();
            poly.Init(new KeyParameter(macKey));
            poly.BlockUpdate(ciphertext, 0, n);
            poly.DoFinal(tag, 0);

            byte[] output = new byte[16 + n];
            Buffer.BlockCopy(tag, 0, output, 0, 16);
            Buffer.BlockCopy(ciphertext, 0, output, 16, n);
            return output;
        }

        internal static byte[] SecretBoxOpen(byte[] taggedCiphertext, byte[] nonce, byte[] key)
        {
            if (taggedCiphertext.Length < 16)
                throw new CryptographicException("ciphertext too short");

            int n = taggedCiphertext.Length - 16;
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[n];
            Buffer.BlockCopy(taggedCiphertext, 0, tag, 0, 16);
            Buffer.BlockCopy(taggedCiphertext, 16, ciphertext, 0, n);

            byte[] zerosThenCipher = new byte[32 + n];
            Buffer.BlockCopy(ciphertext, 0, zerosThenCipher, 32, n);

            byte[] xored = new byte[32 + n];
            var engine = new XSalsa20Engine();
            engine.Init(false, new ParametersWithIV(new KeyParameter(key), nonce));
            engine.ProcessBytes(zerosThenCipher, 0, 32 + n, xored, 0);

            byte[] macKey = new byte[32];
            Buffer.BlockCopy(xored, 0, macKey, 0, 32);

            byte[] expectedTag = new byte[16];
            var poly = new Poly1305();
            poly.Init(new KeyParameter(macKey));
            poly.BlockUpdate(ciphertext, 0, n);
            poly.DoFinal(expectedTag, 0);

            if (!CryptographicOperations.FixedTimeEquals(tag, expectedTag))
                throw new CryptographicException("Poly1305 tag mismatch (corrupted or wrong key)");

            byte[] plaintext = new byte[n];
            Buffer.BlockCopy(xored, 32, plaintext, 0, n);
            return plaintext;
        }

        // HSalsa20: NaCl's KDF. Input = 32-byte key, 16-byte nonce. Output = 32 bytes.
        // BC does not expose it publicly (it uses it internally in XSalsa20Engine.init), so
        // we implement it following the Salsa20/20 algorithm without the final addition, output = 8
        // specific words. Validated in tests via cross-check vs Chaos.NaCl.
        internal static byte[] HSalsa20(byte[] key, byte[] nonce16)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("HSalsa20 key must be 32 bytes");
            if (nonce16 == null || nonce16.Length != 16) throw new ArgumentException("HSalsa20 nonce must be 16 bytes");

            uint x0  = 0x61707865;
            uint x1  = LE32(key,  0);
            uint x2  = LE32(key,  4);
            uint x3  = LE32(key,  8);
            uint x4  = LE32(key, 12);
            uint x5  = 0x3320646e;
            uint x6  = LE32(nonce16,  0);
            uint x7  = LE32(nonce16,  4);
            uint x8  = LE32(nonce16,  8);
            uint x9  = LE32(nonce16, 12);
            uint x10 = 0x79622d32;
            uint x11 = LE32(key, 16);
            uint x12 = LE32(key, 20);
            uint x13 = LE32(key, 24);
            uint x14 = LE32(key, 28);
            uint x15 = 0x6b206574;

            unchecked
            {
                for (int i = 0; i < 10; i++)
                {
                    uint y;
                    y = x0  + x12; x4  ^= (y << 7) | (y >> 25);
                    y = x4  + x0;  x8  ^= (y << 9) | (y >> 23);
                    y = x8  + x4;  x12 ^= (y << 13) | (y >> 19);
                    y = x12 + x8;  x0  ^= (y << 18) | (y >> 14);
                    y = x5  + x1;  x9  ^= (y << 7) | (y >> 25);
                    y = x9  + x5;  x13 ^= (y << 9) | (y >> 23);
                    y = x13 + x9;  x1  ^= (y << 13) | (y >> 19);
                    y = x1  + x13; x5  ^= (y << 18) | (y >> 14);
                    y = x10 + x6;  x14 ^= (y << 7) | (y >> 25);
                    y = x14 + x10; x2  ^= (y << 9) | (y >> 23);
                    y = x2  + x14; x6  ^= (y << 13) | (y >> 19);
                    y = x6  + x2;  x10 ^= (y << 18) | (y >> 14);
                    y = x15 + x11; x3  ^= (y << 7) | (y >> 25);
                    y = x3  + x15; x7  ^= (y << 9) | (y >> 23);
                    y = x7  + x3;  x11 ^= (y << 13) | (y >> 19);
                    y = x11 + x7;  x15 ^= (y << 18) | (y >> 14);
                    y = x0  + x3;  x1  ^= (y << 7) | (y >> 25);
                    y = x1  + x0;  x2  ^= (y << 9) | (y >> 23);
                    y = x2  + x1;  x3  ^= (y << 13) | (y >> 19);
                    y = x3  + x2;  x0  ^= (y << 18) | (y >> 14);
                    y = x5  + x4;  x6  ^= (y << 7) | (y >> 25);
                    y = x6  + x5;  x7  ^= (y << 9) | (y >> 23);
                    y = x7  + x6;  x4  ^= (y << 13) | (y >> 19);
                    y = x4  + x7;  x5  ^= (y << 18) | (y >> 14);
                    y = x10 + x9;  x11 ^= (y << 7) | (y >> 25);
                    y = x11 + x10; x8  ^= (y << 9) | (y >> 23);
                    y = x8  + x11; x9  ^= (y << 13) | (y >> 19);
                    y = x9  + x8;  x10 ^= (y << 18) | (y >> 14);
                    y = x15 + x14; x12 ^= (y << 7) | (y >> 25);
                    y = x12 + x15; x13 ^= (y << 9) | (y >> 23);
                    y = x13 + x12; x14 ^= (y << 13) | (y >> 19);
                    y = x14 + x13; x15 ^= (y << 18) | (y >> 14);
                }
            }

            byte[] output = new byte[32];
            LE32ToBytes(x0,  output,  0);
            LE32ToBytes(x5,  output,  4);
            LE32ToBytes(x10, output,  8);
            LE32ToBytes(x15, output, 12);
            LE32ToBytes(x6,  output, 16);
            LE32ToBytes(x7,  output, 20);
            LE32ToBytes(x8,  output, 24);
            LE32ToBytes(x9,  output, 28);
            return output;
        }

        private static uint LE32(byte[] b, int o)
            => (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);

        private static void LE32ToBytes(uint v, byte[] b, int o)
        {
            b[o    ] = (byte)(v       );
            b[o + 1] = (byte)(v >>  8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }
    }
}
