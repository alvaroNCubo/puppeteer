using System;

namespace Choreography.StageManager
{
    internal sealed class StageConfiguration
    {
        public string StageStateDirectory { get; set; }

        // Las tres siguientes pertenecen al Casting election protocol scaffold —
        // no consumidas hoy. Ver Choreography/Transport/Messages/CastingMessages.cs
        // (comentario top-of-file) para el estado del protocolo y la etapa 2.
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan DirectorTimeout { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan CastingElectionTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan CommandForwardingTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int RehearsalChunkSizeBytes { get; set; } = 256 * 1024;

        public Func<string> GetUserPassword { get; set; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(StageStateDirectory))
                throw new ArgumentException("StageStateDirectory is required");
            if (HeartbeatInterval <= TimeSpan.Zero)
                throw new ArgumentException("HeartbeatInterval must be positive");
            if (DirectorTimeout <= HeartbeatInterval)
                throw new ArgumentException("DirectorTimeout must be greater than HeartbeatInterval");
            if (CastingElectionTimeout <= TimeSpan.Zero)
                throw new ArgumentException("CastingElectionTimeout must be positive");
            if (CommandForwardingTimeout <= TimeSpan.Zero)
                throw new ArgumentException("CommandForwardingTimeout must be positive");
            if (RehearsalChunkSizeBytes <= 0)
                throw new ArgumentException("RehearsalChunkSizeBytes must be positive");
        }
    }
}
