using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;
using Puppeteer.EventSourcing;

namespace Choreography.Transport.SimpleX
{
    // Envelope flow bidireccional para 2-Stage E2E con KEY simetrico.
    //
    // El modelo SMP v6 requiere que cada queue se "secure" con KEY antes de aceptar SEND
    // firmados. Como hay DOS queues por par (forward del creator, reverse del joiner),
    // ambos lados necesitan el senderSignPubKey + senderDhPubKey del otro out-of-band.
    //
    // Secuencia:
    //
    //   1. Creator: NEW(forwardQueue) -> IDS. (CreateInvitationAsync)
    //
    //   2. Joiner (AcceptInvitationAsync):
    //      - Parse invitation URI -> forwardQueue (rol Sender).
    //      - Genera (senderSign + senderDh) propias para la forward queue.
    //      - NEW(reverseQueue) -> joiner es Recipient.
    //      - SUB(reverseQueue).
    //      - SEND_unsigned_plaintext(forwardQueue, ReverseQueueEnvelope) con reverseUri +
    //        joiner.senderSignPub_F + joiner.senderDhPub_F.
    //      - Espera ForwardKeyEnvelope en reverseQueue (plaintext).
    //      - SecureQueueAsync(reverseQueue, creator.senderSignPub_R) [KEY].
    //      - reverseQueue.PeerSenderDhPublicKey = creator.senderDhPub_R.
    //      - Devuelve channel (outbound=forward, inbound=reverse).
    //
    //   3. Creator (WaitForConnectionAsync):
    //      - SUB(forwardQueue). Espera MSG con ReverseQueueEnvelope (plaintext).
    //      - SecureQueueAsync(forwardQueue, envelope.SenderSignPub_F) [KEY].
    //      - forwardQueue.PeerSenderDhPublicKey = envelope.SenderDhPub_F.
    //      - Parse reverseQueue desde reverseUri (rol Sender).
    //      - Genera (senderSign + senderDh) propias para reverseQueue.
    //      - SEND_unsigned_plaintext(reverseQueue, ForwardKeyEnvelope).
    //      - Devuelve channel (outbound=reverse, inbound=forward).
    //
    //   4. Ambas queues quedan secured. SENDs posteriores van firmados con crypto_box y
    //      se decryptan con el PeerSenderDhPublicKey registrado en cada inbound queue.
    //
    // Por que los envelopes van plaintext: el chicken-and-egg de crypto_box hace imposible
    // cifrar el primer mensaje (necesitarias la pubkey que precisamente vas a transportar).
    // Los envelopes solo contienen pubkeys publicas + queue URIs publicas, asi que un
    // observador pasivo no aprende secretos. Post-handshake, todos los SENDs usan crypto_box.
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

        // Stage1 creates a queue on the SMP server and returns the invitation.
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

        // Stage2 (joiner): parsea la invitation URI, genera sus claves de Sender sobre la
        // forward queue, crea la reverse queue, envia ReverseQueueEnvelope unsigned plaintext,
        // y espera el ForwardKeyEnvelope del creator. Devuelve channel bidireccional.
        public async Task<IStageChannel> AcceptInvitationAsync(ConnectionInvitation invitation)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            // 1. Parse invitation URI (incluye serverFingerprint TOFU).
            var forwardQueue = SmpQueue.FromInvitationUri(invitation.Address);
            byte[] keyHash = forwardQueue.ServerFingerprint
                ?? _configuredFingerprint
                ?? throw new InvalidOperationException(
                    "Invitation URI no incluye fingerprint y transport no fue configurado con uno. " +
                    "Verificar que el Ushier emita URIs en formato smp://HASH@host:port/...");

            await EnsureConnectedAsync(keyHash, CancellationToken.None);

            // 2. Generar par senderSign + senderDh ephemeral para la forward queue
            //    (joiner es Sender ahi).
            var (senderSignPub_F, senderSignSec_F) = SmpCrypto.GenerateSigningKeyPair();
            var (senderDhPub_F, senderDhSec_F) = SmpCrypto.GenerateDhKeyPair();
            forwardQueue.SenderSignPublicKey = senderSignPub_F;
            forwardQueue.SenderSignSecretKey = senderSignSec_F;
            forwardQueue.SenderDhPublicKey = senderDhPub_F;
            forwardQueue.SenderDhSecretKey = senderDhSec_F;

