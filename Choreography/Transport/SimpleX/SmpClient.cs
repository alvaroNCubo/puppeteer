using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Puppeteer;
using Puppeteer.EventSourcing;

namespace Choreography.Transport.SimpleX
{
    internal sealed class SmpClient : IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly byte[] _knownKeyHash;
        private readonly IPuppeteerLogger _logger;
        private TcpClient _tcp;
        private TlsAdapterStream _tlsAdapter;
        private Stream _tlsStream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<SmpResponse>> _pending = new();
        private readonly ConcurrentDictionary<string, Channel<SmpMsg>> _queueSubscriptions = new();
        private Task _receivePumpTask;
        private CancellationTokenSource _pumpCts;
        private int _negotiatedVersion;
        private byte[] _sessionId;
        private readonly bool _offline;

        public bool IsConnected { get; private set; }
        public event Action OnDisconnected;
        internal IPuppeteerLogger Logger => _logger;

        // knownKeyHash is the SHA-256 of the server idCert (TOFU). The client knows it
        // a-priori: for the creator it comes from the Stage config; for the joiner it comes
        // from the invitation URI smp://HASH@host:port/... extracted by SmpQueue.FromInvitationUri.
        // The SMP protocol closes the connection if the client does not send it or it does not match
        // the server's.
        public SmpClient(string host, int port, byte[] knownKeyHash, IPuppeteerLogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentNullException(nameof(host));
            if (knownKeyHash == null || knownKeyHash.Length != 32)
                throw new ArgumentException("knownKeyHash must be 32 bytes (SHA-256 of server idCert)", nameof(knownKeyHash));
            _host = host;
            _port = port;
            _knownKeyHash = knownKeyHash;
            _logger = logger ?? new ConsoleLogger();
        }

        // Test-only: "offline" client (no TCP/TLS) that reports IsConnected=true and resolves
        // SubscribeAsync without touching the network. Allows exercising the ReceivePump lifecycle of the
        // SimplexChannel (regression 21) deterministically and on Windows, without an SMP server.
        internal SmpClient(IPuppeteerLogger logger, bool offlineForTesting)
        {
            if (!offlineForTesting)
                throw new ArgumentException("Use the (host, port, keyHash) constructor for real connections", nameof(offlineForTesting));
            _host = "offline";
            _port = 0;
            _knownKeyHash = new byte[32];
            _logger = logger ?? new ConsoleLogger();
            _offline = true;
            IsConnected = true;
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(_host, _port, ct);

            // Phase 4d: we use TlsAdapterStream that wraps BC.Tls TlsClientProtocol
            // in NON-BLOCKING mode. The adaptation allows concurrent read+write without
            // deadlock (Bug A) using an internal Pipe for plaintext and a reader task
            // dedicated to the TCP socket. See TlsAdapterStream.cs.
            //
            // Cipher: TLS 1.3 + CHACHA20-POLY1305 + ALPN smp/1 (same as before).
            var crypto = new BcTlsCrypto(new SecureRandom());
            var client = new SmpTlsClient(crypto, _logger);
            _tlsAdapter = new TlsAdapterStream(_tcp.GetStream());
            await _tlsAdapter.PerformHandshakeAsync(client, ct);
            _tlsStream = _tlsAdapter;

            // The keyHash is known a-priori (TOFU); the server verifies it against its
            // own idCert. If it does not match, the server closes the connection ~400ms later.
            var handshake = await SmpHandshake.PerformAsync(_tlsStream, _knownKeyHash, _logger, ct);
            _negotiatedVersion = handshake.NegotiatedVersion;
            _sessionId = handshake.SessionId;

            IsConnected = true;

            _pumpCts = new CancellationTokenSource();
            _receivePumpTask = Task.Run(() => ReceivePumpAsync(_pumpCts.Token));
        }

