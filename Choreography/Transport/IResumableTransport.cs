using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;

namespace Choreography.Transport
{
    // Bug 19 (SMP) — Optional transport capability: reopen a previously established
    // channel after a process-death, WITHOUT re-handshake.
    //
    // This is a capability separate from IStageTransport (not all transports need it):
    //   - PortableHttps re-binds its listener, so the host-driven rejoin already closes via
    //     WaitForConnectionAsync; it does not implement this.
    //   - InMemory does not persist; it does not implement this.
    //   - SimpleX/SMP DOES: the invitation is single-use (queue KEY-secured) and the recipient key
    //     is ephemeral, so the only recovery path is to resume the channel from its
    //     persisted state (unilateral re-SUB; SMP is store-and-forward).
    //
    // The Stage queries it with `transport is IResumableTransport`; if the transport does not
    // expose it, Stage.ResumeChannelAsync returns null and the host falls back to pairing.
    public interface IResumableTransport
    {
        // Returns the resumed channel, or null if there is no persisted state for (peer, purpose).
        Task<IStageChannel> ResumeChannelAsync(PerformerId peer, ChannelPurpose purpose, CancellationToken ct);
    }
}
