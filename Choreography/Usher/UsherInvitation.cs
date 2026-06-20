using System;
using Choreography.Transport;

namespace Choreography.Usher
{
    // Lo que ContactSecret recibe de Usher.IssueInvitationAsync. Lleva el ConnectionInvitation
    // del transporte (Address es lo que va dentro del QR) mas el nonce que Stage debe
    // echar de vuelta en su JoinRequest para que el Usher pueda correlacionar.
    //
    // El nonce viaja embebido en el QR (a traves del campo "data" del simplex
    // share-link cuando el transporte es SimpleX, o en una query string para
    // HttpsTransport). En el scaffold lo expuestamos por separado para que el caller
    // lo serialice como mejor le sirva al medio fisico (mostrar codigo plano,
    // imprimir QR, NFC, etc).
    public sealed class UsherInvitation
    {
        public Guid Nonce { get; }
        public ConnectionInvitation TransportInvitation { get; }
        public DateTime ExpiresAt { get; }

        public UsherInvitation(Guid nonce, ConnectionInvitation transportInvitation, DateTime expiresAt)
        {
            if (nonce == Guid.Empty) throw new ArgumentException("Nonce cannot be empty", nameof(nonce));
            if (transportInvitation == null) throw new ArgumentNullException(nameof(transportInvitation));
            if (transportInvitation.Purpose != ChannelPurpose.Usher)
                throw new ArgumentException($"Invitation purpose must be Usher, got {transportInvitation.Purpose}", nameof(transportInvitation));

            Nonce = nonce;
            TransportInvitation = transportInvitation;
            ExpiresAt = expiresAt;
        }
    }
}
