using System;

namespace Choreography.Usher
{
    // Abstraccion de "sealed box" (libsodium-style): cifrado anonimo donde el sender
    // no necesita identidad, solo la pubkey del receptor. El receptor desencripta con
    // su privkey.
    //
    // Lo usa el Usher para sellar el JournalSecret a la StagePublicKey del nuevo Kora
    // dentro de UsherJoinResponse. Solo el dueno de la StagePrivateKey puede abrirlo,
    // y eso es exactamente el Kora que pidio la sindicalizacion.
    //
    // Scaffold: la implementacion real usa X25519 + ChaCha20-Poly1305 (BouncyCastle).
    // El test E2E inyecta un sealer pass-through que registra (payload, recipientPubKey)
    // sin cifrar de verdad, para validar el protocolo sin pinear una libreria crypto.
    public interface IPayloadSealer
    {
        byte[] Seal(byte[] payload, byte[] recipientPublicKey);
        byte[] Open(byte[] sealedPayload, byte[] recipientPublicKey, byte[] recipientPrivateKey);
    }
}
