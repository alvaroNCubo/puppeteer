using System;
using System.IO;
using Choreography.StageManager;

// ============================================================================
// Casting election protocol — implementation in progress.
//
// State by phase (origin: scaffold reserved in this file prior to the fix of
// bug 12, see commit 6d7f342 for the legacy "force-promote + tiebreaker" mode):
//
//   PHASE (a) Heartbeat loop — IMPLEMENTED (commit 73f5313).
//     Director-down detection without operator intervention. The Director emits
//     Heartbeat(senderId, currentEntryId) periodically; the Cast keeps a
//     lastSeenDirector dict and a DirectorWatchdog that fires OnDirectorLost
//     when DirectorTimeout is exceeded. On its own it does not recover — it
//     only detects.
//
//   PHASE (b) CastingPropose/Accept/Reject + Quorum — IN PROGRESS.
//     When OnDirectorLost fires, the Cast transitions to Candidate: it bumps
//     currentTerm (TermStore.cs persists it to {StageStateDir}/term.bin),
//     votes for itself and broadcasts CastingPropose with (Term,
//     ProposerEntryCount, ElectionId). Peers respond with CastingAccept
//     or CastingReject following Raft rules:
//       - propose.Term < myTerm  → CastingReject(myTerm).
//       - propose.Term > myTerm  → adopt term, reset votedFor, evaluate
//                                  EntryCount: accept if proposer >= mine.
//       - propose.Term == myTerm → reject if already voted in this term, else
//                                  same EntryCount check.
//     Quorum: floor(N/2)+1 including self. Declared exception for N=2
//     (2-peer Stage apps): weak quorum, self-vote suffices. For N>=3, strict.
//     On win: DirectorAnnounce with the new term. HandleDirectorAnnounce
//     evolved to term-first: higher term always wins; a term tie
//     falls back to the bug-12 tiebreaker (entries desc, performerId asc).
//
//   PHASE (d) Randomized backoff — PENDING.
//     After losing an election or timing out, wait Random(0..CastingElectionTimeout/2)
//     before retrying. Avoids livelock on split votes across 3+ peers.
//
// PromoteToDirector(force:true) — without bumping the term (decision signed
// 2026-05-27 for the end-user Stage environment). Force-promote remains a
// devops/bootstrap escape-hatch; the protocol is the source-of-truth via
// term-first, and a force-promote on the minority side will be silently
// demoted when the cluster reconverges with a higher term.
// ============================================================================

namespace Choreography.Transport
{
    public sealed class CastingPropose : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CastingPropose;

        // Proposer's term at the time of proposing. The voter compares it with its
        // own term: if proposer.Term > voter.Term, the voter adopts the higher term
        // (reset of votedFor) before evaluating; if lower, immediate reject.
        public long Term { get; private set; }

        // Highest journal EntryId of the proposer at the time of proposing.
        // The voter only accepts if proposer.EntryCount >= voter.EntryCount — a guarantee
        // that the elected Director will not lose committed entries (Raft completeness).
        public long ProposerEntryCount { get; private set; }

        // Identifier of the election round. The Candidate generates a new one when
        // it starts; it includes it in every CastingPropose; the Accept/Reject return
        // it so the Candidate can correlate them with its current round (avoids counting
        // votes from previous rounds of the same term).
        public Guid ElectionId { get; private set; }

        public CastingPropose(PerformerId senderId, long term, long proposerEntryCount, Guid electionId) : base(senderId)
        {
            Term = term;
            ProposerEntryCount = proposerEntryCount;
            ElectionId = electionId;
        }

        internal CastingPropose(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(Term);
            writer.Write(ProposerEntryCount);
            writer.Write(ElectionId.ToByteArray());
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            Term = reader.ReadInt64();
            ProposerEntryCount = reader.ReadInt64();
            ElectionId = new Guid(reader.ReadBytes(16));
        }
    }

    public sealed class CastingAccept : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CastingAccept;

        // Voter's term at the time of the vote (==proposer.Term after adopt).
        // The Candidate discards accepts with term != its current term (votes from
        // previous rounds become stale when the Candidate advances its term).
        public long Term { get; private set; }
        public Guid ElectionId { get; private set; }

        public CastingAccept(PerformerId senderId, long term, Guid electionId) : base(senderId)
        {
            Term = term;
            ElectionId = electionId;
        }

        internal CastingAccept(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(Term);
            writer.Write(ElectionId.ToByteArray());
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            Term = reader.ReadInt64();
            ElectionId = new Guid(reader.ReadBytes(16));
        }
    }

    public sealed class CastingReject : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CastingReject;

        // Voter's term when rejecting. If voter.Term > proposer.Term, the proposer
        // learns from the reject that it is behind and adopts the higher term,
        // transitioning to Follower. If voter.Term == proposer.Term the reject indicates
        // "already voted for another" or "your entries are insufficient".
        public long Term { get; private set; }
        public Guid ElectionId { get; private set; }

        public CastingReject(PerformerId senderId, long term, Guid electionId) : base(senderId)
        {
            Term = term;
            ElectionId = electionId;
        }

        internal CastingReject(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(Term);
            writer.Write(ElectionId.ToByteArray());
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            Term = reader.ReadInt64();
            ElectionId = new Guid(reader.ReadBytes(16));
        }
    }

    public sealed class DirectorAnnounce : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.DirectorAnnounce;

        public PerformerId DirectorId { get; private set; }

        // MaxEntryId of the announcing Director at the time of the announce. The receiver
        // uses it as a deterministic tiebreaker when it detects a
        // Director-vs-Director conflict with THE SAME term (bug 12 case: both peers in
        // IsDirector state after a concurrent force-promote). Whoever has more
        // journaled entries wins; a tie is broken by PerformerId.CompareTo.
        // When the terms differ, the term always wins (term-first) and this
        // MaxEntryId is only informational.
        public long MaxEntryId { get; private set; }

        // Term of the announcing Director. Phase b: primary comparison in
        // HandleDirectorAnnounce. announce.Term > myTerm → adopt term and peer
        // as Director; announce.Term < myTerm → ignore (stale); equal term →
        // MaxEntryId tiebreaker. Legacy Stages (pre-phase-b) use term=0 — a
        // peer that later runs a legitimate election to term>=1 will replace them
        // silently when it announces.
        public long Term { get; private set; }

        public DirectorAnnounce(PerformerId senderId, PerformerId directorId, long maxEntryId, long term) : base(senderId)
        {
            DirectorId = directorId;
            MaxEntryId = maxEntryId;
            Term = term;
        }

        internal DirectorAnnounce(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(DirectorId.ToBytes());
            writer.Write(MaxEntryId);
            writer.Write(Term);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            DirectorId = PerformerId.From(reader.ReadBytes(16));
            MaxEntryId = reader.ReadInt64();
            Term = reader.ReadInt64();
        }
    }

    public sealed class Heartbeat : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.Heartbeat;

        public long DirectorMaxEntryId { get; private set; }

        public Heartbeat(PerformerId senderId, long directorMaxEntryId) : base(senderId)
        {
            DirectorMaxEntryId = directorMaxEntryId;
        }

        internal Heartbeat(PerformerId senderId, DateTime ts) : base(senderId, ts) { }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(DirectorMaxEntryId);
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            DirectorMaxEntryId = reader.ReadInt64();
        }
    }
}
