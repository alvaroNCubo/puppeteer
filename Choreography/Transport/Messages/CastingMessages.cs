using System;
using System.IO;
using Choreography.StageManager;

// ============================================================================
// Casting election protocol — implementacion en progreso.
//
// Estado por fase (origen: scaffold reservado en este archivo previo al fix de
// bug 12, ver commit 6d7f342 para el modo legacy "force-promote + tiebreaker"):
//
//   FASE (a) Heartbeat loop — IMPLEMENTADO (commit 73f5313).
//     Director-down detection sin intervencion del operador. El Director emite
//     Heartbeat(senderId, currentEntryId) periodicamente; el Cast lleva un
//     lastSeenDirector dict y un DirectorWatchdog que dispara OnDirectorLost
//     cuando supera DirectorTimeout. Por si solo no recupera — solo detecta.
//
//   FASE (b) CastingPropose/Accept/Reject + Quorum — EN PROGRESO.
//     Cuando OnDirectorLost dispara, el Cast pasa a Candidate: bumpea
//     currentTerm (TermStore.cs lo persiste a {StageStateDir}/term.bin),
//     vota por si mismo y broadcastea CastingPropose con (Term,
//     ProposerEntryCount, ElectionId). Peers responden con CastingAccept
//     o CastingReject segun reglas Raft:
//       - propose.Term < myTerm  → CastingReject(myTerm).
//       - propose.Term > myTerm  → adopt term, reset votedFor, evaluar
//                                  EntryCount: accept si proposer >= mio.
//       - propose.Term == myTerm → reject si ya vote en este term, sino
//                                  mismo check de EntryCount.
//     Quorum: floor(N/2)+1 incluyendo self. Excepcion declarada para N=2
//     (apps Stage 2-peer): weak quorum, self-vote alcanza. Para N>=3, strict.
//     On win: DirectorAnnounce con term nuevo. HandleDirectorAnnounce
//     evolucionado a term-first: term mayor gana siempre; empate de term
//     cae al tiebreaker bug-12 (entries desc, performerId asc).
//
//   FASE (d) Randomized backoff — PENDIENTE.
//     Tras perder eleccion o timeout, esperar Random(0..CastingElectionTimeout/2)
//     antes del retry. Evita livelock en split votes 3+ peers.
//
// PromoteToDirector(force:true) — sin bumpeo de term (decision firmada
// 2026-05-27 para entorno Stage end-user). Force-promote queda como escape-hatch
// devops/bootstrap; el protocolo es source-of-truth via term-first, y un
// force-promote en lado minoritario sera demoted silenciosamente cuando el
// cluster reconverja con un term superior.
// ============================================================================

namespace Choreography.Transport
{
    public sealed class CastingPropose : StageMessage
    {
        public override StageMessageType MessageType => StageMessageType.CastingPropose;

        // Term del proposer en el momento de proponer. Voter lo compara con su
        // propio term: si proposer.Term > voter.Term, voter adopta el term superior
        // (reset de votedFor) antes de evaluar; si menor, reject inmediato.
        public long Term { get; private set; }

        // EntryId mas alto del journal del proposer en el momento de proponer.
        // Voter solo acepta si proposer.EntryCount >= voter.EntryCount — garantia
        // de que el Director electo no perdera entries committed (Raft completeness).
        public long ProposerEntryCount { get; private set; }

        // Identificador del round de eleccion. El Candidate genera uno nuevo al
        // empezar; lo incluye en cada CastingPropose; los Accept/Reject lo devuelven
        // para que el Candidate los correlacione con su round actual (evita contar
        // votos de rounds previos del mismo term).
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

        // Term del voter en el momento del voto (==proposer.Term despues de adopt).
        // El Candidate descarta accepts con term != su term actual (votos de
        // rounds previos quedan obsoletos cuando el Candidate avanza de term).
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

        // Term del voter al rechazar. Si voter.Term > proposer.Term, el proposer
        // aprende del reject que esta atrasado y adopta el term superior pasando
        // a Follower. Si voter.Term == proposer.Term el reject indica "ya vote por
        // otro" o "tus entries son insuficientes".
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

        // MaxEntryId del Director-anunciante al momento del announce. El receiver
        // lo usa como tiebreaker determinista cuando detecta conflicto
        // Director-vs-Director con EL MISMO term (caso bug 12: ambos peers en
        // estado IsDirector tras force-promote concurrente). Quien tiene mas
        // entries journaled gana; empate se rompe por PerformerId.CompareTo.
        // Cuando los terms difieren, el term gana siempre (term-first) y este
        // MaxEntryId solo es informativo.
        public long MaxEntryId { get; private set; }

        // Term del Director anunciante. Fase b: comparacion primaria en
        // HandleDirectorAnnounce. announce.Term > myTerm → adopto term y peer
        // como Director; announce.Term < myTerm → ignoro (stale); igual term →
        // tiebreaker MaxEntryId. Stages legacy (pre-fase-b) usan term=0 — un
        // peer que despues corra eleccion legitima a term>=1 los reemplazara
        // silenciosamente cuando announce.
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
