using System;
using System.Security.Cryptography;

namespace Choreography.Usher
{
    // Par de claves Ed25519 (32 bytes public, 32 bytes private). El StagePublicKey va
    // dentro de UsherJoinRequest. El StagePrivateKey nunca sale del dispositivo Stage.
    //
    // Decision D7: el JoinRequest se firma con StagePrivateKey para tener no-repudio
    // auditable. La firma vive sobre la concatenacion (nonce || pubkey || ts).
    //
    // Decision D5: StageId = SHA-256(StagePublicKey)[..16] (PerformerId 16 bytes).
    // El derivation es publico y deterministico, asi el Usher puede recomputar el
    // StageId desde el pubkey recibido sin pedirlo al Stage.
    //
    // Scaffold: la generacion real con Ed25519 va detras de IStageKeyGenerator para
    // que el test E2E pueda inyectar pares deterministicos. El handoff doc explica
    // que producion usa BouncyCastle.Crypto.Ed25519.
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
