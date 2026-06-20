using System;

namespace Choreography.StageManager
{
    // Payload del evento Stage.OnDirectorElectionLost. Surface explicit del
    // split-brain detectado tras una eleccion: este Stage estaba como Director
    // cuando recibio un DirectorAnnounce de otro peer que tambien se declaraba
    // Director, y el tiebreaker (MaxEntryId desc, PerformerId asc) lo deja
    // del lado del loser.
    //
    // HasDivergentTail==true significa que MyMaxEntryId > 0 y este Stage escribio
    // entries que el winner NO tiene en su journal (el winner gano con menos o
    // igual entries, lo cual implica que el loser tiene tail propia). La
    // reconciliacion sin perdida requeriria CRDT merge o policy app-defined; sin
    // primitiva de truncate en el journal, la rehidratacion-desde-winner
    // perderia esa tail. La decision de como reconciliar queda en la aplicacion.
    //
    // HasDivergentTail==false significa que MyMaxEntryId <= WinnerMaxEntryId y
    // potencialmente puede reconciliarse via SendCatchUpAsync desde el winner
    // (si los prefijos coinciden — lo cual no se valida aqui, queda en la app).
    public sealed class SplitBrainDetected
    {
        public PerformerId Winner { get; }
        public long MyMaxEntryId { get; }
        public long WinnerMaxEntryId { get; }
        public bool HasDivergentTail => MyMaxEntryId > WinnerMaxEntryId;

        public SplitBrainDetected(PerformerId winner, long myMaxEntryId, long winnerMaxEntryId)
        {
            if (myMaxEntryId < 0) throw new ArgumentOutOfRangeException(nameof(myMaxEntryId));
            if (winnerMaxEntryId < 0) throw new ArgumentOutOfRangeException(nameof(winnerMaxEntryId));
            Winner = winner;
            MyMaxEntryId = myMaxEntryId;
            WinnerMaxEntryId = winnerMaxEntryId;
        }
    }
}
