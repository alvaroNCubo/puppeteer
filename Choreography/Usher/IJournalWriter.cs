using System.Threading;
using System.Threading.Tasks;

namespace Choreography.Usher
{
    // Decision D2: ContactSecret is NOT an RSM peer, it is a membership authority. This is the
    // privileged channel through which the Usher injects MembershipRecord into the replicated
    // journal.
    //
    // The real handoff will go against the journal of an anchor peer (or a quorum-shim),
    // which is responsible for propagating the record to the rest via the normal
    // replication mechanisms. The interface only needs to "append and return the epoch in which
    // it was committed".
    public interface IJournalWriter
    {
        Task<long> AppendMembershipAsync(MembershipRecord record, CancellationToken ct);
    }
}
