using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Choreography.StageManager;
using Puppeteer;
using Puppeteer.EventSourcing;

namespace Choreography.Transport
{
    internal sealed class InMemoryTransport : IStageTransport
    {
        private readonly PerformerId _localId;
        private readonly IPuppeteerLogger _logger;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IStageChannel>> _pendingInvitations = new();
        private static readonly ConcurrentDictionary<string, InMemoryTransport> _registry = new();

        public InMemoryTransport(PerformerId localId, IPuppeteerLogger logger = null)
        {
            _localId = localId;
            _logger = logger ?? new ConsoleLogger();
            _registry[localId.ToString()] = this;
        }

        public Task<ConnectionInvitation> CreateInvitationAsync(ChannelPurpose purpose)
        {
            string address = $"{_localId}:{purpose}:{Guid.NewGuid():N}";
            var invitation = new ConnectionInvitation(_localId, purpose, address);
            return Task.FromResult(invitation);
        }

        public async Task<IStageChannel> AcceptInvitationAsync(ConnectionInvitation invitation)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            string inviterKey = invitation.InviterId.ToString();
            if (!_registry.TryGetValue(inviterKey, out InMemoryTransport inviterTransport))
                throw new InvalidOperationException($"Performer {invitation.InviterId} not found in registry");

            var (channelA, channelB) = InMemoryChannel.CreatePair(
                _localId, invitation.InviterId, invitation.Purpose);

            if (!inviterTransport._pendingInvitations.TryGetValue(invitation.Address, out var tcs))
            {
                tcs = new TaskCompletionSource<IStageChannel>();
                inviterTransport._pendingInvitations[invitation.Address] = tcs;
            }

            tcs.TrySetResult(channelA);

            return channelB;
        }

        public async Task<IStageChannel> WaitForConnectionAsync(ConnectionInvitation invitation, CancellationToken ct)
        {
            if (invitation == null) throw new ArgumentNullException(nameof(invitation));

            var tcs = _pendingInvitations.GetOrAdd(invitation.Address, _ => new TaskCompletionSource<IStageChannel>());

            using var registration = ct.Register(() => tcs.TrySetCanceled());
            var channel = await tcs.Task;

            _pendingInvitations.TryRemove(invitation.Address, out _);
            return channel;
        }

        public static void ClearRegistry()
        {
            _registry.Clear();
        }
    }

    internal sealed class InMemoryChannel : IStageChannel
    {
        private readonly Channel<StageMessage> _incoming;
        private readonly Channel<StageMessage> _outgoing;
        private bool _connected = true;
        private InMemoryChannel _peer;

        public PerformerId RemotePerformerId { get; }
        public ChannelPurpose Purpose { get; }
        public bool IsConnected => _connected;
        public event Action<IStageChannel> OnDisconnected;

        private InMemoryChannel(PerformerId remoteId, ChannelPurpose purpose,
            Channel<StageMessage> incoming, Channel<StageMessage> outgoing)
        {
            RemotePerformerId = remoteId;
            Purpose = purpose;
            _incoming = incoming;
            _outgoing = outgoing;
        }

        internal static (InMemoryChannel forInviter, InMemoryChannel forAccepter) CreatePair(
            PerformerId accepterId, PerformerId inviterId, ChannelPurpose purpose)
        {
            var channelAtoB = Channel.CreateUnbounded<StageMessage>();
            var channelBtoA = Channel.CreateUnbounded<StageMessage>();

            var forInviter = new InMemoryChannel(accepterId, purpose, channelBtoA, channelAtoB);
            var forAccepter = new InMemoryChannel(inviterId, purpose, channelAtoB, channelBtoA);

            forInviter._peer = forAccepter;
            forAccepter._peer = forInviter;

            return (forInviter, forAccepter);
        }

        public async Task SendAsync(StageMessage message, CancellationToken ct = default)
        {
            if (!_connected) throw new InvalidOperationException("Channel is disconnected");
            await _outgoing.Writer.WriteAsync(message, ct);
        }

        public async IAsyncEnumerable<StageMessage> Receive([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var message in _incoming.Reader.ReadAllAsync(ct))
            {
                yield return message;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disconnect();
            return ValueTask.CompletedTask;
        }

        internal void Disconnect()
        {
            if (!_connected) return;
            _connected = false;
            _outgoing.Writer.TryComplete();
            _incoming.Writer.TryComplete();

            OnDisconnected?.Invoke(this);

            if (_peer != null && _peer._connected)
            {
                _peer.Disconnect();
            }
        }
    }
}
