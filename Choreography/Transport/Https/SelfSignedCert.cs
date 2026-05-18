using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Choreography.Transport.Https
{
    // Self-signed X509 certificate generation for the Kestrel TLS listener.
    //
    // Paper 7 Phase 2 demonstrates HTTPS between Choreography Stages running
    // in separate Docker containers. The certificate model is TOFU
    // (Trust On First Use): the inviter Stage prints its certificate
    // fingerprint in the share-link payload; the joiner pins that fingerprint
    // when it opens its TLS client connection. No CA is required.
    //
    // The generator below produces a fresh RSA-2048 self-signed certificate
    // valid for one year, with the Stage's listen URL host registered as a
    // SubjectAlternativeName so .NET's HttpClient default hostname matching
    // can still apply on top of the fingerprint pin.
    public static class SelfSignedCert
    {
        public static X509Certificate2 Generate(string subjectCommonName, params string[] dnsNames)
        {
            if (string.IsNullOrWhiteSpace(subjectCommonName))
                throw new ArgumentNullException(nameof(subjectCommonName));

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={subjectCommonName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // Subject Alternative Names — required by modern TLS clients
            // (Chrome, .NET HttpClient from net5+) when the common name
            // alone is not enough.
            if (dnsNames != null && dnsNames.Length > 0)
            {
                var sanBuilder = new SubjectAlternativeNameBuilder();
                foreach (var dns in dnsNames)
                {
                    if (string.IsNullOrWhiteSpace(dns)) continue;
                    sanBuilder.AddDnsName(dns);
                }
                request.CertificateExtensions.Add(sanBuilder.Build());
            }

            // Mark the certificate as a server certificate (Extended Key Usage).
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // serverAuth
                critical: false));

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
            var notAfter  = DateTimeOffset.UtcNow.AddYears(1);

            // CreateSelfSigned returns a cert that includes the private key
            // in-memory; export it as PFX and re-import so the resulting
            // object is suitable to pass to Kestrel.UseHttps.
            using var cert = request.CreateSelfSigned(notBefore, notAfter);
            byte[] pfx = cert.Export(X509ContentType.Pfx);
            return X509CertificateLoader.LoadPkcs12(pfx, password: null);
        }

        // SHA-256 fingerprint of the DER-encoded certificate, lowercase hex
        // without separators. This is the value that goes into the share-link
        // for TOFU pinning.
        public static string Fingerprint(X509Certificate2 cert)
        {
            if (cert == null) throw new ArgumentNullException(nameof(cert));
            byte[] der  = cert.RawData;
            byte[] hash = SHA256.HashData(der);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
