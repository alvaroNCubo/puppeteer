using System;
using System.Security.Cryptography;
using Choreography.StageManager;

namespace Choreography.Usher
{
    // Decision D5: StageId determinista derivado del StagePublicKey.
    // - Self-certifying: el Usher recompoone el mismo Id desde el pubkey recibido sin
    //   tener que pedirle al Stage "como te llamas"; cualquier peer que despues vea el
    //   pubkey en el journal puede verificar que el Id corresponde.
    // - Permite que el Usher escriba el MembershipRecord con el StageId y descarte el
    //   pubkey en memoria (el pubkey vive en el journal, no en el estado del Usher).
    //   Asi se mantiene el invariante "Usher desconoce la lista exacta de pubkeys de
    //   StageManagers" tras el handshake.
    public static class StageIdDerivation
    {
        public static PerformerId FromPublicKey(byte[] stagePublicKey)
        {
            if (stagePublicKey == null) throw new ArgumentNullException(nameof(stagePublicKey));
            if (stagePublicKey.Length != 32) throw new ArgumentException("StagePublicKey must be 32 bytes Ed25519", nameof(stagePublicKey));

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stagePublicKey);
            byte[] first16 = new byte[16];
            Array.Copy(hash, 0, first16, 0, 16);
            return PerformerId.From(first16);
        }
    }
}
