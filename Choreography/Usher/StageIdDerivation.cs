using System;
using System.Security.Cryptography;
using Choreography.StageManager;

namespace Choreography.Usher
{
    // Decision D5: StageId is deterministically derived from the StagePublicKey.
    // - Self-certifying: the Usher recomposes the same Id from the received pubkey without
    //   having to ask the Stage "what is your name"; any peer that later sees the
    //   pubkey in the journal can verify that the Id corresponds.
    // - Lets the Usher write the MembershipRecord with the StageId and discard the
    //   pubkey from memory (the pubkey lives in the journal, not in the Usher's state).
    //   This preserves the invariant "Usher does not know the exact list of pubkeys of
    //   StageManagers" after the handshake.
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
