using System;

namespace Choreography.Usher
{
    // Human-readable identification of the Stage device that is requesting to join the network.
    // The operator sees it in ContactSecret to decide whether to approve it or not. It is not part of the
    // cryptographic identity (that is carried by StagePublicKey), only UX information
    // for the manual approval (D4).
    public sealed class DeviceProfile
    {
        public string Name { get; }
        public string Fingerprint { get; }

        public DeviceProfile(string name, string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentNullException(nameof(fingerprint));
            Name = name;
            Fingerprint = fingerprint;
        }
    }
}
