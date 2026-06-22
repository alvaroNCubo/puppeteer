using System;

namespace Choreography.StageManager
{
    // Payload of the Stage.OnDirectorElectionLost event. Explicit surface of the
    // split-brain detected after an election: this Stage was acting as Director
    // when it received a DirectorAnnounce from another peer that also declared itself
    // Director, and the tiebreaker (MaxEntryId desc, PerformerId asc) leaves it
    // on the loser side.
    //
    // HasDivergentTail==true means that MyMaxEntryId > 0 and this Stage wrote
    // entries that the winner does NOT have in its journal (the winner won with fewer or
    // equal entries, which implies that the loser has its own tail). Lossless
    // reconciliation would require a CRDT merge or app-defined policy; without a
    // truncate primitive in the journal, rehydration-from-winner
    // would lose that tail. The decision of how to reconcile is left to the application.
    //
    // HasDivergentTail==false means that MyMaxEntryId <= WinnerMaxEntryId and
    // can potentially be reconciled via SendCatchUpAsync from the winner
    // (if the prefixes match — which is not validated here, left to the app).
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
