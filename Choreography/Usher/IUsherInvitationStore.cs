using System;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Transport;

namespace Choreography.Usher
{
    // Persistencia de las invitaciones emitidas por el Usher. Tiene que sobrevivir
    // restart de ContactSecret: una invitacion emitida hace 10 minutos debe seguir siendo
    // valida si el operador reinicio mientras tanto, hasta que llegue su TTL.
    //
    // El handoff real puede persistir en sqlite/file/etc. Para el test scaffold
    // basta con una impl in-memory (incluida en el test project).
    public interface IUsherInvitationStore
    {
        Task SaveAsync(PendingInvitation invitation, CancellationToken ct);
        Task<PendingInvitation> FindByNonceAsync(Guid nonce, CancellationToken ct);
        Task MarkConsumedAsync(Guid nonce, CancellationToken ct);
        Task MarkRejectedAsync(Guid nonce, string reason, CancellationToken ct);
    }

    public sealed class PendingInvitation
    {
        public Guid Nonce { get; }
        public OperatorId IssuedBy { get; }
        public DateTime IssuedAt { get; }
        public DateTime ExpiresAt { get; }
        public ConnectionInvitation TransportInvitation { get; }
        public PendingInvitationStatus Status { get; private set; }
        public string RejectionReason { get; private set; }

        public PendingInvitation(
            Guid nonce,
            OperatorId issuedBy,
            DateTime issuedAt,
            DateTime expiresAt,
            ConnectionInvitation transportInvitation)
        {
            if (nonce == Guid.Empty) throw new ArgumentException("Nonce cannot be empty", nameof(nonce));
            if (transportInvitation == null) throw new ArgumentNullException(nameof(transportInvitation));
            if (expiresAt <= issuedAt) throw new ArgumentException("ExpiresAt must be after IssuedAt", nameof(expiresAt));

            Nonce = nonce;
            IssuedBy = issuedBy;
            IssuedAt = issuedAt;
            ExpiresAt = expiresAt;
            TransportInvitation = transportInvitation;
            Status = PendingInvitationStatus.Pending;
            RejectionReason = string.Empty;
        }

        public bool IsExpired(DateTime now) => now >= ExpiresAt;

        public void MarkConsumed()
        {
            if (Status != PendingInvitationStatus.Pending)
                throw new InvalidOperationException($"Cannot consume invitation in status {Status}");
            Status = PendingInvitationStatus.Consumed;
        }

        public void MarkRejected(string reason)
        {
            if (Status != PendingInvitationStatus.Pending)
                throw new InvalidOperationException($"Cannot reject invitation in status {Status}");
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));
            Status = PendingInvitationStatus.Rejected;
            RejectionReason = reason;
        }
    }

    public enum PendingInvitationStatus
    {
        Pending,
        Consumed,
        Rejected
    }
}
