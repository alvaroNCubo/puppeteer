using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;

namespace Choreography.Transport.Https
{
    // Paper 7 Phase 2 — Kestrel-backed real TLS transport.
    //
    // The peer-trust model is Trust On First Use (TOFU): the inviter Stage
    // publishes its certificate fingerprint inside the UsherShareLink payload
    // (see Choreography/Usher/IUsherShareLinkEncoder.cs). The joiner pins
    // that fingerprint via TrustPeerFingerprint before opening its
    // HttpsChannel toward the inviter; every TLS handshake checks the remote
    // certificate against the pinned fingerprint.
    //
    // Implementations of CreateInvitationAsync / AcceptInvitationAsync /
    // WaitForConnectionAsync are otherwise unchanged from the previous
    // HttpListener-based version — only the underlying listener was swapped
    // out for Kestrel and the channels are constructed with a fingerprint
    // pin.
    // Paper 7 Phase 2: promoted internal→public alongside IStageTransport
    // and Usher. The CLI and per-Docker host construct an HttpsTransport
    // directly (outside a Stage) when issuing or accepting onboarding
    // invitations. Stage continues to wrap construction through
    // ConfigureTransport for in-runtime callers.
    public sealed class HttpsTransport : IStageTransport, IAsyncDisposable
    {
        private readonly PerformerId localId;
        private readonly string listenUrl;
        private readonly string advertiseUrl;
        private readonly HttpsTransportListener listener;
        private readonly X509Certificate2 serverCert;
        private readonly ConcurrentDictionary<string, string> peerFingerprintsByListenUrl = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly System.Threading.SemaphoreSlim startupLock = new(1, 1);
        private bool started;

        // listenUrl = the URL Kestrel binds to (e.g. https://0.0.0.0:5443 inside
        //             a Docker container).
        // advertiseUrl = the URL peers see in ConnectionInvitation.Address
        //                when they decide where to connect (e.g.
        //                https://ordering-a:5443 in a Docker compose network,
        //                or https://laptop-ip:5443 across machines).
        // If advertiseUrl is null, listenUrl is used for both — the common
        // case for tests where everything runs on loopback.
        public HttpsTransport(PerformerId localId, string listenUrl,
            X509Certificate2 serverCert, string advertiseUrl = null)
        {
            this.localId = localId;
            this.listenUrl = listenUrl ?? throw new ArgumentNullException(nameof(listenUrl));
            this.serverCert = serverCert ?? throw new ArgumentNullException(nameof(serverCert));
            this.advertiseUrl = string.IsNullOrWhiteSpace(advertiseUrl) ? listenUrl : advertiseUrl;
            this.listener = new HttpsTransportListener(listenUrl, serverCert);
        }

        // SHA-256 fingerprint (lowercase hex) of the local server certificate.
        // Goes into the UsherShareLink so peers can pin it.
        public string LocalCertFingerprint => SelfSignedCert.Fingerprint(serverCert);

        // Pre-register the fingerprint a peer's TLS cert must match. The
        // peer's listenUrl is the key; subsequent AcceptInvitation /
        // WaitForConnection calls that produce an HttpsChannel for that URL
        // pin the fingerprint on every outbound HTTPS request the channel
        // makes.
        public void TrustPeerFingerprint(string peerListenUrl, string fingerprintHex)
        {
            if (string.IsNullOrWhiteSpace(peerListenUrl)) throw new ArgumentNullException(nameof(peerListenUrl));
            if (string.IsNullOrWhiteSpace(fingerprintHex)) throw new ArgumentNullException(nameof(fingerprintHex));
            peerFingerprintsByListenUrl[peerListenUrl.TrimEnd('/')] = fingerprintHex.ToLowerInvariant();
        }

        // The Usher issues invitations in a tight loop; each invitation spawns
        // a background Task that calls WaitForConnectionAsync → here. Without
        // serialisation two parallel tasks would race past `if (started)`,
        // both call listener.StartAsync, and Kestrel throws
        // "Server has already started" on the second call. Demo run #2
        // exposed this exact failure mode.
        internal async Task EnsureStartedAsync(CancellationToken ct = default)
        {
            if (started) return;
            await startupLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (started) return;          // double-check inside the lock
                await listener.StartAsync(ct).ConfigureAwait(false);
                started = true;
            }
            finally
            {
                startupLock.Release();
            }
        }

        public Task<ConnectionInvitation> CreateInvitationAsync(ChannelPurpose purpose)
        {
            string address = $"{advertiseUrl.TrimEnd('/')}|{localId}|{purpose}|{Guid.NewGuid():N}";
            var invitation = new ConnectionInvitation(localId, purpose, address);
            return Task.FromResult(invitation);
        }

        public async Task<IStageChannel> AcceptInvitationAsync(ConnectionInvitation invitation)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            await EnsureStartedAsync();

            string[] parts = invitation.Address.Split('|');
            if (parts.Length < 4) throw new ArgumentException("Invalid invitation address format");

            string remoteListenUrl = parts[0];
            var remoteId = invitation.InviterId;
            var purpose = invitation.Purpose;

            string pinnedFingerprint = ResolveFingerprintForUrl(remoteListenUrl);
            var channel = new HttpsChannel(localId, remoteId, purpose, remoteListenUrl, pinnedFingerprint);
            listener.RegisterChannel(remoteId, channel);

            string connectPayload = $"{Convert.ToBase64String(localId.ToBytes())}\n{(byte)purpose}\n{invitation.Address}\n{advertiseUrl.TrimEnd('/')}";

            string connectUrl = $"{remoteListenUrl.TrimEnd('/')}/connect";

            using var connectClient = HttpsClientFactory.BuildClient(pinnedFingerprint);
            using var content = new StringContent(connectPayload, Encoding.UTF8, "text/plain");
            var response = await connectClient.PostAsync(connectUrl, content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            return channel;
        }

        public async Task<IStageChannel> WaitForConnectionAsync(ConnectionInvitation invitation, CancellationToken ct)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            await EnsureStartedAsync(ct);

            var tcs = listener.CreatePendingConnection(invitation.Address);

            using var registration = ct.Register(() => tcs.TrySetCanceled());
            var (peerId, purpose, remoteListenUrl) = await tcs.Task;

            string pinnedFingerprint = ResolveFingerprintForUrl(remoteListenUrl);
            var channel = new HttpsChannel(localId, peerId, purpose, remoteListenUrl, pinnedFingerprint);
            listener.RegisterChannel(peerId, channel);

            return channel;
        }

        private string ResolveFingerprintForUrl(string remoteListenUrl)
        {
            string normalized = (remoteListenUrl ?? string.Empty).TrimEnd('/');
            if (peerFingerprintsByListenUrl.TryGetValue(normalized, out var fp))
                return fp;
            // No fingerprint pinned — return null so HttpsChannel falls back
            // to "accept any cert" for this URL. Used in tests where the cert
            // fingerprint is irrelevant; production paths always pin.
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await listener.DisposeAsync();
            startupLock.Dispose();
        }
    }
}
