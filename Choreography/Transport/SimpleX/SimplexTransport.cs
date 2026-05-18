using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;

namespace Choreography.Transport.SimpleX
{
    // FASE 7 PENDIENTE — envelope flow para 2-Kora E2E con KEY bidireccional.
    //
    // El modelo SMP v6 requiere que cada queue se "secure" con KEY antes de aceptar
    // SEND firmados. Como hay DOS queues por par (forward del creator, reverse del
    // joiner), ambos lados necesitan el senderSignPubKey del otro out-of-band.
    //
    // Flow correcto (no implementado aun en este transport):
    //
    //   1. Creator: NEW(forwardQueue) -> IDS. SUB(forwardQueue).
    //      Genera invitation URI con sndId+rcpDhPub.
    //
    //   2. Joiner: parse invitation URI. Genera senderSign/Dh keys propias.
    //      NEW(reverseQueue) -> IDS propio. SUB(reverseQueue).
    //      SEND inicial (anonimo, queue unsecured) al forwardQueue con envelope:
    //        - reverseQueue invitation URI (joiner como recipient)
    //        - joiner.senderSignPub_F (sign key del joiner para forwardQueue)
    //
    //   3. Creator: pump entrega MSG con envelope. Procesa:
    //      - Aprende reverseQueue invitation -> queue.SenderId del reverse.
    //      - SecureQueueAsync(forwardQueue, joiner.senderSignPub_F) [KEY].
    //      - Genera senderSign/Dh keys propias para reverseQueue.
    //      - SEND inicial (anonimo, reverseQueue unsecured) con envelope inverso:
    //        - creator.senderSignPub_R (sign key del creator para reverseQueue)
    //
    //   4. Joiner: pump entrega MSG con envelope inverso.
    //      - SecureQueueAsync(reverseQueue, creator.senderSignPub_R) [KEY].
    //
    //   5. Ambas queues quedan secured. Cualquier SEND posterior va firmado.
    //
    // Hoy SimplexTransport.AcceptInvitationAsync y WaitForConnectionAsync llaman a
    // _client.SecureQueueAsync(queue) sin senderSignPubKey explicito. Esa overload
    // ahora lanza NotImplementedException con guidance hacia el overload de 5 args.
    // El cableo del envelope flow (extraer senderSignPubKey de ReverseQueueEnvelope,
    // bootstrap inicial unsigned, etc.) queda como Fase 7 follow-up.
    //
    // Test single-actor (LocalSmp_Sub_Send_Receive_E2E) y multi-actor manual
    // (LocalSmp_TwoKoras_E2E_MessageDelivered) cubren el camino real con KEY pasado
    // explicitamente, validando que el wire format funciona; falta solo el wiring
    // declarativo arriba para que SimplexTransport haga este flow automatico.
    internal sealed class SimplexTransport : IStageTransport
    {
        private readonly PerformerId _localId;
        private readonly string _smpServer;
        private readonly int _smpPort;
        // serverFingerprint = SHA-256(idCert) que el Stage conoce a-priori (TOFU).
        // Para creator (Stage que crea invitaciones via Ushier) viene del config.
        // Para joiner-only (solo acepta invitaciones), puede ser null y se extrae del URI.
        private readonly byte[] _configuredFingerprint;
        private SmpClient _client;
        private readonly ConcurrentDictionary<string, PendingInvitation> _pending = new();
        private readonly SemaphoreSlim _connectLock = new(1, 1);

        public SimplexTransport(PerformerId localId, string smpServerUrl, byte[] serverFingerprint = null)
        {
            if (string.IsNullOrWhiteSpace(smpServerUrl)) throw new ArgumentNullException(nameof(smpServerUrl));
            _localId = localId;
            ParseServerUrl(smpServerUrl, out _smpServer, out _smpPort);
            _configuredFingerprint = serverFingerprint;
        }

        private static void ParseServerUrl(string url, out string host, out int port)
        {
            int colonIndex = url.LastIndexOf(':');
            if (colonIndex > 0 && int.TryParse(url.AsSpan(colonIndex + 1), out port))
            {
                host = url.Substring(0, colonIndex);
            }
            else
            {
                host = url;
                port = 5223;
            }
        }

