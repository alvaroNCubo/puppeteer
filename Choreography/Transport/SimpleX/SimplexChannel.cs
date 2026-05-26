using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Choreography.StageManager;

namespace Choreography.Transport.SimpleX
{
    internal sealed class SimplexChannel : IStageChannel
    {
        private readonly SmpClient _client;
        private readonly SmpQueue _outboundQueue; // we SEND here
        private readonly SmpQueue _inboundQueue;  // we RECEIVE here
        private readonly Channel<StageMessage> _receiveBuffer = Channel.CreateUnbounded<StageMessage>();
        private bool _connected = true;
        private Task _receivePumpTask;
        private CancellationTokenSource _pumpCts;

        public PerformerId RemotePerformerId { get; }
        public ChannelPurpose Purpose { get; }
        public bool IsConnected => _connected && _client.IsConnected;
        public event Action<IStageChannel> OnDisconnected;

        internal SimplexChannel(SmpClient client, SmpQueue outboundQueue, SmpQueue inboundQueue,
            PerformerId remotePerformerId, ChannelPurpose purpose)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (outboundQueue == null) throw new ArgumentNullException(nameof(outboundQueue));
            if (inboundQueue == null) throw new ArgumentNullException(nameof(inboundQueue));

            _client = client;
            _outboundQueue = outboundQueue;
            _inboundQueue = inboundQueue;
            RemotePerformerId = remotePerformerId;
            Purpose = purpose;
        }

        internal async Task StartAsync(CancellationToken ct)
        {
            // Subscribe to inbound queue to receive messages
            await _client.SubscribeAsync(_inboundQueue, ct);

            _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receivePumpTask = Task.Run(() => ReceivePumpAsync(_pumpCts.Token));
        }

        public async Task SendAsync(StageMessage message, CancellationToken ct = default)
        {
            if (!IsConnected) throw new InvalidOperationException("Channel is disconnected");
            if (message == null) throw new ArgumentNullException(nameof(message));

            byte[] serialized = message.Serialize();
            await _client.SendMessageAsync(_outboundQueue, serialized, ct);
        }

        public async IAsyncEnumerable<StageMessage> Receive([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var msg in _receiveBuffer.Reader.ReadAllAsync(ct))
            {
                yield return msg;
            }
        }

