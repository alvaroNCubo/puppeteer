using System;
using System.IO;
using System.Security.Cryptography;

namespace Choreography.StageManager
{
    internal static class StageEncryption
    {
        private const string RubiconFileName = "rubicon.enc";
        private const int RubiconContactSize = 32;
        private const int NonceSize = 12;  // AES-GCM nonce
        private const int TagSize = 16;    // AES-GCM tag
        private const int KeySize = 32;    // AES-256

        public static byte[] DeriveJournalKey(string stageStateDirectory, string password)
        {
            if (string.IsNullOrWhiteSpace(stageStateDirectory))
                throw new ArgumentNullException(nameof(stageStateDirectory));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(nameof(password));

            byte[] rubiconContact = LoadRubiconContact(stageStateDirectory, password);

            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: rubiconContact,
                outputLength: KeySize,
                salt: Array.Empty<byte>(),
                info: System.Text.Encoding.UTF8.GetBytes("stage-journal"));
        }

        public static void InitializeRubicon(string stageStateDirectory, string password)
        {
            if (string.IsNullOrWhiteSpace(stageStateDirectory))
                throw new ArgumentNullException(nameof(stageStateDirectory));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(nameof(password));

            string filePath = Path.Combine(stageStateDirectory, RubiconFileName);
            if (File.Exists(filePath))
                throw new InvalidOperationException("Rubicon contact already initialized");

            byte[] rubiconContact = RandomNumberGenerator.GetBytes(RubiconContactSize);
            SaveRubiconContact(stageStateDirectory, password, rubiconContact);
        }

        public static void UpdateRubiconContact(string stageStateDirectory, string password, byte[] newRubiconContact)
        {
            if (string.IsNullOrWhiteSpace(stageStateDirectory))
                throw new ArgumentNullException(nameof(stageStateDirectory));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(nameof(password));
            if (newRubiconContact == null) throw new ArgumentNullException(nameof(newRubiconContact));

            SaveRubiconContact(stageStateDirectory, password, newRubiconContact);
        }

        public static bool RubiconExists(string stageStateDirectory)
        {
            if (string.IsNullOrWhiteSpace(stageStateDirectory)) return false;
            return File.Exists(Path.Combine(stageStateDirectory, RubiconFileName));
        }

        // --- Private ---

        private static byte[] LoadRubiconContact(string stageStateDirectory, string password)
        {
            string filePath = Path.Combine(stageStateDirectory, RubiconFileName);
            if (!File.Exists(filePath))
                throw new InvalidOperationException(
                    "Rubicon contact not initialized. Call InitializeRubicon first.");

            byte[] encrypted = File.ReadAllBytes(filePath);
            return DecryptWithPassword(encrypted, password);
        }

        private static void SaveRubiconContact(string stageStateDirectory, string password, byte[] rubiconContact)
        {
            Directory.CreateDirectory(stageStateDirectory);
            string filePath = Path.Combine(stageStateDirectory, RubiconFileName);

            byte[] encrypted = EncryptWithPassword(rubiconContact, password);
            File.WriteAllBytes(filePath, encrypted);
        }

        // AES-256-GCM encrypt with key derived from password
        // Format: [nonce(12) | tag(16) | ciphertext(N)]
        private static byte[] EncryptWithPassword(byte[] plaintext, string password)
        {
            byte[] key = PasswordToKey(password);
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            byte[] result = new byte[NonceSize + TagSize + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);
            return result;
        }

        private static byte[] DecryptWithPassword(byte[] encrypted, string password)
        {
            if (encrypted.Length < NonceSize + TagSize)
                throw new InvalidOperationException("Encrypted data too short");

            byte[] key = PasswordToKey(password);
            byte[] nonce = new byte[NonceSize];
            byte[] tag = new byte[TagSize];
            int ciphertextLength = encrypted.Length - NonceSize - TagSize;
            byte[] ciphertext = new byte[ciphertextLength];
            byte[] plaintext = new byte[ciphertextLength];

            Buffer.BlockCopy(encrypted, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(encrypted, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(encrypted, NonceSize + TagSize, ciphertext, 0, ciphertextLength);

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }

        private static byte[] PasswordToKey(string password)
        {
            return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
        }
    }
}
