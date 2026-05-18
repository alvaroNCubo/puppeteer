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
                        // Decrypt: [nonce(24) | ciphertext]
                        if (smpMsg.EncryptedBody.Length <= SmpCrypto.NonceSize) continue;

                        byte[] nonce = new byte[SmpCrypto.NonceSize];
                        Buffer.BlockCopy(smpMsg.EncryptedBody, 0, nonce, 0, SmpCrypto.NonceSize);

                        byte[] ciphertext = new byte[smpMsg.EncryptedBody.Length - SmpCrypto.NonceSize];
                        Buffer.BlockCopy(smpMsg.EncryptedBody, SmpCrypto.NonceSize, ciphertext, 0, ciphertext.Length);

                        byte[] plaintext = SmpCrypto.Decrypt(ciphertext, nonce,
                            _inboundQueue.RecipientDhSecretKey, _outboundQueue.SenderDhPublicKey);

                        // Check if it's a ReverseQueueEnvelope (marker 0xFF) or a StageMessage
                        if (plaintext.Length > 0 && plaintext[0] == ReverseQueueEnvelope.Marker)
                        {
                            // Skip envelope messages in channel receive - handled by transport
                            continue;
                        }

                        StageMessage stageMsg = StageMessage.Deserialize(plaintext);
                        await _receiveBuffer.Writer.WriteAsync(stageMsg, ct);

                        // ACK the message
                        await _client.AcknowledgeAsync(_inboundQueue, smpMsg.MsgId, ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SimplexChannel] Error processing message: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[SimplexChannel] ReceivePump error: {ex.Message}");
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

    // Control message for bidirectional channel setup
    internal sealed class ReverseQueueEnvelope
    {
        public const byte Marker = 0xFF;

        public string ReverseQueueUri { get; set; }
        public PerformerId PerformerId { get; set; }

        public byte[] Serialize()
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);
            writer.Write(Marker);
            byte[] uriBytes = System.Text.Encoding.UTF8.GetBytes(ReverseQueueUri);
            writer.Write(uriBytes.Length);
            writer.Write(uriBytes);
            writer.Write(PerformerId.ToBytes());
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

            return new ReverseQueueEnvelope
            {
                ReverseQueueUri = uri,
                PerformerId = PerformerId.From(idBytes)
            };
        }
    }
}
