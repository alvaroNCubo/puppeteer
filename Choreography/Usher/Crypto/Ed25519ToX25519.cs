using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Choreography.Usher.Crypto
{
    // Converts an Ed25519 key pair to its X25519 (curve25519, Montgomery form)
    // counterpart, mirroring libsodium's crypto_sign_ed25519_{pk,sk}_to_curve25519.
    //
    // This conversion is the standard library trick that lets a single Ed25519
    // identity key serve as both a signature key (Edwards curve, Ed25519Signer)
    // and an encryption key (Montgomery curve, X25519). The Usher publishes the
    // Ed25519 public key on the wire; both the sender (Usher) and the receiver
    // (Kora) derive the X25519 form locally before they perform sealed-box
    // encryption.
    //
    // Algorithm references:
    //   - public key: Edwards y → Montgomery u = (1+y)/(1-y) mod p
    //                where p = 2^255 - 19
    //   - private key: x25519_priv = SHA-512(ed25519_seed)[0..32] with the
    //                  standard clamping (lowest 3 bits of byte 0 cleared,
    //                  bit 6 of byte 31 set, bit 7 of byte 31 cleared)
    //
    // Both operations are well-defined and reversible; the same Ed25519 key
    // produces the same X25519 key every time.
    internal static class Ed25519ToX25519
    {
        // p = 2^255 - 19, the curve25519 field prime.
        private static readonly BigInteger P =
            (BigInteger.One << 255) - 19;

        // Convert a 32-byte Ed25519 public key (compressed Edwards point) to
        // a 32-byte X25519 public key (Montgomery u-coordinate).
        public static byte[] PublicKey(byte[] ed25519PublicKey)
        {
            if (ed25519PublicKey == null) throw new ArgumentNullException(nameof(ed25519PublicKey));
            if (ed25519PublicKey.Length != 32)
                throw new ArgumentException("Ed25519 public key must be 32 bytes", nameof(ed25519PublicKey));

            // Ed25519 pubkey encoding: little-endian 255-bit y-coordinate;
            // bit 7 of byte 31 is the parity of x (we don't need x for the
            // conversion, just y).
            byte[] yBytes = (byte[])ed25519PublicKey.Clone();
            yBytes[31] &= 0x7F;                       // strip the x-sign bit
            BigInteger y = ToLittleEndianBigInt(yBytes);

            // u = (1 + y) * modInverse(1 - y) mod p
            BigInteger oneMinusY = ModPositive(BigInteger.One - y, P);
            BigInteger inv = ModInverse(oneMinusY, P);
            BigInteger u = ModPositive((BigInteger.One + y) * inv, P);

            return FromLittleEndianBigInt(u, 32);
        }

        // Convert a 32-byte Ed25519 private key (the seed) to a 32-byte
        // X25519 private key. The Ed25519 expanded private key (64 bytes,
        // used for signing) is also derivable from the same seed but is not
        // needed for ECDH.
        public static byte[] PrivateKey(byte[] ed25519PrivateKey)
        {
            if (ed25519PrivateKey == null) throw new ArgumentNullException(nameof(ed25519PrivateKey));
            if (ed25519PrivateKey.Length != 32)
                throw new ArgumentException(
                    "Ed25519 private key must be the 32-byte seed (not the 64-byte expanded form)",
                    nameof(ed25519PrivateKey));

            byte[] hash;
            using (var sha = SHA512.Create())
                hash = sha.ComputeHash(ed25519PrivateKey);

            byte[] x = new byte[32];
            Buffer.BlockCopy(hash, 0, x, 0, 32);
            // Standard X25519 clamping.
            x[0]  &= 248;
            x[31] &= 127;
            x[31] |= 64;
            return x;
        }

        private static BigInteger ToLittleEndianBigInt(byte[] le)
        {
            // BigInteger ctor expects little-endian + a trailing zero byte to
            // force a positive interpretation.
            byte[] buf = new byte[le.Length + 1];
            Buffer.BlockCopy(le, 0, buf, 0, le.Length);
            return new BigInteger(buf);
        }

        private static byte[] FromLittleEndianBigInt(BigInteger value, int length)
        {
            byte[] le = value.ToByteArray();
            byte[] result = new byte[length];
            int n = Math.Min(le.Length, length);
            Buffer.BlockCopy(le, 0, result, 0, n);
            return result;
        }

        private static BigInteger ModPositive(BigInteger x, BigInteger m)
        {
            BigInteger r = x % m;
            return r.Sign < 0 ? r + m : r;
        }

        // Extended Euclidean algorithm for modular inverse — the inputs we
        // pass (curve25519 field elements) are always coprime to p (a prime),
        // so the gcd is always 1.
        private static BigInteger ModInverse(BigInteger a, BigInteger m)
        {
            BigInteger old_r = a, r = m;
            BigInteger old_s = 1, s = 0;
            while (r != 0)
            {
                BigInteger q = old_r / r;
                (old_r, r) = (r, old_r - q * r);
                (old_s, s) = (s, old_s - q * s);
            }
            return ModPositive(old_s, m);
        }
    }
}