            // 3. Crear reverse queue (joiner es Recipient ahi).
            var reverseQueue = new SmpQueue(_smpServer, _smpPort) { ServerFingerprint = keyHash };
            await _client.CreateQueueAsync(reverseQueue);

            // 4. SUB a la reverse queue (necesitamos recibir el ForwardKeyEnvelope).
            await _client.SubscribeAsync(reverseQueue, CancellationToken.None);
            var reverseReader = _client.GetSubscription(reverseQueue.RecipientId);

            // 5. Construir y enviar ReverseQueueEnvelope (unsigned plaintext: la forward
            //    queue aun no esta secured con KEY y el chicken-and-egg de crypto_box
            //    impide cifrar el primer mensaje).
            var envelope = new ReverseQueueEnvelope
            {
                ReverseQueueUri = reverseQueue.ToInvitationUri(),
                PerformerId = _localId,
                SenderSignPubKey = senderSignPub_F,
                SenderDhPubKey = senderDhPub_F
            };
            byte[] envelopeBytes = envelope.Serialize();
            await _client.SendMessageUnsignedAsync(forwardQueue, envelopeBytes, CancellationToken.None);

            // 6. Esperar ForwardKeyEnvelope del creator en la reverse queue.
            //    Mismo unwrap server-encryption + paddedString.
            ForwardKeyEnvelope forwardKey;
            using (var awaitCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                SmpMsg msg = await reverseReader.ReadAsync(awaitCts.Token);
                byte[] fkBytes = DecryptServerEnvelope(msg, reverseQueue);
                forwardKey = ForwardKeyEnvelope.Deserialize(fkBytes);
                await _client.AcknowledgeAsync(reverseQueue, msg.MsgId, CancellationToken.None);
            }

            // 7. KEY sobre la reverse queue: registrar creator.senderSignPub_R.
            await _client.SecureQueueAsync(reverseQueue, forwardKey.SenderSignPubKey, CancellationToken.None);
            reverseQueue.PeerSenderDhPublicKey = forwardKey.SenderDhPubKey;

            // 8. Channel bidireccional: outbound=forward (joiner envia ahi), inbound=reverse
            //    (joiner recibe ahi).
            var channel = new SimplexChannel(_client, forwardQueue, reverseQueue,
                invitation.InviterId, invitation.Purpose);
            await channel.StartAsync(CancellationToken.None);