        // BC.Tls TlsClient for SMP: TLS 1.3 with CHACHA20-POLY1305 and ALPN smp/1.
        // Without ALPN the public server returns a degraded hello (37 bytes vs 1114 with cert chain).
        // SimpleX uses self-signed certs at the TLS level; the real anchor is the SMP-layer keyHash
        // validated in SmpHandshake. Here we simply trust-all at TLS.
        private sealed class SmpTlsClient : DefaultTlsClient, ISmpTlsClient
        {
            private readonly IPuppeteerLogger _logger;
            public SmpTlsClient(BcTlsCrypto crypto, IPuppeteerLogger logger) : base(crypto)
            {
                _logger = logger;
            }

            public bool HandshakeComplete { get; private set; }

            public override void NotifyHandshakeComplete()
            {
                base.NotifyHandshakeComplete();
                HandshakeComplete = true;
            }

            protected override int[] GetSupportedCipherSuites()
                => new[] { CipherSuite.TLS_CHACHA20_POLY1305_SHA256 };

            protected override ProtocolVersion[] GetSupportedVersions()
                => new[] { ProtocolVersion.TLSv13 };

            public override IDictionary<int, byte[]> GetClientExtensions()
            {
                var ext = base.GetClientExtensions() ?? new Dictionary<int, byte[]>();
                TlsExtensionsUtilities.AddAlpnExtensionClient(ext, new List<ProtocolName>
                {
                    ProtocolName.AsUtf8Encoding("smp/1")
                });
                return ext;
            }

            public override TlsAuthentication GetAuthentication() => new TrustAllAuthentication();

            // Diagnostic hook: TLS alerts from the server (close_notify, decode_error,
            // internal_error, etc). Useful for debug; BC.Tls does not surface them in an exception
            // until the next I/O. Only log incoming (server-initiated); outgoing
            // alerts (that we raise on Close) are noise.
            public override void NotifyAlertReceived(short alertLevel, short alertDescription)
            {
                base.NotifyAlertReceived(alertLevel, alertDescription);
                _logger.Debug($"[SmpTls] AlertReceived: level={alertLevel} ({AlertLevel.GetText(alertLevel)}), description={alertDescription} ({AlertDescription.GetText(alertDescription)})");
            }
        }

