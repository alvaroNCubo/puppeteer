using System;
using System.IO;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Choreography.Usher.Crypto
{
    // Libsodium-style sealed box, implemented in BouncyCastle 2.6.2 primitives.
    //
    // A sealed box is anonymous public-key encryption: the sender does not need
    // an identity, only the recipient's public key. The Usher uses it to seal
    // the JournalSecret to the new Stage's StagePublicKey in UsherJoinResponse.
    // Only the holder of the corresponding StagePrivateKey can open the box.
    //
    // The Stage keys are Ed25519 (signature). For encryption we convert them
    // to their X25519 (Montgomery) counterparts on the fly — the standard
    // libsodium pattern that lets one identity key serve both roles.
    //
    // Wire format of a sealed box (the byte[] returned by Seal):
    //
    //   bytes 0..31    : ephemeral X25519 public key (sender, throwaway)
    //   bytes 32..N-17 : AEAD ciphertext
    //   bytes N-16..N-1: Poly1305 tag (16 bytes, AEAD tag)
    //
    // Key derivation:
    //
    //   ephPriv, ephPub   = X25519 keypair, freshly generated per Seal call
    //   recipientX25519Pub = Ed25519ToX25519.PublicKey(recipient.Ed25519 pubkey)
    //   shared            = X25519(ephPriv, recipientX25519Pub)        (32 B)
    //   ikm               = shared || ephPub || recipientX25519Pub     (96 B)
    //   key (32) || nonce (12) = HKDF-SHA256(ikm, salt=∅, info="puppeteer-sealed-box-v1", L=44)
    //   AEAD = ChaCha20-Poly1305(key, nonce, plaintext, aad=ephPub || recipientX25519Pub)
    //
    // The AAD binds the ciphertext to the two public keys that participated
    // in the key agreement, foreclosing replay against a different recipient.
    public sealed class SealedBoxPayloadSealer : IPayloadSealer
    {
        private const int X25519KeySize  = 32;
        private const int ChaCha20KeyLen = 32;
        private const int ChaChaNonceLen = 12;
        private const int Poly1305TagLen = 16;
        private static readonly byte[] HkdfInfo =
            System.Text.Encoding.ASCII.GetBytes("puppeteer-sealed-box-v1");

        public byte[] Seal(byte[] payload, byte[] recipientPublicKey)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (recipientPublicKey == null) throw new ArgumentNullException(nameof(recipientPublicKey));
            if (recipientPublicKey.Length != 32)
                throw new ArgumentException("Recipient public key must be a 32-byte Ed25519 key",
                    nameof(recipientPublicKey));

            byte[] recipientX25519Pub = Ed25519ToX25519.PublicKey(recipientPublicKey);

            // Generate the ephemeral X25519 keypair.
            var x25519Gen = new X25519KeyPairGenerator();
            x25519Gen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
            var ephPair    = x25519Gen.GenerateKeyPair();
            var ephPrivKey = (X25519PrivateKeyParameters)ephPair.Private;
            var ephPubKey  = (X25519PublicKeyParameters)ephPair.Public;
            byte[] ephPub  = ephPubKey.GetEncoded();

            // ECDH.
            var agreement = new X25519Agreement();
            agreement.Init(ephPrivKey);
            byte[] shared = new byte[X25519KeySize];
            agreement.CalculateAgreement(
                new X25519PublicKeyParameters(recipientX25519Pub, 0),
                shared, 0);

            // HKDF → key || nonce.
            (byte[] key, byte[] nonce) = DeriveKeyAndNonce(shared, ephPub, recipientX25519Pub);
            byte[] aad = ConcatAad(ephPub, recipientX25519Pub);

            byte[] ciphertext = ChaChaSeal(key, nonce, payload, aad);

            // Output = ephPub || ciphertext || tag (the tag is appended at the end
            // of ciphertext by ChaCha20-Poly1305 in the BC implementation).
            byte[] output = new byte[X25519KeySize + ciphertext.Length];
            Buffer.BlockCopy(ephPub, 0, output, 0, X25519KeySize);
            Buffer.BlockCopy(ciphertext, 0, output, X25519KeySize, ciphertext.Length);
            return output;
        }

        public byte[] Open(byte[] sealedPayload, byte[] recipientPublicKey, byte[] recipientPrivateKey)
        {
            if (sealedPayload == null) throw new ArgumentNullException(nameof(sealedPayload));
            if (recipientPublicKey == null) throw new ArgumentNullException(nameof(recipientPublicKey));
            if (recipientPrivateKey == null) throw new ArgumentNullException(nameof(recipientPrivateKey));
            if (sealedPayload.Length < X25519KeySize + Poly1305TagLen)
                throw new ArgumentException("Sealed payload too short to contain ephemeral key + AEAD tag",
                    nameof(sealedPayload));

            byte[] ephPub = new byte[X25519KeySize];
            Buffer.BlockCopy(sealedPayload, 0, ephPub, 0, X25519KeySize);

            byte[] recipientX25519Pub  = Ed25519ToX25519.PublicKey(recipientPublicKey);
            byte[] recipientX25519Priv = Ed25519ToX25519.PrivateKey(recipientPrivateKey);

            var agreement = new X25519Agreement();
            agreement.Init(new X25519PrivateKeyParameters(recipientX25519Priv, 0));
            byte[] shared = new byte[X25519KeySize];
            agreement.CalculateAgreement(
                new X25519PublicKeyParameters(ephPub, 0),
                shared, 0);

            (byte[] key, byte[] nonce) = DeriveKeyAndNonce(shared, ephPub, recipientX25519Pub);
            byte[] aad = ConcatAad(ephPub, recipientX25519Pub);

            int ciphertextLen = sealedPayload.Length - X25519KeySize;
            byte[] ciphertext = new byte[ciphertextLen];
            Buffer.BlockCopy(sealedPayload, X25519KeySize, ciphertext, 0, ciphertextLen);

            return ChaChaOpen(key, nonce, ciphertext, aad);
        }

        private static (byte[] key, byte[] nonce) DeriveKeyAndNonce(
            byte[] shared, byte[] ephPub, byte[] recipientPub)
        {
            // ikm = shared || ephPub || recipientPub
            byte[] ikm = new byte[shared.Length + ephPub.Length + recipientPub.Length];
            Buffer.BlockCopy(shared,       0, ikm, 0,                                shared.Length);
            Buffer.BlockCopy(ephPub,       0, ikm, shared.Length,                    ephPub.Length);
            Buffer.BlockCopy(recipientPub, 0, ikm, shared.Length + ephPub.Length,    recipientPub.Length);

            var hkdf = new HkdfBytesGenerator(new Sha256Digest());
            hkdf.Init(new HkdfParameters(ikm, salt: null, info: HkdfInfo));

            byte[] out44 = new byte[ChaCha20KeyLen + ChaChaNonceLen];
            hkdf.GenerateBytes(out44, 0, out44.Length);

            byte[] key   = new byte[ChaCha20KeyLen];
            byte[] nonce = new byte[ChaChaNonceLen];
            Buffer.BlockCopy(out44, 0,                key,   0, ChaCha20KeyLen);
            Buffer.BlockCopy(out44, ChaCha20KeyLen,   nonce, 0, ChaChaNonceLen);
            return (key, nonce);
        }

        private static byte[] ConcatAad(byte[] ephPub, byte[] recipientPub)
        {
            byte[] aad = new byte[ephPub.Length + recipientPub.Length];
            Buffer.BlockCopy(ephPub,       0, aad, 0,               ephPub.Length);
            Buffer.BlockCopy(recipientPub, 0, aad, ephPub.Length,   recipientPub.Length);
            return aad;
        }

        private static byte[] ChaChaSeal(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
        {
            var cipher = new ChaCha20Poly1305();
            cipher.Init(forEncryption: true,
                new ParametersWithIV(new KeyParameter(key), nonce));
            cipher.ProcessAadBytes(aad, 0, aad.Length);

            byte[] output = new byte[cipher.GetOutputSize(plaintext.Length)];
            int len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
            cipher.DoFinal(output, len);
            return output;
        }

        private static byte[] ChaChaOpen(byte[] key, byte[] nonce, byte[] ciphertext, byte[] aad)
        {
            var cipher = new ChaCha20Poly1305();
            cipher.Init(forEncryption: false,
                new ParametersWithIV(new KeyParameter(key), nonce));
            cipher.ProcessAadBytes(aad, 0, aad.Length);

            byte[] output = new byte[cipher.GetOutputSize(ciphertext.Length)];
            int len = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, output, 0);
            try
            {
                cipher.DoFinal(output, len);
            }
            catch (Org.BouncyCastle.Crypto.InvalidCipherTextException ex)
            {
                throw new InvalidDataException("Sealed box AEAD verification failed", ex);
            }
            return output;
        }
    }
}
