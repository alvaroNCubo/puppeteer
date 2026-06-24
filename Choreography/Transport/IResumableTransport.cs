using System.Threading;
using System.Threading.Tasks;
using Choreography.StageManager;

namespace Choreography.Transport
{
    // Bug 19 (SMP) — Capacidad opcional de un transport: reabrir un canal previamente
    // establecido tras un process-death, SIN re-handshake.
    //
    // Es una capability aparte de IStageTransport (no todos los transports la necesitan):
    //   - PortableHttps re-bindea su listener, asi que el rejoin host-driven ya cierra via
    //     WaitForConnectionAsync; no implementa esto.
    //   - InMemory no persiste; no implementa esto.
    //   - SimpleX/SMP SI: la invitacion es single-use (queue KEY-secured) y la recipient key
    //     es efimera, asi que el unico camino de recuperacion es resumir el canal desde su
    //     estado persistido (re-SUB unilateral; SMP es store-and-forward).
    //
    // El Stage la consulta con `transport is IResumableTransport`; si el transport no la
    // expone, Stage.ResumeChannelAsync devuelve null y el host cae a su fallback de pairing.
    public interface IResumableTransport
    {
        // Devuelve el canal resumido, o null si no hay estado persistido para (peer, purpose).
        Task<IStageChannel> ResumeChannelAsync(PerformerId peer, ChannelPurpose purpose, CancellationToken ct);
    }
}