        private async Task ReceivePumpAsync(CancellationToken ct)
        {
            try
            {
                var reader = _client.GetSubscription(_inboundQueue.RecipientId);

                await foreach (var smpMsg in reader.ReadAllAsync(ct))
                {
                    try
                    {
                        // Wire layout que el SMP server entrega al recipient (mismo formato
                        // que reciben AcceptInvitationAsync/WaitForConnectionAsync durante el
                        // handshake):
                        //
                        //   Layer 1 (C2S, server-to-recipient):
                        //     smpMsg.EncryptedBody = [16B Poly1305 tag][16106B padded crypto_box]
                        //     -> DecryptServerEnvelope hace C2S unwrap + strip del meta block
                        //        (SystemTime + MsgFlags + ' ') y devuelve el msgBody original
                        //        que el sender envio.
                        //
                        //   Layer 2 (E2E, peer-to-peer):
                        //     msgBody = [24B sender nonce][crypto_box(stage payload)]
                        //     -> peer-decrypt con ECDH(myRecDhSec, peerSenderDhPub).
                        //
                        // Antes del fix este path aplicaba la Layer 2 directamente al
                        // EncryptedBody sin haber strippeado el wrap C2S del server, lo que
                        // disparaba Poly1305 tag mismatch en el primer (y todos los demas)
                        // mensaje post-handshake (caso reportado en 2-Kora demos).
                        byte[] msgBody = SimplexTransport.DecryptServerEnvelope(smpMsg, _inboundQueue);

                        if (msgBody.Length <= SmpCrypto.NonceSize) continue;

                        byte[] nonce = new byte[SmpCrypto.NonceSize];
                        Buffer.BlockCopy(msgBody, 0, nonce, 0, SmpCrypto.NonceSize);

                        byte[] ciphertext = new byte[msgBody.Length - SmpCrypto.NonceSize];
                        Buffer.BlockCopy(msgBody, SmpCrypto.NonceSize, ciphertext, 0, ciphertext.Length);

                        // ECDH para decryptar: shared = ECDH(myRecDhSec, peerSenderDhPub).
                        // peerSenderDhPub se aprende del envelope de handshake (ver SimplexTransport).
                        if (_inboundQueue.PeerSenderDhPublicKey == null)
                            throw new InvalidOperationException(
                                "Inbound queue PeerSenderDhPublicKey not set; envelope handshake incomplete");

                        byte[] plaintext = SmpCrypto.Decrypt(ciphertext, nonce,
                            _inboundQueue.RecipientDhSecretKey, _inboundQueue.PeerSenderDhPublicKey);

                        // Skip handshake envelopes if a stray copy lands here (transport handles them).
                        if (plaintext.Length > 0 &&
                            (plaintext[0] == ReverseQueueEnvelope.Marker || plaintext[0] == ForwardKeyEnvelope.Marker))
                        {
                            await _client.AcknowledgeAsync(_inboundQueue, smpMsg.MsgId, ct);
                            continue;
                        }

                        StageMessage stageMsg = StageMessage.Deserialize(plaintext);
                        await _receiveBuffer.Writer.WriteAsync(stageMsg, ct);

                        // ACK the message
                        await _client.AcknowledgeAsync(_inboundQueue, smpMsg.MsgId, ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SimplexChannel:{Purpose}] Error processing message: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimplexChannel:{Purpose}] ReceivePump error: {ex.Message}");
            }
            finally
            {
                _connected = false;
                _receiveBuffer.Writer.TryComplete();
                OnDisconnected?.Invoke(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            _connected = false;
            _pumpCts?.Cancel();
            _receiveBuffer.Writer.TryComplete();
            _pumpCts?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // Control message for bidirectional channel setup.
    //
    // Lo manda el joiner al creator sobre la forward queue (la creada por el creator),
    // como primer SEND unsigned. Contiene:
    //   - ReverseQueueUri: invitation URI de la queue creada por el joiner para SENDs del creator.
    //   - PerformerId: identidad del joiner.
    //   - SenderSignPubKey: la pubkey Ed25519 del joiner para firmar SENDs en la forward queue.
    //     El creator la registra via KEY para securizar la forward queue.
    //   - SenderDhPubKey: la pubkey X25519 ephemeral del joiner usada como sender en la forward queue.
    //     El creator la guarda en forwardQueue.PeerSenderDhPublicKey para decryptar SENDs posteriores.
    //
    // Layout v2 (back-compat con v1 que no llevaba pubkeys):
    //   [Marker(1)] [UriLen(4)] [UriBytes] [PerformerId(16)] [SenderSignPubKey(32)?] [SenderDhPubKey(32)?]
    // Las dos ultimas son opcionales y se detectan por longitud restante (>= 64).
    internal sealed class ReverseQueueEnvelope
    {
        public const byte Marker = 0xFF;

        public string ReverseQueueUri { get; set; }
        public PerformerId PerformerId { get; set; }
        public byte[] SenderSignPubKey { get; set; }   // 32 B, Ed25519 pubkey
        public byte[] SenderDhPubKey { get; set; }     // 32 B, X25519 pubkey

        public byte[] Serialize()
        {
            if (ReverseQueueUri == null) throw new InvalidOperationException("ReverseQueueUri not set");
            if (SenderSignPubKey != null && SenderSignPubKey.Length != 32)
                throw new InvalidOperationException("SenderSignPubKey must be 32 bytes");
            if (SenderDhPubKey != null && SenderDhPubKey.Length != 32)
                throw new InvalidOperationException("SenderDhPubKey must be 32 bytes");

            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            writer.Write(Marker);
            byte[] uriBytes = System.Text.Encoding.UTF8.GetBytes(ReverseQueueUri);
            writer.Write(uriBytes.Length);
            writer.Write(uriBytes);
            writer.Write(PerformerId.ToBytes());
            if (SenderSignPubKey != null && SenderDhPubKey != null)
            {
                writer.Write(SenderSignPubKey);
                writer.Write(SenderDhPubKey);
            }
            return ms.ToArray();
        }

        public static ReverseQueueEnvelope Deserialize(byte[] data)
        {
            if (data == null || data.Length == 0) throw new ArgumentNullException(nameof(data));
            if (data[0] != Marker) throw new ArgumentException("Not a ReverseQueueEnvelope");

            using var ms = new System.IO.MemoryStream(data);
            using var reader = new System.IO.BinaryReader(ms);
            reader.ReadByte(); // skip marker
            int uriLen = reader.ReadInt32();
            string uri = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(uriLen));
            byte[] idBytes = reader.ReadBytes(16);

            var env = new ReverseQueueEnvelope
            {
                ReverseQueueUri = uri,
                PerformerId = PerformerId.From(idBytes)
            };

            long remaining = ms.Length - ms.Position;
            if (remaining >= 64)
            {
                env.SenderSignPubKey = reader.ReadBytes(32);
                env.SenderDhPubKey = reader.ReadBytes(32);
            }

            return env;
        }
    }

    // Inverse handshake envelope: lo manda el creator al joiner sobre la reverse queue
    // (la creada por el joiner), como primer SEND unsigned. Contiene las dos pubkeys del
    // creator en rol de Sender sobre la reverse queue. El joiner las usa para:
    //   - SenderSignPubKey: KEY sobre la reverse queue (joiner es recipient ahi).
    //   - SenderDhPubKey: guardar en reverseQueue.PeerSenderDhPublicKey para decryptar.
    internal sealed class ForwardKeyEnvelope
    {
        public const byte Marker = 0xFE;

        public byte[] SenderSignPubKey { get; set; }   // 32 B
        public byte[] SenderDhPubKey { get; set; }     // 32 B

        public byte[] Serialize()
        {
            if (SenderSignPubKey == null || SenderSignPubKey.Length != 32)
                throw new InvalidOperationException("SenderSignPubKey must be 32 bytes");
            if (SenderDhPubKey == null || SenderDhPubKey.Length != 32)
                throw new InvalidOperationException("SenderDhPubKey must be 32 bytes");

            byte[] result = new byte[1 + 32 + 32];
            result[0] = Marker;
            Buffer.BlockCopy(SenderSignPubKey, 0, result, 1, 32);
            Buffer.BlockCopy(SenderDhPubKey, 0, result, 33, 32);
            return result;
        }

        public static ForwardKeyEnvelope Deserialize(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length != 1 + 32 + 32) throw new ArgumentException("Invalid ForwardKeyEnvelope length");
            if (data[0] != Marker) throw new ArgumentException("Not a ForwardKeyEnvelope");

            byte[] signPub = new byte[32];
            byte[] dhPub = new byte[32];
            Buffer.BlockCopy(data, 1, signPub, 0, 32);
            Buffer.BlockCopy(data, 33, dhPub, 0, 32);
            return new ForwardKeyEnvelope
            {
                SenderSignPubKey = signPub,
                SenderDhPubKey = dhPub
            };
        }
    }
}
