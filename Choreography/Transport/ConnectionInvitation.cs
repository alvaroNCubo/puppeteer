using System;
using Choreography.StageManager;

namespace Choreography.Transport
{
    public sealed class ConnectionInvitation
    {
        public PerformerId InviterId { get; }
        public ChannelPurpose Purpose { get; }
        public string Address { get; }

        public ConnectionInvitation(PerformerId inviterId, ChannelPurpose purpose, string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Address is required", nameof(address));
            InviterId = inviterId;
            Purpose = purpose;
            Address = address;
        }
    }
}