        private sealed class TrustAllAuthentication : TlsAuthentication
        {
            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) => null;
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate) { /* trust-all */ }
        }

        // PING diagnostic — no auth, no queueId. Server responds PONG.
        public async Task<SmpResponse> SendPingAsync(CancellationToken ct = default)
        {
            byte[] corrId = SmpCrypto.RandomBytes(SmpCommandBuilder.CorrIdSize);
            byte[] payload = SmpCommandBuilder.BuildPing(_sessionId, corrId);
            return await SendAndWaitAsync(corrId, payload, ct);
        }

        // --- Queue operations ---

        public async Task<SmpIds> CreateQueueAsync(SmpQueue queue, CancellationToken ct = default)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));

            // Generate recipient keys
            var (signPub, signSec) = SmpCrypto.GenerateSigningKeyPair();
            var (dhPub, dhSec) = SmpCrypto.GenerateDhKeyPair();

            queue.RecipientSignPublicKey = signPub;
            queue.RecipientSignSecretKey = signSec;
            queue.RecipientDhPublicKey = dhPub;
            queue.RecipientDhSecretKey = dhSec;

            byte[] corrId = SmpCrypto.RandomBytes(SmpCommandBuilder.CorrIdSize);
            byte[] payload = SmpCommandBuilder.BuildNew(_sessionId, corrId, signPub, dhPub, signSec);

            var response = await SendAndWaitAsync(corrId, payload, ct);

            if (response is SmpIds ids)
            {
                queue.RecipientId = ids.RecipientId;
                queue.SenderId = ids.SenderId;
                queue.ServerDhPublicKey = ids.ServerDhPublicKey;
                queue.State = SmpQueueState.Active;
                queue.Role = SmpQueueRole.Recipient;

                // NEW v6 uses subMode='S' (SMSubscribe): the server starts delivering MSGs
                // immediately without waiting for an explicit SUB. We register the local Channel
                // right now to avoid the race window where an early MSG is dropped
                // because the dictionary entry did not yet exist.
                string queueKey = SmpCrypto.ToBase64Url(ids.RecipientId);
                _queueSubscriptions.GetOrAdd(queueKey, _ => Channel.CreateUnbounded<SmpMsg>());

                return ids;
            }

            if (response is SmpErr err)
                throw new InvalidOperationException($"SMP NEW failed: {err.ErrorType}");

            throw new InvalidOperationException($"Unexpected response to NEW: {response.GetType().Name}");
        }

        // SecureQueue (Recipient role): registers the sender's signing pub key in the queue
        // identified by rcvId. The recipient must have obtained senderSignPubKey
        // out-of-band (e.g. from the reverse invitation envelope).
        public async Task SecureQueueAsync(SmpQueue queue, byte[] senderSignPubKey, CancellationToken ct = default)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (queue.RecipientId == null) throw new InvalidOperationException("Queue has no RecipientId");
            if (queue.RecipientSignSecretKey == null) throw new InvalidOperationException("Queue has no RecipientSignSecretKey");

            byte[] corrId = SmpCrypto.RandomBytes(SmpCommandBuilder.CorrIdSize);
            byte[] payload = SmpCommandBuilder.BuildKey(_sessionId, corrId, queue.RecipientId,
                senderSignPubKey, queue.RecipientSignSecretKey);

            var response = await SendAndWaitAsync(corrId, payload, ct);

            if (response is SmpOk)
            {
                queue.State = SmpQueueState.Secured;
                return;
            }

            if (response is SmpErr err)
                throw new InvalidOperationException($"SMP KEY failed: {err.ErrorType}");
            throw new InvalidOperationException($"Unexpected response to KEY: {response.GetType().Name}");
        }

        public async Task SubscribeAsync(SmpQueue queue, CancellationToken ct = default)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (queue.RecipientId == null) throw new InvalidOperationException("Queue has no RecipientId");

            // Register subscription channel
            string queueKey = SmpCrypto.ToBase64Url(queue.RecipientId);
            _queueSubscriptions.GetOrAdd(queueKey, _ => Channel.CreateUnbounded<SmpMsg>());

            if (_offline) return; // test-only: subscription registered, without a network SUB

            byte[] corrId = SmpCrypto.RandomBytes(SmpCommandBuilder.CorrIdSize);
            byte[] payload = SmpCommandBuilder.BuildSub(_sessionId, corrId, queue.RecipientId,
                queue.RecipientSignSecretKey);

            var response = await SendAndWaitAsync(corrId, payload, ct);

            if (response is SmpOk)
                return;

            if (response is SmpErr err)
                throw new InvalidOperationException($"SMP SUB failed: {err.ErrorType}");
        }

        public async Task SendMessageAsync(SmpQueue queue, byte[] plaintext, CancellationToken ct = default)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (queue.SenderId == null) throw new InvalidOperationException("Queue has no SenderId");
            if (queue.SenderSignSecretKey == null) throw new InvalidOperationException("Queue has no SenderSignSecretKey");
            if (queue.SenderDhSecretKey == null) throw new InvalidOperationException("Queue has no SenderDhSecretKey");
            if (queue.RecipientDhPublicKey == null) throw new InvalidOperationException("Queue has no RecipientDhPublicKey");

            // Encrypt with NaCl crypto_box (recipient's DH pub from invitation URI)
            byte[] nonce = SmpCrypto.GenerateNonce();
            byte[] encrypted = SmpCrypto.Encrypt(plaintext, nonce,
                queue.SenderDhSecretKey, queue.RecipientDhPublicKey);

            byte[] message = new byte[SmpCrypto.NonceSize + encrypted.Length];
            Buffer.BlockCopy(nonce, 0, message, 0, SmpCrypto.NonceSize);
            Buffer.BlockCopy(encrypted, 0, message, SmpCrypto.NonceSize, encrypted.Length);

            byte[] corrId = SmpCrypto.RandomBytes(SmpCommandBuilder.CorrIdSize);
            byte[] payload = SmpCommandBuilder.BuildSend(_sessionId, corrId, queue.SenderId,
                message, queue.SenderSignSecretKey);

            var response = await SendAndWaitAsync(corrId, payload, ct);

            if (response is SmpOk)
                return;

            if (response is SmpErr err)
                throw new InvalidOperationException($"SMP SEND failed: {err.ErrorType}");
        }

        // SEND unsigned: used in the bidirectional handshake for the bootstrap envelopes
        // before the queue has KEY. The sender is anonymous (empty signature) and the payload
        // travels plaintext without crypto_box.
        //
        // Reason for plaintext: the crypto_box chicken-and-egg during the handshake. The
        // receiver needs the sender's senderDhPub to decrypt, but the senderDhPub
        // is PRECISELY what the handshake transports. The envelopes only carry public
        // pubkeys + public queue URIs, so sending them plaintext does not compromise
        // secrecy. Once the handshake completes, subsequent SENDs use
        // SendMessageAsync with full crypto_box.
        public async Task SendMessageUnsignedAsync(SmpQueue queue, byte[] payload, CancellationToken ct = default)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (queue.SenderId == null) throw new InvalidOperationException("Queue has no SenderId");

            byte[] corrId = SmpCrypto.RandomBytes(SmpCommandBuilder.CorrIdSize);
            byte[] cmd = SmpCommandBuilder.BuildSendUnsigned(_sessionId, corrId, queue.SenderId, payload);

            var response = await SendAndWaitAsync(corrId, cmd, ct);

            if (response is SmpOk)
                return;

            if (response is SmpErr err)
                throw new InvalidOperationException($"SMP SEND unsigned failed: {err.ErrorType}");
            throw new InvalidOperationException($"Unexpected response to SEND unsigned: {response.GetType().Name}");
        }

        public async Task AcknowledgeAsync(SmpQueue queue, byte[] msgId, CancellationToken ct = default)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (msgId == null) throw new ArgumentNullException(nameof(msgId));

            byte[] corrId = SmpCrypto.RandomBytes(SmpCommandBuilder.CorrIdSize);
            byte[] payload = SmpCommandBuilder.BuildAck(_sessionId, corrId, queue.RecipientId,
                msgId, queue.RecipientSignSecretKey);

            var response = await SendAndWaitAsync(corrId, payload, ct);
            // ACK response is OK or ERR, both acceptable
        }

        public ChannelReader<SmpMsg> GetSubscription(byte[] recipientId)
        {
            string key = SmpCrypto.ToBase64Url(recipientId);
            if (_queueSubscriptions.TryGetValue(key, out var channel))
                return channel.Reader;
            throw new InvalidOperationException($"No subscription for queue {key}");
        }

        // Test-only: completes the subscription as a real transport disconnect would.
        // Used by regression 21 to prove that the ReceivePump keeps REALLY reading the
        // subscription after cancelling the connect token (it exits cleanly on completion).
        internal void CompleteSubscriptionForTesting(byte[] recipientId)
        {
            string key = SmpCrypto.ToBase64Url(recipientId);
            if (_queueSubscriptions.TryGetValue(key, out var channel))
                channel.Writer.TryComplete();
        }

        // --- Internal ---

        private async Task<SmpResponse> SendAndWaitAsync(byte[] corrId, byte[] payload, CancellationToken ct)
        {
            string corrKey = SmpCrypto.ToBase64Url(corrId);
            var tcs = new TaskCompletionSource<SmpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[corrKey] = tcs;

            // Timeout covers the WHOLE operation (write + wait response). Before it only covered
            // the wait, so a write that got stuck hung forever. Since BC.Tls does not
            // cooperatively respect the cancellation token when the TLS is left in a
            // "closed waiting for ACK" state (the case of a server that closed silently post-handshake),
            // we force-close the TLS protocol when the timeout expires — that wakes up the write.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            using var reg = timeoutCts.Token.Register(() =>
            {
                tcs.TrySetCanceled();
                try { _tlsAdapter?.Dispose(); } catch { /* force wake-up of blocked I/O */ }
            });

            try
            {
                // Phase 4d: write goes through TlsAdapterStream (non-blocking BC.Tls
                // under the cap), receive is pump-routed via _pending by corrId.
                // The TlsAdapterStream coordinates concurrent read+write without deadlock.
                await _writeLock.WaitAsync(timeoutCts.Token);
                try
                {
                    await SmpBlock.WriteBlockAsync(_tlsStream, payload, timeoutCts.Token);
                }
                finally
                {
                    _writeLock.Release();
                }
                return await tcs.Task;
            }
            finally
            {
                _pending.TryRemove(corrKey, out _);
            }
        }

        private async Task ReceivePumpAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && IsConnected)
                {
                    byte[] payload = await SmpBlock.ReadBlockAsync(_tlsStream, ct);
                    // The server may batch several transmissions in the same block
                    // (e.g. OK from a previous ACK + pending server-push MSG). We must
                    // route ALL of them, not just the first. See SmpResponseParser.ParseAll
                    // for the detail of the historical bug (#2).
                    foreach (var response in SmpResponseParser.ParseAll(payload))
                    {
                        await RouteResponseAsync(response, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException)
            {
                HandleDisconnect();
            }
            catch (Exception ex)
            {
                _logger.Error("[SmpClient] ReceivePump error", ex);
                HandleDisconnect();
            }
        }

        private async Task RouteResponseAsync(SmpResponse response, CancellationToken ct)
        {
            // Bug #2-CATCHUP fix (2026-05-22): the SMP server "piggyback"s
            // MSG deliveries on the response of the command that triggers them
            // (SUB or ACK). That is, instead of returning SmpOk to the ACK and sending
            // the next MSG separately, it returns ONE single SmpMsg with the corrId
            // of the ACK that delivers the implicit OK + the next MSG. See
            // simplexmq Server.hs subscribeQueue / acknowledgeMessage:
            //   pure $ case mNextMsg of
            //     Just msg -> Msg msg   -- piggyback on the command response
            //     Nothing  -> Ok
            //
            // Previous bug (master): RouteResponseAsync routed SmpMsg to `_pending`
            // by corrId if it matched. The ACK's TCS received the SmpMsg, but
            // AcknowledgeAsync ignores SmpMsg (it expects SmpOk/SmpErr). The queue
            // subscription never saw the MSG → the entry was lost and the server
            // stopped delivering (gated by ACK). Symptom: in catch-up batch,
            // Cast applied 1-2 entries out of N expected, then total silence.
            //
            // Fix: if the response is SmpMsg, ALWAYS route it to the subscription
            // by queueId. If it additionally has a pending corrId, we also wake up
            // the waiter (AcknowledgeAsync) so the command completes.
            if (response is SmpMsg msg && response.QueueId != null)
            {
                string queueKey = SmpCrypto.ToBase64Url(response.QueueId);
                if (_queueSubscriptions.TryGetValue(queueKey, out var channel))
                    await channel.Writer.WriteAsync(msg, ct);

                // Piggyback: if the server used the corrId of a pending command,
                // we also wake up the waiter so the command completes.
                if (response.CorrelationId != null && response.CorrelationId.Length > 0)
                {
                    string corrKey = SmpCrypto.ToBase64Url(response.CorrelationId);
                    if (_pending.TryGetValue(corrKey, out var tcs))
                        tcs.TrySetResult(response);
                }
                return;
            }

            // No-MSG responses (OK/IDS/ERR/END/PONG): they only go by corrId.
            if (response.CorrelationId != null && response.CorrelationId.Length > 0)
            {
                string corrKey = SmpCrypto.ToBase64Url(response.CorrelationId);
                if (_pending.TryGetValue(corrKey, out var tcs))
                {
                    tcs.TrySetResult(response);
                    return;
                }
            }
        }

        private void HandleDisconnect()
        {
            if (!IsConnected) return;
            IsConnected = false;
            OnDisconnected?.Invoke();

            foreach (var sub in _queueSubscriptions.Values)
                sub.Writer.TryComplete();
        }

        public async ValueTask DisposeAsync()
        {
            IsConnected = false;
            _pumpCts?.Cancel();

            if (_receivePumpTask != null)
            {
                try { await _receivePumpTask; } catch { }
            }

            try { _tlsAdapter?.Dispose(); } catch { }
            _tcp?.Dispose();
            _pumpCts?.Dispose();
            _writeLock.Dispose();
        }
    }
}
