using System.Threading;
using System.Threading.Tasks;
using Choreography.Transport;

namespace Choreography.Usher
{
    // Queue that shows the pending JoinRequests in the ContactSecret UI and returns the
    // operator's decision (D4: manual approval always in v1). The real handoff is
    // a persisted queue + web UI + signalR/websocket so the operator sees the
    // requests come in; here we only care about the contract.
    public interface IUsherApprovalQueue
    {
        Task<UsherApprovalDecision> RequestApprovalAsync(
            UsherJoinRequest request,
            PendingInvitation invitation,
            CancellationToken ct);
    }

    public sealed class UsherApprovalDecision
    {
        public bool IsApproved { get; }
        public OperatorId ApprovedBy { get; }
        public string RejectionReason { get; }

        private UsherApprovalDecision(bool approved, OperatorId approvedBy, string rejectionReason)
        {
            IsApproved = approved;
            ApprovedBy = approvedBy;
            RejectionReason = rejectionReason;
        }

        public static UsherApprovalDecision Approved(OperatorId by) =>
            new UsherApprovalDecision(true, by, string.Empty);

        public static UsherApprovalDecision Rejected(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new System.ArgumentNullException(nameof(reason));
            return new UsherApprovalDecision(false, default, reason);
        }
    }
}
