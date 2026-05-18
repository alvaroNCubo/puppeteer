using System;

namespace Choreography.Usher
{
    public sealed class ServerFingerprint : IEquatable<ServerFingerprint>
    {
        public byte[] Sha256 { get; }
        public string Host { get; }
        public int Port { get; }

        public ServerFingerprint(byte[] sha256, string host, int port)
        {
            if (sha256 == null) throw new ArgumentNullException(nameof(sha256));
            if (sha256.Length != 32) throw new ArgumentException("Sha256 fingerprint must be 32 bytes", nameof(sha256));
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentNullException(nameof(host));
            if (port <= 0 || port > 65535) throw new ArgumentException("Port out of range", nameof(port));

            Sha256 = (byte[])sha256.Clone();
            Host = host;
            Port = port;
        }

        public bool Equals(ServerFingerprint other)
        {
            if (other == null) return false;
            if (Host != other.Host) return false;
            if (Port != other.Port) return false;
            if (Sha256.Length != other.Sha256.Length) return false;
            for (int i = 0; i < Sha256.Length; i++)
                if (Sha256[i] != other.Sha256[i]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is ServerFingerprint other && Equals(other);

        public override int GetHashCode()
        {
            int hash = HashCode.Combine(Host, Port);
            foreach (var b in Sha256) hash = HashCode.Combine(hash, b);
            return hash;
        }
    }
}
