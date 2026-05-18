using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Choreography.Usher.Crypto
{
    // Production-grade Ed25519 keypair generator. Replaces the DeterministicKeyGenerator
    // fake used in UsherOnboardingTests; that fake exists for reproducibility under
    // test, not for production. Paper 7 Phase 2 uses this real generator end-to-end
    // including the cross-container syndicalization flow.
    public sealed class Ed25519StageKeyGenerator : IStageKeyGenerator
    {
        public StageKeyPair Generate()
        {
            var gen = new Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var pair = gen.GenerateKeyPair();

            var priv = (Ed25519PrivateKeyParameters)pair.Private;
            var pub = (Ed25519PublicKeyParameters)pair.Public;

            return new StageKeyPair(pub.GetEncoded(), priv.GetEncoded());
        }
    }
}
