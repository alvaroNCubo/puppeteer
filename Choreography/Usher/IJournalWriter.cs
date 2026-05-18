using System.Threading;
using System.Threading.Tasks;

namespace Choreography.Usher
{
    // Decision D2: Rubicon NO es peer del RSM, es membership authority. Este es el
    // canal privilegiado por el que el Usher inyecta MembershipRecord al journal
    // replicado.
    //
    // El handoff real ira contra el journal de un anchor peer (o un quorum-shim),
    // que se encarga de propagar el record al resto via los mecanismos normales de
    // replicacion. La interfaz solo necesita "appendear y devolver el epoch en que
    // quedo committed".
    public interface IJournalWriter
    {
        Task<long> AppendMembershipAsync(MembershipRecord record, CancellationToken ct);
    }
}
