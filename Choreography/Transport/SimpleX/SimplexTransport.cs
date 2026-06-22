using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;
using Puppeteer;
using Puppeteer.EventSourcing;

namespace Choreography.Transport.SimpleX
{
    // Bidirectional envelope flow for 2-Stage E2E with symmetric KEY.
    //
    // The SMP v6 model requires each queue to be "secured" with KEY before accepting signed
    // SENDs. Since there are TWO queues per pair (forward from the creator, reverse from the joiner),
    // both sides need the other's senderSignPubKey + senderDhPubKey out-of-band.
    //
    // Sequence:
    //
    //   1. Creator: NEW(forwardQueue) -> IDS. (CreateInvitationAsync)
    //
    //   2. Joiner (AcceptInvitationAsync):
    //      - Parse invitation URI -> forwardQueue (Sender role).
    //      - Generates its own (senderSign + senderDh) for the forward queue.
    //      - NEW(reverseQueue) -> joiner is Recipient.
    //      - SUB(reverseQueue).
    //      - SEND_unsigned_plaintext(forwardQueue, ReverseQueueEnvelope) with reverseUri +
    //        joiner.senderSignPub_F + joiner.senderDhPub_F.
    //      - Waits for ForwardKeyEnvelope on reverseQueue (plaintext).
    //      - SecureQueueAsync(reverseQueue, creator.senderSignPub_R) [KEY].
    //      - reverseQueue.PeerSenderDhPublicKey = creator.senderDhPub_R.
    //      - Returns channel (outbound=forward, inbound=reverse).
    //
    //   3. Creator (WaitForConnectionAsync):
    //      - SUB(forwardQueue). Waits for MSG with ReverseQueueEnvelope (plaintext).
    //      - SecureQueueAsync(forwardQueue, envelope.SenderSignPub_F) [KEY].
    //      - forwardQueue.PeerSenderDhPublicKey = envelope.SenderDhPub_F.
    //      - Parse reverseQueue from reverseUri (Sender role).
    //      - Generates its own (senderSign + senderDh) for reverseQueue.
    //      - SEND_unsigned_plaintext(reverseQueue, ForwardKeyEnvelope).
    //      - Returns channel (outbound=reverse, inbound=forward).
    //
    //   4. Both queues end up secured. Subsequent SENDs are signed with crypto_box and
    //      are decrypted with the PeerSenderDhPublicKey registered in each inbound queue.
    //
    // Why the envelopes go plaintext: the crypto_box chicken-and-egg makes it impossible
    // to encrypt the first message (you would need the pubkey you are precisely about to transport).
    // The envelopes only contain public pubkeys + public queue URIs, so a
    // passive observer learns no secrets. Post-handshake, all SENDs use crypto_box.
    internal sealed class SimplexTransport : IStageTransport
    {
        private readonly PerformerId _localId;
        private readonly string _smpServer;
        private readonly int _smpPort;
        // serverFingerprint = SHA-256(idCert) that the Stage knows a-priori (TOFU).
        // For the creator (Stage that creates invitations via the invitation issuer) it comes from the config.
        // For joiner-only (only accepts invitations), it may be null and is extracted from the URI.
        private readonly byte[] _configuredFingerprint;
        private readonly IPuppeteerLogger _logger;
        private SmpClient _client;
        private readonly ConcurrentDictionary<string, PendingInvitation> _pending = new();
        private readonly SemaphoreSlim _connectLock = new(1, 1);

        internal IPuppeteerLogger Logger => _logger;

        public SimplexTransport(PerformerId localId, string smpServerUrl, byte[] serverFingerprint = null,
            IPuppeteerLogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(smpServerUrl)) throw new ArgumentNullException(nameof(smpServerUrl));
            _localId = localId;
            ParseServerUrl(smpServerUrl, out _smpServer, out _smpPort);
            _configuredFingerprint = serverFingerprint;
            _logger = logger ?? new ConsoleLogger();
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

                _client = new SmpClient(_smpServer, _smpPort, keyHash, _logger);
                await _client.ConnectAsync(ct);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        // Stage1 creates a queue on the SMP server and returns the invitation.
        // Requires that the Stage config provided serverFingerprint (invitation-issuer role).
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

