using System;
using Org.BouncyCastle.Crypto.Parameters;

namespace Choreography.Usher.Crypto
{
    // Production-grade Ed25519 signature verifier. Verifies a 64-byte signature
    // against a 32-byte Ed25519 public key and an arbitrary payload. Replaces
    // the ToyEd25519SignatureVerifier used in UsherOnboardingTests.
    public sealed class Ed25519StageSignatureVerifier : IStageSignatureVerifier
    {
        public bool Verify(byte[] publicKey, byte[] payload, byte[] signature)
        {
            if (publicKey == null) throw new ArgumentNullException(nameof(publicKey));
            if (publicKey.Length != 32)
                throw new ArgumentException("Ed25519 public key must be 32 bytes", nameof(publicKey));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (signature == null) throw new ArgumentNullException(nameof(signature));
            if (signature.Length != 64)
                return false;  // any non-64-byte input is not a valid Ed25519 sig.

            var pub = new Ed25519PublicKeyParameters(publicKey, 0);
            var bcSigner = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            bcSigner.Init(forSigning: false, pub);
            bcSigner.BlockUpdate(payload, 0, payload.Length);
            return bcSigner.VerifySignature(signature);
        }
    }
}
