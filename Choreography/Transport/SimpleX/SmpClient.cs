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

namespace Choreography.Transport.SimpleX
{
    internal sealed class SmpClient : IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly byte[] _knownKeyHash;
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

        public bool IsConnected { get; private set; }
        public event Action OnDisconnected;

        // knownKeyHash es el SHA-256 del idCert del server (TOFU). Lo conoce el cliente
        // a-priori: para el creator viene del config del Stage; para el joiner viene
        // del invitation URI smp://HASH@host:port/... extraido por SmpQueue.FromInvitationUri.
        // El protocolo SMP cierra la conexion si el cliente no lo envia o no matchea
        // el del server.
        public SmpClient(string host, int port, byte[] knownKeyHash)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentNullException(nameof(host));
            if (knownKeyHash == null || knownKeyHash.Length != 32)
                throw new ArgumentException("knownKeyHash must be 32 bytes (SHA-256 of server idCert)", nameof(knownKeyHash));
            _host = host;
            _port = port;
            _knownKeyHash = knownKeyHash;
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(_host, _port, ct);

            // Fase 4d: usamos TlsAdapterStream que envuelve BC.Tls TlsClientProtocol
            // en NON-BLOCKING mode. La adaptacion permite read+write concurrentes sin
            // deadlock (Bug A) usando un Pipe interno para plaintext y un reader task
            // dedicado al TCP socket. Ver TlsAdapterStream.cs.
            //
            // Cipher: TLS 1.3 + CHACHA20-POLY1305 + ALPN smp/1 (igual que antes).
            var crypto = new BcTlsCrypto(new SecureRandom());
            var client = new SmpTlsClient(crypto);
            _tlsAdapter = new TlsAdapterStream(_tcp.GetStream());
            await _tlsAdapter.PerformHandshakeAsync(client, ct);
            _tlsStream = _tlsAdapter;

            // El keyHash se conoce a-priori (TOFU); el server lo verifica contra su
            // propio idCert. Si no matchea, server cierra la conexion ~400ms despues.
            var handshake = await SmpHandshake.PerformAsync(_tlsStream, _knownKeyHash, ct);
            _negotiatedVersion = handshake.NegotiatedVersion;
            _sessionId = handshake.SessionId;

            IsConnected = true;

            _pumpCts = new CancellationTokenSource();
            _receivePumpTask = Task.Run(() => ReceivePumpAsync(_pumpCts.Token));
        }

        // BC.Tls TlsClient para SMP: TLS 1.3 con CHACHA20-POLY1305 y ALPN smp/1.
        // Sin ALPN el server publico devuelve hello degradado (37 bytes vs 1114 con cert chain).
        // SimpleX usa certs self-signed a nivel TLS; el anclaje real es el keyHash SMP-layer
        // que se valida en SmpHandshake. Aqui simplemente trust-all en TLS.
        private sealed class SmpTlsClient : DefaultTlsClient, ISmpTlsClient
        {
            public SmpTlsClient(BcTlsCrypto crypto) : base(crypto) { }

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

            // Diagnostic hook: TLS alerts del server (close_notify, decode_error,
            // internal_error, etc). Util para debug; BC.Tls no las surface en exception
            // hasta el siguiente I/O. Solo log incoming (server-initiated); outgoing
            // alerts (que nosotros raise al hacer Close) son ruido.
            public override void NotifyAlertReceived(short alertLevel, short alertDescription)
            {
                base.NotifyAlertReceived(alertLevel, alertDescription);
                Console.WriteLine($"[SmpTls] AlertReceived: level={alertLevel} ({AlertLevel.GetText(alertLevel)}), description={alertDescription} ({AlertDescription.GetText(alertDescription)})");
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
                return ids;
            }

            if (response is SmpErr err)
                throw new InvalidOperationException($"SMP NEW failed: {err.ErrorType}");

            throw new InvalidOperationException($"Unexpected response to NEW: {response.GetType().Name}");
        }

        // Compat overload para callers existentes (SimplexTransport joiner+creator paths).
        // En v6 el sender NO puede securizar la queue por si mismo (SKEY existe desde v9).
        // El recipient debe usar la overload con senderSignPubKey, obtenido out-of-band.
        // Este overload queda como fail-fast hasta que Fase 7 cablee el envelope flow.
        public Task SecureQueueAsync(SmpQueue queue, CancellationToken ct = default)
            => throw new NotImplementedException(
                "SecureQueueAsync(queue) requires SKEY (v9+) or out-of-band senderSignPubKey via " +
                "the overload SecureQueueAsync(queue, senderSignPubKey, ct). Wired in Fase 7.");

        // SecureQueue (rol Recipient): registra el sender's signing pub key en la queue
        // identificada por rcvId. El recipient debe haber obtenido senderSignPubKey
        // out-of-band (e.g. del envelope de invitation reverso).
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

        // --- Internal ---

        private async Task<SmpResponse> SendAndWaitAsync(byte[] corrId, byte[] payload, CancellationToken ct)
        {
            string corrKey = SmpCrypto.ToBase64Url(corrId);
            var tcs = new TaskCompletionSource<SmpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[corrKey] = tcs;

            // Timeout cubre TODA la operacion (write + wait response). Antes solo cubria
            // el wait, asi que un write que se atoraba colgaba infinito. Como BC.Tls no
            // respeta cooperativamente el cancellation token cuando el TLS quedo en estado
            // "closed esperando ACK" (caso del server que cerro silenciosamente post-handshake),
            // forzamos cierre del protocolo TLS al expirar el timeout — eso despierta el write.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            using var reg = timeoutCts.Token.Register(() =>
            {
                tcs.TrySetCanceled();
                try { _tlsAdapter?.Dispose(); } catch { /* forzamos despertar de I/O bloqueada */ }
            });

            try
            {
                // Fase 4d: write goes through TlsAdapterStream (non-blocking BC.Tls
                // bajo el cap), receive es pump-routed via _pending por corrId.
                // El TlsAdapterStream coordina concurrent read+write sin deadlock.
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
                    SmpResponse response = SmpResponseParser.Parse(payload);

                    // Route to pending request by corrId, or to subscription by queueId
                    if (response.CorrelationId != null && response.CorrelationId.Length > 0)
                    {
                        string corrKey = SmpCrypto.ToBase64Url(response.CorrelationId);
                        if (_pending.TryGetValue(corrKey, out var tcs))
                        {
                            tcs.TrySetResult(response);
                            continue;
                        }
                    }

                    // MSG with no pending corrId → subscription delivery
                    if (response is SmpMsg msg && response.QueueId != null)
                    {
                        string queueKey = SmpCrypto.ToBase64Url(response.QueueId);
                        if (_queueSubscriptions.TryGetValue(queueKey, out var channel))
                        {
                            await channel.Writer.WriteAsync(msg, ct);
                        }
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
                Console.WriteLine($"[SmpClient] ReceivePump error: {ex.Message}");
                HandleDisconnect();
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
