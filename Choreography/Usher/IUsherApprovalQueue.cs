using System.Threading;
using System.Threading.Tasks;
using Choreography.Transport;

namespace Choreography.Usher
{
    // Cola que muestra los JoinRequest pendientes en la UI de ContactSecret y devuelve la
    // decision del operador (D4: approval manual siempre en v1). El handoff real es
    // una cola persistida + UI web + signalR/websocket para que el operador vea los
    // requests entrar; aqui solo nos importa el contrato.
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
