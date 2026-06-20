using System.Threading;
using System.Threading.Tasks;
using Choreography.Transport;
using Choreography.Usher;

namespace PuppeteerCli.Mocks
{
    // Paper 7 Phase 2 mock for IUsherApprovalQueue.
    //
    // Production ContactSecret shows the operator a UI to approve/reject each
    // join request manually. Phase 2 of Paper 7 demonstrates the
    // server-as-non-prerequisite property; the human-in-the-loop is
    // orthogonal to that demonstration and is replaced here by an
    // automatic-approver. The Usher onboard flow still runs end-to-end
    // (nonce, TTL, signature, sealed-box, journal-write).
    public sealed class AutoApprovingApprovalQueue : IUsherApprovalQueue
    {
        private readonly OperatorId approverId;

        public AutoApprovingApprovalQueue(OperatorId approverId)
        {
            this.approverId = approverId;
        }

        public Task<UsherApprovalDecision> RequestApprovalAsync(
            UsherJoinRequest request,
            PendingInvitation pending,
            CancellationToken ct)
        {
            return Task.FromResult(UsherApprovalDecision.Approved(approverId));
        }
    }
}
