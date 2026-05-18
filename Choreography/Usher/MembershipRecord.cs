using System;
using Choreography.StageManager;

namespace Choreography.Usher
{
    // Record que el Usher inyecta al journal cuando un nuevo Kora pasa la aprobacion.
    // Es la fuente de verdad de membresia: los peers existentes lo replican y a partir
    // de el saben que existe un Kora nuevo con esta pubkey, y emiten sus invitaciones
    // de peer-to-peer hacia el (Fase 6).
    //
    // IMPORTANTE (D5): el StagePublicKey va dentro porque los peers lo necesitan para
    // sellar PeerInvitationRecord. El Usher lo escribe aqui y descarta su copia
    // in-memory; despues del commit el Usher no conserva el pubkey. La "fuente" del
    // pubkey pasa a ser el journal replicado, no el Usher.
    public sealed class MembershipRecord
    {
        public PerformerId StageId { get; }
        public byte[] StagePublicKey { get; }
        public string DeviceName { get; }
        public OperatorId ApprovedBy { get; }
        public DateTime ApprovedAt { get; }

        public MembershipRecord(
            PerformerId stageId,
            byte[] stagePublicKey,
            string deviceName,
            OperatorId approvedBy,
            DateTime approvedAt)
        {
            if (stagePublicKey == null) throw new ArgumentNullException(nameof(stagePublicKey));
            if (stagePublicKey.Length != 32) throw new ArgumentException("StagePublicKey must be 32 bytes", nameof(stagePublicKey));
            if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentNullException(nameof(deviceName));

            StageId = stageId;
            StagePublicKey = (byte[])stagePublicKey.Clone();
            DeviceName = deviceName;
            ApprovedBy = approvedBy;
            ApprovedAt = approvedAt;
        }
    }
}