        // Stage2 (joiner): parses the invitation URI, generates its Sender keys over the
        // forward queue, creates the reverse queue, sends ReverseQueueEnvelope unsigned plaintext,
        // and waits for the creator's ForwardKeyEnvelope. Returns a bidirectional channel.
        public async Task<IStageChannel> AcceptInvitationAsync(ConnectionInvitation invitation)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            // 1. Parse invitation URI (includes serverFingerprint TOFU).
            var forwardQueue = SmpQueue.FromInvitationUri(invitation.Address);
            byte[] keyHash = forwardQueue.ServerFingerprint
                ?? _configuredFingerprint
                ?? throw new InvalidOperationException(
                    "Invitation URI no incluye fingerprint y transport no fue configurado con uno. " +
                    "Verificar que el Ushier emita URIs en formato smp://HASH@host:port/...");

            await EnsureConnectedAsync(keyHash, CancellationToken.None);

            // 2. Generate an ephemeral senderSign + senderDh pair for the forward queue
            //    (joiner is Sender there).
            var (senderSignPub_F, senderSignSec_F) = SmpCrypto.GenerateSigningKeyPair();
            var (senderDhPub_F, senderDhSec_F) = SmpCrypto.GenerateDhKeyPair();
            forwardQueue.SenderSignPublicKey = senderSignPub_F;
            forwardQueue.SenderSignSecretKey = senderSignSec_F;
            forwardQueue.SenderDhPublicKey = senderDhPub_F;
            forwardQueue.SenderDhSecretKey = senderDhSec_F;

            // 3. Create the reverse queue (joiner is Recipient there).
            var reverseQueue = new SmpQueue(_smpServer, _smpPort) { ServerFingerprint = keyHash };
            await _client.CreateQueueAsync(reverseQueue);

            // 4. SUB to the reverse queue (we need to receive the ForwardKeyEnvelope).
            await _client.SubscribeAsync(reverseQueue, CancellationToken.None);
            var reverseReader = _client.GetSubscription(reverseQueue.RecipientId);

            // 5. Build and send ReverseQueueEnvelope (unsigned plaintext: the forward
            //    queue is not yet secured with KEY and the crypto_box chicken-and-egg
            //    prevents encrypting the first message).
            var envelope = new ReverseQueueEnvelope
            {
                ReverseQueueUri = reverseQueue.ToInvitationUri(),
                PerformerId = _localId,
                SenderSignPubKey = senderSignPub_F,
                SenderDhPubKey = senderDhPub_F
            };
            byte[] envelopeBytes = envelope.Serialize();
            await _client.SendMessageUnsignedAsync(forwardQueue, envelopeBytes, CancellationToken.None);

            // 6. Wait for the creator's ForwardKeyEnvelope on the reverse queue.
            //    Same server-encryption + paddedString unwrap.
            ForwardKeyEnvelope forwardKey;
            using (var awaitCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                SmpMsg msg = await reverseReader.ReadAsync(awaitCts.Token);
                byte[] fkBytes = DecryptServerEnvelope(msg, reverseQueue);
                forwardKey = ForwardKeyEnvelope.Deserialize(fkBytes);
                await _client.AcknowledgeAsync(reverseQueue, msg.MsgId, CancellationToken.None);
            }

            // 7. KEY over the reverse queue: register creator.senderSignPub_R.
            await _client.SecureQueueAsync(reverseQueue, forwardKey.SenderSignPubKey, CancellationToken.None);
            reverseQueue.PeerSenderDhPublicKey = forwardKey.SenderDhPubKey;

            // 8. Bidirectional channel: outbound=forward (joiner sends there), inbound=reverse
            //    (joiner receives there).
            var channel = new SimplexChannel(_client, forwardQueue, reverseQueue,
                invitation.InviterId, invitation.Purpose, _logger);
            await channel.StartAsync(CancellationToken.None);

            return channel;
        }

        // Stage1 (creator): waits for the ReverseQueueEnvelope on the forward queue, does KEY on
        // that queue with the joiner's pubkeys, generates its keys for the reverse queue, and
        // sends ForwardKeyEnvelope unsigned. Returns a bidirectional channel.
        public async Task<IStageChannel> WaitForConnectionAsync(ConnectionInvitation invitation, CancellationToken ct)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            if (!_pending.TryGetValue(invitation.Address, out var pending))
                throw new InvalidOperationException($"No pending invitation for {invitation.Address}");

            // On this path the Stage is the creator (it already created the invitation previously),
            // so we always have _configuredFingerprint available.
            await EnsureConnectedAsync(_configuredFingerprint, ct);

