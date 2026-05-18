using System.Threading;
using System.Threading.Tasks;

namespace Choreography.Transport
{
    // Promoted internal→public for Paper 7 Phase 2: the CLI that issues
    // onboarding invitations and the per-Docker host that joins via Usher
    // both live outside Choreography.dll. The implementations
    // (InMemoryTransport, HttpsTransport, SimplexTransport) stay internal
    // and are reached only through this abstraction.
    public interface IStageTransport
    {
        Task<ConnectionInvitation> CreateInvitationAsync(ChannelPurpose purpose);
        Task<IStageChannel> AcceptInvitationAsync(ConnectionInvitation invitation);
        Task<IStageChannel> WaitForConnectionAsync(ConnectionInvitation invitation, CancellationToken ct);
    }
}
