using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Choreography.StageManager;

namespace Choreography.Transport.Https
{
    internal sealed class HttpsChannel : IStageChannel
    {
        private readonly PerformerId localId;
        private readonly string remoteUrl;
        private readonly HttpClient httpClient;
        private readonly Channel<StageMessage> incomingMessages;
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private volatile bool connected = true;

        public PerformerId RemotePerformerId { get; }
        public ChannelPurpose Purpose { get; }
        public bool IsConnected => connected;
        public event Action<IStageChannel> OnDisconnected;

        internal HttpsChannel(PerformerId localId, PerformerId remoteId, ChannelPurpose purpose,
            string remoteUrl, string pinnedFingerprintHex)
        {
            if (string.IsNullOrWhiteSpace(remoteUrl)) throw new ArgumentNullException(nameof(remoteUrl));

            this.localId = localId;
            this.RemotePerformerId = remoteId;
            this.Purpose = purpose;
            this.remoteUrl = remoteUrl.TrimEnd('/');
            // TLS-pinned client. If pinnedFingerprintHex is null the client
            // accepts any server cert — used only by the loopback test path.
            this.httpClient = HttpsClientFactory.BuildClient(pinnedFingerprintHex);
            this.incomingMessages = Channel.CreateUnbounded<StageMessage>();
        }

        public async Task SendAsync(StageMessage message, CancellationToken ct = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (!connected) throw new InvalidOperationException("Channel is disconnected");

            await sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                byte[] serialized = message.Serialize();

                // POST body layout (wire format v2):
                //   byte[16]   sender PerformerId (Guid bytes)
                //   byte[1]    ChannelPurpose tag of THIS channel
                //   byte[N]    serialized StageMessage
                //
                // The purpose byte disambiguates which of the (up to 4)
                // simultaneous channels between the same pair of peers
                // should receive this message. Without it, the listener
                // can only key its channel dictionary by sender, and
                // multiple channels between the same pair overwrite each
                // other — Director→Cast Replication CueEvents end up
                // delivered to whichever channel was registered last
                // (typically Command), and the receiver's ListenReplication
                // never sees them.
                byte[] senderIdBytes = localId.ToBytes();
                byte[] payload = new byte[senderIdBytes.Length + 1 + serialized.Length];
                Buffer.BlockCopy(senderIdBytes, 0, payload, 0, senderIdBytes.Length);
                payload[senderIdBytes.Length] = (byte)Purpose;
                Buffer.BlockCopy(serialized, 0, payload, senderIdBytes.Length + 1, serialized.Length);

                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                using var response = await httpClient.PostAsync($"{remoteUrl}/messages", content, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[HttpsChannel] Send failed to {remoteUrl}: {ex.Message}");
                Disconnect();
                throw;
            }
            finally
            {
                sendLock.Release();
            }
        }

        public async IAsyncEnumerable<StageMessage> Receive([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var message in incomingMessages.Reader.ReadAllAsync(ct))
            {
                yield return message;
            }
        }

        internal void EnqueueReceived(StageMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            if (!incomingMessages.Writer.TryWrite(message))
            {
                System.Diagnostics.Debug.WriteLine($"[HttpsChannel] Failed to enqueue message from {RemotePerformerId}");
            }
        }

        public ValueTask DisposeAsync()
        {
            Disconnect();
            httpClient.Dispose();
            return ValueTask.CompletedTask;
        }

        private void Disconnect()
        {
            if (!connected) return;
            connected = false;
            incomingMessages.Writer.TryComplete();
            OnDisconnected?.Invoke(this);
        }
    }
}
