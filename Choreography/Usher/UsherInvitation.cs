using System;
using Choreography.Transport;

namespace Choreography.Usher
{
    // What ContactSecret receives from Usher.IssueInvitationAsync. It carries the transport's
    // ConnectionInvitation (Address is what goes inside the QR) plus the nonce that Stage must
    // echo back in its JoinRequest so the Usher can correlate.
    //
    // The nonce travels embedded in the QR (through the "data" field of the simplex
    // share-link when the transport is SimpleX, or in a query string for
    // HttpsTransport). In the scaffold we expose it separately so the caller
    // can serialize it however best suits the physical medium (showing a plain code,
    // printing a QR, NFC, etc).
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
