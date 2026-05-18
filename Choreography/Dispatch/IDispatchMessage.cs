namespace Choreography.Dispatch
{
    public interface IDispatchMessage
    {
        static abstract int TypeId { get; }
        static abstract IDispatchMessage Deserialize(string raw);
    }
}