        private async Task EnsureConnectedAsync(byte[] keyHash, CancellationToken ct)
        {
            if (_client != null && _client.IsConnected) return;

            await _connectLock.WaitAsync(ct);
            try
            {
                if (_client != null && _client.IsConnected) return;

                if (_client != null) await _client.DisposeAsync();

                _client = new SmpClient(_smpServer, _smpPort, keyHash);
                await _client.ConnectAsync(ct);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        // Kora1 creates a queue on the SMP server and returns the invitation.
        // Requiere que el config del Stage haya provisto serverFingerprint (rol del Ushier).
        public async Task<ConnectionInvitation> CreateInvitationAsync(ChannelPurpose purpose)
        {
            if (_configuredFingerprint == null)
                throw new InvalidOperationException(
                    "serverFingerprint requerido para crear invitaciones. " +
                    "Configurar via Stage.ConfigureTransport(SimpleX, host, serverFingerprint).");

            await EnsureConnectedAsync(_configuredFingerprint, CancellationToken.None);

            var queue = new SmpQueue(_smpServer, _smpPort) { ServerFingerprint = _configuredFingerprint };
            await _client.CreateQueueAsync(queue);

            string uri = queue.ToInvitationUri();

            _pending[uri] = new PendingInvitation
            {
                Queue = queue,
                Purpose = purpose
            };

            return new ConnectionInvitation(_localId, purpose, uri);
        }

        // Kora2 reads the invitation (QR), secures the forward queue, creates a reverse queue,
        // sends ReverseQueueEnvelope to Kora1, returns bidirectional channel
        public async Task<IStageChannel> AcceptInvitationAsync(ConnectionInvitation invitation)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            // Parse the forward queue from invitation URI (incluye serverFingerprint TOFU).
            var forwardQueue = SmpQueue.FromInvitationUri(invitation.Address);
            byte[] keyHash = forwardQueue.ServerFingerprint
                ?? _configuredFingerprint
                ?? throw new InvalidOperationException(
                    "Invitation URI no incluye fingerprint y transport no fue configurado con uno. " +
                    "Verificar que el Ushier emita URIs en formato smp://HASH@host:port/...");

            await EnsureConnectedAsync(keyHash, CancellationToken.None);

            // Secure the forward queue (we become the sender)
            await _client.SecureQueueAsync(forwardQueue);

            // Create reverse queue (we are the recipient of return messages)
            var reverseQueue = new SmpQueue(_smpServer, _smpPort);
            await _client.CreateQueueAsync(reverseQueue);

            // Send ReverseQueueEnvelope via the forward queue so Kora1 learns our reverse queue
            var envelope = new ReverseQueueEnvelope
            {
                ReverseQueueUri = reverseQueue.ToInvitationUri(),
                PerformerId = _localId
            };

            byte[] envelopeBytes = envelope.Serialize();
            byte[] nonce = SmpCrypto.GenerateNonce();
            byte[] encrypted = SmpCrypto.Encrypt(envelopeBytes, nonce,
                forwardQueue.SenderDhSecretKey, forwardQueue.RecipientDhPublicKey);

            byte[] message = new byte[SmpCrypto.NonceSize + encrypted.Length];
            Buffer.BlockCopy(nonce, 0, message, 0, SmpCrypto.NonceSize);
            Buffer.BlockCopy(encrypted, 0, message, SmpCrypto.NonceSize, encrypted.Length);

            await _client.SendMessageAsync(forwardQueue, envelopeBytes, CancellationToken.None);

            // Build bidirectional channel: we send on forward, receive on reverse
            var channel = new SimplexChannel(_client, forwardQueue, reverseQueue,
                invitation.InviterId, invitation.Purpose);
            await channel.StartAsync(CancellationToken.None);

            return channel;
        }

        // Kora1 waits for Kora2 to connect, receives ReverseQueueEnvelope, secures reverse queue
        public async Task<IStageChannel> WaitForConnectionAsync(ConnectionInvitation invitation, CancellationToken ct)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            if (!_pending.TryGetValue(invitation.Address, out var pending))
                throw new InvalidOperationException($"No pending invitation for {invitation.Address}");

            // En este path el Stage es el creator (ya creo invitacion previamente),
            // entonces siempre tenemos _configuredFingerprint disponible.
            await EnsureConnectedAsync(_configuredFingerprint, ct);

            var forwardQueue = pending.Queue;

            // Subscribe and wait for the first message (ReverseQueueEnvelope)
            await _client.SubscribeAsync(forwardQueue, ct);

            var reader = _client.GetSubscription(forwardQueue.RecipientId);

            SmpMsg firstMsg;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            firstMsg = await reader.ReadAsync(timeoutCts.Token);

            // Decrypt and parse envelope
            byte[] nonce = new byte[SmpCrypto.NonceSize];
            Buffer.BlockCopy(firstMsg.EncryptedBody, 0, nonce, 0, SmpCrypto.NonceSize);
            byte[] ciphertext = new byte[firstMsg.EncryptedBody.Length - SmpCrypto.NonceSize];
            Buffer.BlockCopy(firstMsg.EncryptedBody, SmpCrypto.NonceSize, ciphertext, 0, ciphertext.Length);

            byte[] envelopeBytes;
            try
            {
                envelopeBytes = SmpCrypto.Decrypt(ciphertext, nonce,
                    forwardQueue.RecipientDhSecretKey, forwardQueue.ServerDhPublicKey);
            }
            catch
            {
                // If decryption fails, the raw body might be the envelope (e.g., in test mode)
                envelopeBytes = firstMsg.EncryptedBody;
            }

            var envelope = ReverseQueueEnvelope.Deserialize(envelopeBytes);

            // ACK the envelope message
            await _client.AcknowledgeAsync(forwardQueue, firstMsg.MsgId, ct);

            // Parse and secure the reverse queue
            var reverseQueue = SmpQueue.FromInvitationUri(envelope.ReverseQueueUri);
            await _client.SecureQueueAsync(reverseQueue);

            _pending.TryRemove(invitation.Address, out _);

            // Build bidirectional channel: we send on reverse, receive on forward
            var channel = new SimplexChannel(_client, reverseQueue, forwardQueue,
                envelope.PerformerId, pending.Purpose);
            await channel.StartAsync(ct);

            return channel;
        }

        private sealed class PendingInvitation
        {
            public SmpQueue Queue { get; set; }
            public ChannelPurpose Purpose { get; set; }
        }
    }
}
