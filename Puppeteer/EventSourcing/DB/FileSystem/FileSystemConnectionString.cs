using System;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
    internal sealed class FileSystemConnectionString
    {
        internal string Path { get; }
        internal int MaxFileSizeBytes { get; }
        internal PayloadCompression Compression { get; }
        internal EncryptionMode Encryption { get; }

        // AES-256 key for an encrypted journal, base64 in the connection string.
        // Null when absent. Decoded here and then kept apart from the raw string,
        // because it is a secret: it must not reach a log line, an exception message,
        // or the store-identity comparison in DiaryStorage.IsSameStoreAs. Redact
        // strips it from the string a store retains.
        internal byte[] EncryptionKey { get; }

        internal const string ENCRYPTION_KEY_SETTING = "encryptionKey";

        private const int AES_256_KEY_BYTES = 32;
        private const int DEFAULT_MAX_FILE_SIZE = 4 * 1024 * 1024;

        internal FileSystemConnectionString(string connectionString)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

            if (!connectionString.Contains('='))
            {
                // Backward-compatible: the whole string is the path
                Path = connectionString;
                MaxFileSizeBytes = DEFAULT_MAX_FILE_SIZE;
                Compression = PayloadCompression.None;
                Encryption = EncryptionMode.None;
                return;
            }

            MaxFileSizeBytes = DEFAULT_MAX_FILE_SIZE;
            Compression = PayloadCompression.None;
            Encryption = EncryptionMode.None;

            foreach (string segment in connectionString.Split(';'))
            {
                int eq = segment.IndexOf('=');
                if (eq < 0) continue;

                string key = segment[..eq].Trim();
                string value = segment[(eq + 1)..].Trim();

                if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
                    Path = value;
                else if (key.Equals("maxFileSize", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out int size) && size > 0)
                        MaxFileSizeBytes = size;
                }
                else if (key.Equals("compression", StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<PayloadCompression>(value, ignoreCase: true, out var comp))
                        Compression = comp;
                }
                else if (key.Equals("encryption", StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse<EncryptionMode>(value, ignoreCase: true, out var enc))
                        Encryption = enc;
                }
                else if (key.Equals(ENCRYPTION_KEY_SETTING, StringComparison.OrdinalIgnoreCase))
                {
                    EncryptionKey = ParseEncryptionKey(value);
                }
            }

            if (Path == null)
                throw new ArgumentException("FileSystem connection string must include 'path=<directory>'.", nameof(connectionString));
        }

        // The connection string minus the key setting. What a store retains long-term
        // (and compares against another store to decide whether they are the same
        // physical store) must not carry a secret: the key says nothing about WHICH
        // store this is, only about how to read it, so two configurations that differ
        // only in the key do denote the same store.
        internal static string Redact(string connectionString)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            if (connectionString.IndexOf(ENCRYPTION_KEY_SETTING, StringComparison.OrdinalIgnoreCase) < 0)
                return connectionString;

            var kept = new List<string>();
            foreach (string segment in connectionString.Split(';'))
            {
                int eq = segment.IndexOf('=');
                if (eq >= 0 && segment[..eq].Trim().Equals(ENCRYPTION_KEY_SETTING, StringComparison.OrdinalIgnoreCase))
                    continue;

                kept.Add(segment);
            }

            return string.Join(";", kept);
        }

        // A malformed key is rejected here rather than carried as null, so the failure
        // names the real cause. The messages describe the SHAPE only — never the value,
        // and never the offending text, which is the secret itself.
        private static byte[] ParseEncryptionKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new LanguageException(
                    $"'{ENCRYPTION_KEY_SETTING}' is present in the FileSystem connection string but empty. "
                    + "Supply a base64-encoded 32-byte AES-256 key, or remove the setting.");

            byte[] key;
            try
            {
                key = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                throw new LanguageException(
                    $"'{ENCRYPTION_KEY_SETTING}' in the FileSystem connection string is not valid base64. "
                    + "Supply a base64-encoded 32-byte AES-256 key.");
            }

            if (key.Length != AES_256_KEY_BYTES)
                throw new LanguageException(
                    $"'{ENCRYPTION_KEY_SETTING}' in the FileSystem connection string decodes to {key.Length} bytes; "
                    + $"AES-256 requires exactly {AES_256_KEY_BYTES}.");

            return key;
        }
    }
}
