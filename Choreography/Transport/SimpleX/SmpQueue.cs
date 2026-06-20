using System;
using System.Text.RegularExpressions;

namespace Choreography.Transport.SimpleX
{
    internal enum SmpQueueRole { Recipient, Sender }
    internal enum SmpQueueState { Created, Secured, Active, Suspended }

    internal sealed class SmpQueue
    {
        public string ServerHost { get; }
        public int ServerPort { get; }
        public byte[] ServerFingerprint { get; set; }

        public byte[] RecipientId { get; set; }
        public byte[] SenderId { get; set; }

        // Recipient keys (queue creator owns these)
        public byte[] RecipientSignPublicKey { get; set; }
        public byte[] RecipientSignSecretKey { get; set; }
        public byte[] RecipientDhPublicKey { get; set; }
        public byte[] RecipientDhSecretKey { get; set; }

        // Sender keys (set when securing the queue)
        public byte[] SenderSignPublicKey { get; set; }
        public byte[] SenderSignSecretKey { get; set; }
        public byte[] SenderDhPublicKey { get; set; }
        public byte[] SenderDhSecretKey { get; set; }

        // Server DH public key (from IDS response)
        public byte[] ServerDhPublicKey { get; set; }

        public SmpQueueState State { get; set; }
        public SmpQueueRole Role { get; set; }

        public SmpQueue(string serverHost, int serverPort = 5223)
        {
            if (string.IsNullOrWhiteSpace(serverHost)) throw new ArgumentNullException(nameof(serverHost));
            ServerHost = serverHost;
            ServerPort = serverPort;
            State = SmpQueueState.Created;
        }

        // Generate invitation URI for sharing (QR code)
        // Format: smp://<fingerprint>@<host>:<port>/<senderId>#/<recipientDhPubKey>
        public string ToInvitationUri()
        {
            if (SenderId == null) throw new InvalidOperationException("SenderId not set");
            if (RecipientDhPublicKey == null) throw new InvalidOperationException("RecipientDhPublicKey not set");

            string fp = ServerFingerprint != null ? SmpCrypto.ToBase64Url(ServerFingerprint) + "@" : "";
            string sid = SmpCrypto.ToBase64Url(SenderId);
            string dhKey = SmpCrypto.ToBase64Url(RecipientDhPublicKey);

            string port = ServerPort != 5223 ? $":{ServerPort}" : "";
            return $"smp://{fp}{ServerHost}{port}/{sid}#/{dhKey}";
        }

        // Parse invitation URI back to queue reference
        // smp://[fingerprint@]host[:port]/senderId#/dhKey
        private static readonly Regex UriPattern = new(
            @"^smp://(?:(?<fp>[A-Za-z0-9_-]+)@)?(?<host>[^:/]+)(?::(?<port>\d+))?/(?<sid>[A-Za-z0-9_-]+)#/(?<dh>[A-Za-z0-9_-]+)$",
            RegexOptions.Compiled);

        public static SmpQueue FromInvitationUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) throw new ArgumentNullException(nameof(uri));

            var match = UriPattern.Match(uri);
            if (!match.Success)
                throw new ArgumentException($"Invalid SMP invitation URI: {uri}");

            string host = match.Groups["host"].Value;
            int port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value) : 5223;

            var queue = new SmpQueue(host, port)
            {
                Role = SmpQueueRole.Sender,
                SenderId = SmpCrypto.FromBase64Url(match.Groups["sid"].Value),
                RecipientDhPublicKey = SmpCrypto.FromBase64Url(match.Groups["dh"].Value)
            };

            if (match.Groups["fp"].Success)
                queue.ServerFingerprint = SmpCrypto.FromBase64Url(match.Groups["fp"].Value);

            return queue;
        }
    }
}
