using System;
using System.IO;

namespace Choreography.StageManager
{
    // Casting election protocol phase (b) — Persistence of currentTerm + votedFor.
    //
    // Why in a dedicated file and not in the actor journal:
    //   - The term and the vote are OPERATIONAL state of the Stage (election, membership),
    //     not business-data of the actor. Mixing them into the journal would semantically
    //     couple two planes that the rest of the system already keeps separate (same argument
    //     that motivated separating PlaybillStore from the journal in Paper 5).
    //   - They are 25 bytes, written infrequently (only on term bump or vote change —
    //     orders of magnitude less frequent than journal appends). A dedicated file
    //     is cheaper and simpler than paying the cost of a new
    //     record type in the journal.
    //
    // Atomicity: write-temp + atomic rename. On NTFS (Windows) and ext4 (Linux) the
    // rename is atomic at the filesystem level; the only gap is directory fsync
    // on power-loss, a gap that the FileSystem Journal does not cover today either. If in the
    // future the journal layer upgrades to robust fsync, TermStore replicates the same
    // pattern without changing the on-wire of the election protocol.
    //
    // Disk layout (25 bytes, little-endian):
    //   [00..07] currentTerm: int64
    //   [08..08] hasVotedFor: byte (0 or 1)
    //   [09..24] votedForBytes: 16 bytes (PerformerId, zeros if !hasVotedFor)
    //
    // No version header: if the format changes, bump the path (term.bin → term-v2.bin).
    //
    // Thread-safety: all operations under lock. The Stage calls this store
    // from several listeners/coroutines (ListenCoordination, watchdog timeout
    // triggering StartElection, HandleDirectorAnnounce adopting a higher term).
    internal sealed class TermStore
    {
        private const int FileSize = 25;
        private const string FileName = "term.bin";

        private readonly string filePath;
        private readonly object writeLock = new object();
        private long currentTerm;
        private PerformerId? votedFor;

        public TermStore(string stageStateDirectory)
        {
            if (stageStateDirectory == null) throw new ArgumentNullException(nameof(stageStateDirectory));
            this.filePath = Path.Combine(stageStateDirectory, FileName);
            Load();
        }

        public long CurrentTerm
        {
            get { lock (writeLock) return currentTerm; }
        }

        public PerformerId? VotedFor
        {
            get { lock (writeLock) return votedFor; }
        }

        // Advances to the new term and resets votedFor (standard Raft: each term is a
        // fresh voting cycle). Rejects newTerm <= currentTerm to preserve
        // monotonicity — a term that goes backwards would break the
        // safety guarantees of the protocol.
        public void BumpTerm(long newTerm)
        {
            lock (writeLock)
            {
                if (newTerm <= currentTerm)
                    throw new InvalidOperationException(
                        $"BumpTerm requires newTerm > currentTerm (currentTerm={currentTerm}, newTerm={newTerm}).");
                currentTerm = newTerm;
                votedFor = null;
                PersistAtomic();
            }
        }

        // Adopts a term observed in messaging from a peer (CastingPropose,
        // CastingReject or DirectorAnnounce with a higher term). Idempotent if
        // observedTerm == currentTerm (no-op, votedFor preserved); advances
        // and resets votedFor if observedTerm > currentTerm; rejects if lower
        // (that caller came with a stale term, ignore it).
        public void AdoptTermIfHigher(long observedTerm)
        {
            lock (writeLock)
            {
                if (observedTerm <= currentTerm) return;
                currentTerm = observedTerm;
                votedFor = null;
                PersistAtomic();
            }
        }

        // Records a vote for the current term. Rejects if there is already a vote
        // in this term (one-vote-per-term: critical Raft safety invariant).
        // To vote in a new term, first invoke BumpTerm or AdoptTermIfHigher.
        public void RecordVote(PerformerId candidateId)
        {
            lock (writeLock)
            {
                if (votedFor.HasValue)
                    throw new InvalidOperationException(
                        $"Already voted for {votedFor.Value} in term {currentTerm}.");
                votedFor = candidateId;
                PersistAtomic();
            }
        }

        private void Load()
        {
            lock (writeLock)
            {
                if (!File.Exists(filePath))
                {
                    currentTerm = 0;
                    votedFor = null;
                    return;
                }
                byte[] buffer = File.ReadAllBytes(filePath);
                if (buffer.Length != FileSize)
                    throw new InvalidOperationException(
                        $"Corrupt term.bin at {filePath}: expected {FileSize} bytes, got {buffer.Length}.");

                using var ms = new MemoryStream(buffer);
                using var reader = new BinaryReader(ms);
                currentTerm = reader.ReadInt64();
                byte hasVoted = reader.ReadByte();
                byte[] idBytes = reader.ReadBytes(16);
                votedFor = hasVoted == 0 ? (PerformerId?)null : PerformerId.From(idBytes);
            }
        }

        private void PersistAtomic()
        {
            using var ms = new MemoryStream(FileSize);
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(currentTerm);
                writer.Write((byte)(votedFor.HasValue ? 1 : 0));
                writer.Write(votedFor.HasValue ? votedFor.Value.ToBytes() : new byte[16]);
            }
            byte[] buffer = ms.ToArray();

            string tempPath = filePath + ".tmp";
            File.WriteAllBytes(tempPath, buffer);
            File.Move(tempPath, filePath, overwrite: true);
        }
    }
}
