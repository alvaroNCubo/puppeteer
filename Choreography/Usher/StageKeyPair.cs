using System;
using System.Security.Cryptography;

namespace Choreography.Usher
{
    // Ed25519 key pair (32 bytes public, 32 bytes private). The StagePublicKey goes
    // inside UsherJoinRequest. The StagePrivateKey never leaves the Stage device.
    //
    // Decision D7: the JoinRequest is signed with StagePrivateKey to provide auditable
    // non-repudiation. The signature lives over the concatenation (nonce || pubkey || ts).
    //
    // Decision D5: StageId = SHA-256(StagePublicKey)[..16] (PerformerId 16 bytes).
    // The derivation is public and deterministic, so the Usher can recompute the
    // StageId from the received pubkey without asking the Stage for it.
    //
    // Scaffold: the real Ed25519 generation goes behind IStageKeyGenerator so
    // the E2E test can inject deterministic pairs. The handoff doc explains
    // that production uses BouncyCastle.Crypto.Ed25519.
    public sealed class StageKeyPair
    {
        public byte[] PublicKey { get; }
        public byte[] PrivateKey { get; }

        public StageKeyPair(byte[] publicKey, byte[] privateKey)
        {
            if (publicKey == null) throw new ArgumentNullException(nameof(publicKey));
            if (privateKey == null) throw new ArgumentNullException(nameof(privateKey));
            if (publicKey.Length != 32) throw new ArgumentException("PublicKey must be 32 bytes", nameof(publicKey));
            if (privateKey.Length != 32 && privateKey.Length != 64)
                throw new ArgumentException("PrivateKey must be 32 or 64 bytes", nameof(privateKey));

            PublicKey = (byte[])publicKey.Clone();
            PrivateKey = (byte[])privateKey.Clone();
        }
    }

    public interface IStageKeyGenerator
    {
        StageKeyPair Generate();
    }

    public interface IStageSignatureVerifier
    {
        bool Verify(byte[] publicKey, byte[] payload, byte[] signature);
    }

    public interface IStageSigner
    {
        byte[] Sign(byte[] privateKey, byte[] payload);
    }
}