            return channel;
        }

        // Stage1 (creator): espera el ReverseQueueEnvelope en la forward queue, hace KEY de
        // esa queue con las pubkeys del joiner, genera sus claves para la reverse queue, y
        // envia ForwardKeyEnvelope unsigned. Devuelve channel bidireccional.
        public async Task<IStageChannel> WaitForConnectionAsync(ConnectionInvitation invitation, CancellationToken ct)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            if (!_pending.TryGetValue(invitation.Address, out var pending))
                throw new InvalidOperationException($"No pending invitation for {invitation.Address}");

            // En este path el Stage es el creator (ya creo invitacion previamente),
            // entonces siempre tenemos _configuredFingerprint disponible.
            await EnsureConnectedAsync(_configuredFingerprint, ct);

            var forwardQueue = pending.Queue;

            // 1. SUB la forward queue para recibir el ReverseQueueEnvelope.
            await _client.SubscribeAsync(forwardQueue, ct);
            var reader = _client.GetSubscription(forwardQueue.RecipientId);

            SmpMsg firstMsg;
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
                firstMsg = await reader.ReadAsync(timeoutCts.Token);
            }

            // 2. C2S decryption + paddedString unwrap. El SMP server encripta MSG bodies en
            //    delivery con NaCl crypto_box (X25519(srvDhSec,rcvDhPub) + HSalsa20-zero-nonce)
            //    y msgId padded-a-24 como nonce. El recipient decripta con la misma derivacion
            //    sobre su rcvDhSec + serverDhPub, luego unpad del paddedString y skip del meta
            //    block (SystemTime + MsgFlags + ' ') para recuperar el msgBody original.
            byte[] envelopeBytes = DecryptServerEnvelope(firstMsg, forwardQueue);
            var envelope = ReverseQueueEnvelope.Deserialize(envelopeBytes);

            if (envelope.SenderSignPubKey == null || envelope.SenderDhPubKey == null)
                throw new InvalidOperationException(
                    "ReverseQueueEnvelope no incluye SenderSignPubKey/SenderDhPubKey; " +
                    "version pre-Fase 7 del joiner detectada. Upgrade necesario.");

            await _client.AcknowledgeAsync(forwardQueue, firstMsg.MsgId, ct);

            // 3. KEY sobre forward queue con la senderSignPubKey del joiner.
            await _client.SecureQueueAsync(forwardQueue, envelope.SenderSignPubKey, ct);
            forwardQueue.PeerSenderDhPublicKey = envelope.SenderDhPubKey;

            // 4. Parse reverse queue (creator se vuelve Sender ahi).
            var reverseQueue = SmpQueue.FromInvitationUri(envelope.ReverseQueueUri);

            // 5. Generar par senderSign + senderDh ephemeral para la reverse queue.
            var (senderSignPub_R, senderSignSec_R) = SmpCrypto.GenerateSigningKeyPair();
            var (senderDhPub_R, senderDhSec_R) = SmpCrypto.GenerateDhKeyPair();
            reverseQueue.SenderSignPublicKey = senderSignPub_R;
            reverseQueue.SenderSignSecretKey = senderSignSec_R;
            reverseQueue.SenderDhPublicKey = senderDhPub_R;
            reverseQueue.SenderDhSecretKey = senderDhSec_R;

            // 6. ForwardKeyEnvelope sobre la reverse queue (unsigned plaintext).
            var fkEnvelope = new ForwardKeyEnvelope
            {
                SenderSignPubKey = senderSignPub_R,
                SenderDhPubKey = senderDhPub_R
            };
            await _client.SendMessageUnsignedAsync(reverseQueue, fkEnvelope.Serialize(), ct);

            _pending.TryRemove(invitation.Address, out _);

            // 7. Channel bidireccional: outbound=reverse (creator envia ahi), inbound=forward
            //    (creator recibe ahi).
            var channel = new SimplexChannel(_client, reverseQueue, forwardQueue,
                envelope.PerformerId, pending.Purpose);
            await channel.StartAsync(ct);

            return channel;
        }

        // Decrypt + unwrap MSG body (C2S server-to-recipient layer).
        //
        // simplexmq Server.hs:2030-2036 — encryptMsg = cbEncryptMaxLenBS rcvDhSecret (cbNonce msgId).
        // El server toma RcvMsgBody { msgTs, msgFlags, msgBody }, lo encodea como paddedString
        // de tamano fijo y lo encripta con NaCl crypto_box. La key del shared es el X25519 entre
        // serverDhSec_per_queue y recipientDhPub_per_queue (con el HSalsa20-zero-nonce wrap de
        // NaCl crypto_box_beforenm; ver SmpCrypto.DecryptC2SEnvelope). El nonce es el msgId
        // padeado a 24 bytes con ceros a la derecha. Wire layout:
        //
        //   EncryptedBody = [16-byte Poly1305 tag][16106-byte ciphertext]   (16122B total)
        //   plaintext     = [Word16 BE bodyLen][rcvMsgBody bytes]['#' padding to 16106]
        //   rcvMsgBody    = [SystemTime 8B][MsgFlags 1B + extras][' '][... msgBody ...]
        //
        // Devuelve el msgBody (lo que el sender envio originalmente al SMP server).
        internal static byte[] DecryptServerEnvelope(SmpMsg msg, SmpQueue receivingQueue)
        {
            if (msg == null) throw new ArgumentNullException(nameof(msg));
            if (msg.EncryptedBody == null) throw new ArgumentNullException(nameof(msg) + ".EncryptedBody");
            if (msg.MsgId == null) throw new ArgumentNullException(nameof(msg) + ".MsgId");
            if (receivingQueue == null) throw new ArgumentNullException(nameof(receivingQueue));
            if (receivingQueue.RecipientDhSecretKey == null)
                throw new InvalidOperationException("Queue has no RecipientDhSecretKey");
            if (receivingQueue.ServerDhPublicKey == null)
                throw new InvalidOperationException("Queue has no ServerDhPublicKey (set during NEW)");

            byte[] rcvMsgBody = SmpCrypto.DecryptC2SEnvelope(
                msg.EncryptedBody, msg.MsgId,
                receivingQueue.RecipientDhSecretKey,
                receivingQueue.ServerDhPublicKey);
            return SmpCrypto.ExtractMsgBodyFromRcvMsgBody(rcvMsgBody);
        }

        private sealed class PendingInvitation
        {
            public SmpQueue Queue { get; set; }
            public ChannelPurpose Purpose { get; set; }
        }
    }
}
