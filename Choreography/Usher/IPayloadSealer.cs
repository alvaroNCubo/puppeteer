using System;

namespace Choreography.Usher
{
    // "Sealed box" abstraction (libsodium-style): anonymous encryption where the sender
    // does not need an identity, only the recipient's pubkey. The recipient decrypts with
    // their privkey.
    //
    // The Usher uses it to seal the JournalSecret to the new Stage's StagePublicKey
    // inside UsherJoinResponse. Only the owner of the StagePrivateKey can open it,
    // and that is exactly the Stage that requested syndication.
    //
    // Scaffold: the real implementation uses X25519 + ChaCha20-Poly1305 (BouncyCastle).
    // The E2E test injects a pass-through sealer that records (payload, recipientPubKey)
    // without truly encrypting, to validate the protocol without pinning a crypto library.
    public interface IPayloadSealer
    {
        byte[] Seal(byte[] payload, byte[] recipientPublicKey);
        byte[] Open(byte[] sealedPayload, byte[] recipientPublicKey, byte[] recipientPrivateKey);
    }
}
