using System;
using System.IO;

namespace Choreography.StageManager
{
    // Casting election protocol fase (b) — Persistencia de currentTerm + votedFor.
    //
    // Por que en un archivo dedicado y no en el journal del actor:
    //   - El term y el voto son state OPERACIONAL del Stage (eleccion, membership),
    //     no business-data del actor. Mezclarlos en el journal acoplaria semanticamente
    //     dos planos que el resto del sistema ya mantiene separados (mismo argumento
    //     que motivo separar PlaybillStore del journal en Paper 5).
    //   - Son 25 bytes, write infrequente (solo en bump de term o cambio de voto —
    //     ordenes de magnitud menos frecuente que journal appends). Un archivo
    //     dedicado es mas barato y mas simple que pagar el costo de un nuevo
    //     record type en el journal.
    //
    // Atomicidad: write-temp + rename atomico. En NTFS (Windows) y ext4 (Linux) el
    // rename es atomico a nivel filesystem; la unica brecha es fsync del directorio
    // ante power-loss, brecha que tampoco cubre el FileSystem Journal hoy. Si en el
    // futuro el journal layer upgrada a fsync robusto, TermStore replica el mismo
    // patron sin cambiar el on-wire del protocolo de eleccion.
    //
    // Layout disco (25 bytes, little-endian):
    //   [00..07] currentTerm: int64
    //   [08..08] hasVotedFor: byte (0 o 1)
    //   [09..24] votedForBytes: 16 bytes (PerformerId, ceros si !hasVotedFor)
    //
    // Sin version header: si cambia el formato, bumpear el path (term.bin → term-v2.bin).
    //
    // Thread-safety: todas las operaciones bajo lock. El Stage llama a este store
    // desde varios listeners/coroutines (ListenCoordination, watchdog timeout
    // disparando StartElection, HandleDirectorAnnounce adoptando term superior).
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

        // Avanza al nuevo term y resetea votedFor (Raft estandar: cada term es un
        // ciclo de votacion fresco). Rechaza newTerm <= currentTerm para preservar
        // la monotonicidad — un term que retrocede romperia las garantias de
        // safety del protocolo.
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

        // Adopta un term observado en mensajeria de un peer (CastingPropose,
        // CastingReject o DirectorAnnounce con term mayor). Idempotente si
        // observedTerm == currentTerm (no-op, votedFor preservado); avanza
        // y resetea votedFor si observedTerm > currentTerm; rechaza si menor
        // (ese caller venia con term stale, ignorarlo).
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

        // Registra un voto para el current term. Rechaza si ya hay un voto
        // en este term (one-vote-per-term: invariante critica de Raft safety).
        // Para votar en un nuevo term, primero invocar BumpTerm o AdoptTermIfHigher.
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
