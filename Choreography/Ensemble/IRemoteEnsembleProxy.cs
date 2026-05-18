using System;
using System.Threading.Tasks;

namespace Choreography.Ensemble
{
    public interface IRemoteEnsembleProxy
    {
        Task<string> ForwardCommand(string actorId, string script, string ip, string user, DateTime now);
        Task<string> ForwardQuery(string actorId, string script);
        Task<long> GetCurrentEntryId(string actorId);
        Task NotifyEviction(string actorId);
    }
}
