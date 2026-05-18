namespace Choreography.Transport
{
    public enum ChannelPurpose : byte
    {
        Coordination = 1,
        Replication = 2,
        Command = 3,
        Usher = 4
    }
}
