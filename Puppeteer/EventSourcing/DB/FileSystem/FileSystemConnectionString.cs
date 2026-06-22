using System;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
    internal sealed class FileSystemConnectionString
    {
        internal string Path { get; }
        internal int MaxFileSizeBytes { get; }
        internal PayloadCompression Compression { get; }
        internal EncryptionMode Encryption { get; }

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
            }

            if (Path == null)
                throw new ArgumentException("FileSystem connection string must include 'path=<directory>'.", nameof(connectionString));
        }
    }
}
