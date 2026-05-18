using System;
using System.Text;
using System.Text.Json;
using Choreography.StageManager;
using Choreography.Transport;

namespace Choreography.Usher
{
    // Encodes / decodes a UsherInvitation as a single short string suitable for
    // rendering as a QR code, pasting into a chat, dictating over the phone, or
    // printing on paper. The wire format is a URI:
    //
    //   puppeteer-usher://v1/{base64url(json)}
    //
    // Where json is the compact serialisation of UsherShareLinkPayload (one
    // line, ASCII only). base64url keeps the payload safe to put in URLs and
    // QR codes without further escaping.
    //
    // The scheme is versioned (v1) so future encodings can be introduced
    // without breaking compat — a decoder that sees a version it does not
    // recognise rejects the link cleanly.
    public interface IUsherShareLinkEncoder
    {
        // Encodes the invitation into a single QR-friendly string. If
        // serverCertFingerprintHex is provided (lowercase SHA-256 hex of the
        // Usher's TLS server certificate), the joiner can pin that
        // fingerprint at TLS handshake time. Paper 7 Phase 2 always passes it.
        string Encode(UsherInvitation invitation, string serverCertFingerprintHex = null);

        // Returns the embedded UsherInvitation. The fingerprint, if it was
        // present in the encoded payload, is available via DecodeFingerprint.
        UsherInvitation Decode(string shareLink);

        // Returns the SHA-256 hex fingerprint that was embedded in the
        // share-link by Encode(..., fingerprint), or null if absent.
        string DecodeFingerprint(string shareLink);
    }

    public sealed class UsherShareLinkEncoder : IUsherShareLinkEncoder
    {
        private const string SchemePrefix = "puppeteer-usher://v1/";

        public string Encode(UsherInvitation invitation, string serverCertFingerprintHex = null)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            var payload = new UsherShareLinkPayload
            {
                v       = 1,
                nonce   = invitation.Nonce.ToString("N"),
                inviter = invitation.TransportInvitation.InviterId.ToString(),
                purpose = (byte)invitation.TransportInvitation.Purpose,
                addr    = invitation.TransportInvitation.Address,
                ttl     = invitation.ExpiresAt.ToUniversalTime().ToString("O"),
                fp      = string.IsNullOrWhiteSpace(serverCertFingerprintHex)
                            ? null
                            : serverCertFingerprintHex.ToLowerInvariant()
            };

            string json = JsonSerializer.Serialize(payload, JsonOpts);
            string b64u = Base64Url.Encode(Encoding.UTF8.GetBytes(json));
            return SchemePrefix + b64u;
        }

        public string DecodeFingerprint(string shareLink)
        {
            UsherShareLinkPayload payload = DecodePayload(shareLink);
            return payload.fp;
        }

        public UsherInvitation Decode(string shareLink)
        {
            UsherShareLinkPayload payload = DecodePayload(shareLink);

            Guid nonce       = Guid.ParseExact(payload.nonce, "N");
            Guid inviterGuid = Guid.Parse(payload.inviter);
            var inviterId    = new PerformerId(inviterGuid);
            var purpose      = (ChannelPurpose)payload.purpose;
            var address      = payload.addr;
            DateTime ttl     = DateTime.Parse(payload.ttl, System.Globalization.CultureInfo.InvariantCulture,
                                              System.Globalization.DateTimeStyles.RoundtripKind);

            var transportInv = new ConnectionInvitation(inviterId, purpose, address);
            return new UsherInvitation(nonce, transportInv, ttl);
        }

        private static UsherShareLinkPayload DecodePayload(string shareLink)
        {
            if (shareLink == null) throw new ArgumentNullException(nameof(shareLink));
            if (!shareLink.StartsWith(SchemePrefix, StringComparison.Ordinal))
                throw new FormatException(
                    $"Share link must start with {SchemePrefix}; got: {Truncate(shareLink, 40)}");

            string b64u = shareLink.Substring(SchemePrefix.Length);
            byte[] jsonBytes;
            try { jsonBytes = Base64Url.Decode(b64u); }
            catch (FormatException ex)
            {
                throw new FormatException("Share link body is not valid base64url", ex);
            }

            UsherShareLinkPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<UsherShareLinkPayload>(jsonBytes, JsonOpts);
            }
            catch (JsonException ex)
            {
                throw new FormatException("Share link body is not valid JSON", ex);
            }

            if (payload == null)
                throw new FormatException("Share link decoded to null payload");
            if (payload.v != 1)
                throw new FormatException($"Unsupported share-link version: {payload.v}");
            if (string.IsNullOrWhiteSpace(payload.nonce) ||
                string.IsNullOrWhiteSpace(payload.inviter) ||
                string.IsNullOrWhiteSpace(payload.addr) ||
                string.IsNullOrWhiteSpace(payload.ttl))
                throw new FormatException("Share link payload is missing required fields");

            return payload;
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = null
        };

        private static string Truncate(string s, int n)
            => s.Length <= n ? s : s.Substring(0, n) + "...";

        // The compact JSON shape that lives inside the URI. Field names are
        // intentionally short to keep the QR small. Lowercase to match libsodium-
        // adjacent conventions and to avoid name-case surprises across JSON
        // libraries.
        private sealed class UsherShareLinkPayload
        {
            public int    v       { get; set; }
            public string nonce   { get; set; }
            public string inviter { get; set; }
            public byte   purpose { get; set; }
            public string addr    { get; set; }
            public string ttl     { get; set; }
            // Optional in v1 — present when the invitation goes over HTTPS
            // and the joiner needs to TOFU-pin the Usher's server cert.
            public string fp      { get; set; }
        }
    }

    // Minimal RFC 4648 §5 base64url encoder/decoder (no padding). Kept private
    // to the Usher namespace so it does not collide with any other helper that
    // might land in the assembly later.
    internal static class Base64Url
    {
        public static string Encode(byte[] data)
        {
            string b64 = Convert.ToBase64String(data);
            return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static byte[] Decode(string s)
        {
            string b64 = s.Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "=";  break;
                case 1: throw new FormatException("Invalid base64url length");
            }
            return Convert.FromBase64String(b64);
        }
    }
}
