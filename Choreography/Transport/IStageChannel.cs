using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;

namespace Choreography.Transport
{
    public interface IStageChannel : IAsyncDisposable
    {
        PerformerId RemotePerformerId { get; }
        ChannelPurpose Purpose { get; }
        bool IsConnected { get; }
        Task SendAsync(StageMessage message, CancellationToken ct = default);
        IAsyncEnumerable<StageMessage> Receive(CancellationToken ct);
        event Action<IStageChannel> OnDisconnected;
    }
}
