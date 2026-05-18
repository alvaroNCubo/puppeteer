using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Choreography.Transport.Https
{
    // Constructs an HttpClient whose TLS handshake validates the server
    // certificate against a pinned SHA-256 fingerprint (TOFU). If the
    // fingerprint argument is null, the client accepts any cert — used only
    // by tests against the loopback Kestrel listener where the cert
    // identity is irrelevant; production callers always pass a fingerprint.
    internal static class HttpsClientFactory
    {
        public static HttpClient BuildClient(string pinnedFingerprintHex)
        {
            var handler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    {
                        if (cert == null) return false;
                        if (pinnedFingerprintHex == null)
                            return true;   // unpinned — used by tests only.

                        // SHA-256 of the DER-encoded peer cert.
                        var x509 = cert as X509Certificate2 ?? new X509Certificate2(cert);
                        byte[] hash = SHA256.HashData(x509.RawData);
                        string actual = Convert.ToHexString(hash).ToLowerInvariant();
                        return string.Equals(actual, pinnedFingerprintHex, StringComparison.OrdinalIgnoreCase);
                    }
                }
            };

            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }
    }
}
