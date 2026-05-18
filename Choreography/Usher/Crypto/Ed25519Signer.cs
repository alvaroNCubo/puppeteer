using System;
using Org.BouncyCastle.Crypto.Parameters;

namespace Choreography.Usher.Crypto
{
    // Production-grade Ed25519 signer. Signs payload bytes with a 32-byte Ed25519
    // private key and emits a 64-byte signature. Replaces the ToyEd25519Signer
    // (HMAC-SHA256) used in UsherOnboardingTests.
    //
    // Note: the type name uses "Stage" to avoid colliding with BouncyCastle's
    // Org.BouncyCastle.Crypto.Signers.Ed25519Signer.
    public sealed class Ed25519StageSigner : IStageSigner
    {
        public byte[] Sign(byte[] privateKey, byte[] payload)
        {
            if (privateKey == null) throw new ArgumentNullException(nameof(privateKey));
            if (privateKey.Length != 32)
                throw new ArgumentException("Ed25519 private key must be 32 bytes", nameof(privateKey));
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            var priv = new Ed25519PrivateKeyParameters(privateKey, 0);
            var bcSigner = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            bcSigner.Init(forSigning: true, priv);
            bcSigner.BlockUpdate(payload, 0, payload.Length);
            return bcSigner.GenerateSignature();
        }
    }
}
