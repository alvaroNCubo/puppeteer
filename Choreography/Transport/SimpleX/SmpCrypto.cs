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
    // Reemplaza Sodium.Core para que Choreography corra en MAUI Android/iOS donde
    // Sodium no provee binarios nativos. Toda la crypto es managed via BouncyCastle
    // o System.Security.Cryptography (SHA256 nativo .NET).
    //
    // crypto_box (X25519 + XSalsa20-Poly1305) se implementa sobre primitivas BC:
    //   - X25519Agreement para shared secret
    //   - HSalsa20 manual para derivar shared_key (BC no expone HSalsa20 por separado)
    //   - XSalsa20Engine + Poly1305 para secretbox
    //
    // La correctitud de HSalsa20 + crypto_box se valida en tests via cross-check
    // contra Chaos.NaCl (NetSparkleUpdater fork) — produce el mismo output byte-exacto.
    internal static class SmpCrypto
    {
        public const int NonceSize = 24;
        public const int MacSize = 16;

        private static readonly Org.BouncyCastle.Security.SecureRandom Rng = new();

        // --- Ed25519 signing ---
        // Sodium expone secret key como 64 bytes (32-byte seed || 32-byte public).
        // BC usa solo el 32-byte seed. Mantenemos el formato Sodium-style en la API
        // para no romper datos persistidos; internamente extraemos los primeros 32 bytes.

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
        // simplexmq Crypto.hs encodePubKey = encodeASNObj . publicToX509 — las keys
        // del wire SMP NO van como 32 bytes raw, van envueltas en ASN.1 DER X.509 SPKI.
        // Para Ed25519: ~44 bytes (OID 1.3.101.112). Para X25519: ~44 bytes (OID 1.3.101.110).

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

        // Decodifica una pubkey X25519 envuelta en ASN.1 DER X.509 SubjectPublicKeyInfo
        // (formato que el servidor SMP envia para ServerDhPublicKey en IDS) y devuelve
        // los 32 bytes raw aptos para crypto_box.
        public static byte[] DecodeX25519PublicKeyDer(byte[] derBytes)
        {
            if (derBytes == null) throw new ArgumentNullException(nameof(derBytes));
            // Si ya viene en formato raw 32 bytes (caso degenerado), devolverlo tal cual.
            if (derBytes.Length == 32) return derBytes;

            var spki = Org.BouncyCastle.Asn1.X509.SubjectPublicKeyInfo.GetInstance(derBytes);
            var keyParam = Org.BouncyCastle.Security.PublicKeyFactory.CreateKey(spki);
            if (keyParam is X25519PublicKeyParameters x25519)
                return x25519.GetEncoded();
            throw new ArgumentException(
                $"DER no contiene una pubkey X25519 valida: {keyParam.GetType().Name}");
        }

        // --- Hash + encoding helpers (managed nativo, no cambian) ---

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

        // crypto_secretbox(m, n, k): keystream = XSalsa20(k, n) sobre 32 zeros || m.
        // Primeros 32 bytes son mac_key, resto = ciphertext. tag = Poly1305(mac_key, ct).
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

        // HSalsa20: KDF de NaCl. Input = 32-byte key, 16-byte nonce. Output = 32 bytes.
        // BC no la expone publica (la usa internamente en XSalsa20Engine.init), asi que
        // la implementamos siguiendo el algoritmo Salsa20/20 sin suma final, output = 8
        // words especificos. Validado en tests via cross-check vs Chaos.NaCl.
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