            var forwardQueue = pending.Queue;

            // 1. SUB the forward queue to receive the ReverseQueueEnvelope.
            await _client.SubscribeAsync(forwardQueue, ct);
            var reader = _client.GetSubscription(forwardQueue.RecipientId);

            SmpMsg firstMsg;
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
                firstMsg = await reader.ReadAsync(timeoutCts.Token);
            }

            // 2. C2S decryption + paddedString unwrap. The SMP server encrypts MSG bodies on
            //    delivery with NaCl crypto_box (X25519(srvDhSec,rcvDhPub) + HSalsa20-zero-nonce)
            //    and msgId padded-to-24 as the nonce. The recipient decrypts with the same derivation
            //    over its rcvDhSec + serverDhPub, then unpads the paddedString and skips the meta
            //    block (SystemTime + MsgFlags + ' ') to recover the original msgBody.
            byte[] envelopeBytes = DecryptServerEnvelope(firstMsg, forwardQueue);
            var envelope = ReverseQueueEnvelope.Deserialize(envelopeBytes);

            if (envelope.SenderSignPubKey == null || envelope.SenderDhPubKey == null)
                throw new InvalidOperationException(
                    "ReverseQueueEnvelope no incluye SenderSignPubKey/SenderDhPubKey; " +
                    "version pre-Fase 7 del joiner detectada. Upgrade necesario.");

            await _client.AcknowledgeAsync(forwardQueue, firstMsg.MsgId, ct);

            // 3. KEY over the forward queue with the joiner's senderSignPubKey.
            await _client.SecureQueueAsync(forwardQueue, envelope.SenderSignPubKey, ct);
            forwardQueue.PeerSenderDhPublicKey = envelope.SenderDhPubKey;

            // 4. Parse reverse queue (creator becomes Sender there).
            var reverseQueue = SmpQueue.FromInvitationUri(envelope.ReverseQueueUri);

            // 5. Generate an ephemeral senderSign + senderDh pair for the reverse queue.
            var (senderSignPub_R, senderSignSec_R) = SmpCrypto.GenerateSigningKeyPair();
            var (senderDhPub_R, senderDhSec_R) = SmpCrypto.GenerateDhKeyPair();
            reverseQueue.SenderSignPublicKey = senderSignPub_R;
            reverseQueue.SenderSignSecretKey = senderSignSec_R;
            reverseQueue.SenderDhPublicKey = senderDhPub_R;
            reverseQueue.SenderDhSecretKey = senderDhSec_R;

            // 6. ForwardKeyEnvelope over the reverse queue (unsigned plaintext).
            var fkEnvelope = new ForwardKeyEnvelope
            {
                SenderSignPubKey = senderSignPub_R,
                SenderDhPubKey = senderDhPub_R
            };
            await _client.SendMessageUnsignedAsync(reverseQueue, fkEnvelope.Serialize(), ct);

            _pending.TryRemove(invitation.Address, out _);

            // 7. Bidirectional channel: outbound=reverse (creator sends there), inbound=forward
            //    (creator receives there).
            var channel = new SimplexChannel(_client, reverseQueue, forwardQueue,
                envelope.PerformerId, pending.Purpose, _logger);
            await channel.StartAsync(ct);

            return channel;
        }

        // Decrypt + unwrap MSG body (C2S server-to-recipient layer).
        //
        // simplexmq Server.hs:2030-2036 — encryptMsg = cbEncryptMaxLenBS rcvDhSecret (cbNonce msgId).
        // The server takes RcvMsgBody { msgTs, msgFlags, msgBody }, encodes it as a fixed-size
        // paddedString and encrypts it with NaCl crypto_box. The shared key is the X25519 between
        // serverDhSec_per_queue and recipientDhPub_per_queue (with the HSalsa20-zero-nonce wrap of
        // NaCl crypto_box_beforenm; see SmpCrypto.DecryptC2SEnvelope). The nonce is the msgId
        // padded to 24 bytes with trailing zeros. Wire layout:
        //
        //   EncryptedBody = [16-byte Poly1305 tag][16106-byte ciphertext]   (16122B total)
        //   plaintext     = [Word16 BE bodyLen][rcvMsgBody bytes]['#' padding to 16106]
        //   rcvMsgBody    = [SystemTime 8B][MsgFlags 1B + extras][' '][... msgBody ...]
        //
        // Returns the msgBody (what the sender originally sent to the SMP server).
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
