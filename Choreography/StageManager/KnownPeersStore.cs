using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Choreography.StageManager
{
    // Bug 19 — Ex-Director sobreviviendo a process-death no se reincorpora.
    //
    // El re-handshake in-band (bug 18) presupone un canal de Coordination VIVO para
    // viajar (el caso "Director silencioso pero proceso vivo"). Cuando el proceso del
    // Stage muere (force-stop / OOM / crash / reboot del device) y vuelve, todos sus
    // channels — Coordination incluido — murieron con el. Al rehidratar desde el
    // journal, coordinationPeers arranca vacio: no llega ningun DirectorAnnounce, asi
    // que RequestRehandshakeIfRotated nunca se dispara y el nodo queda aislado.
    //
    // KnownPeersStore persiste la MEMBRESIA: los PerformerId de los peers con los que
    // este Stage ha establecido Coordination. Sobrevive al process-death en
    // StageStateDirectory/peers.bin (analogo a TermStore/term.bin). El host la consulta
    // al arrancar (Stage.RecallKnownPeers) para reabrir Coordination con cada peer
    // conocido — sin importar el rol previo del nodo, cerrando el caso del ex-Director.
    //
    // Por que solo IDs y no Address: el modelo de transporte es invitation-based; el
    // Stage recibe channels ya abiertos en JoinCoordination y NUNCA ve la Address de
    // reconexion. Las Address viven en el host (que crea/publica las invitaciones). El
    // Stage aporta a QUIENES reconectar; el host aporta el COMO. Esa division es la que
    // mantiene al Stage transport-agnostico.
    //
    // Por que en archivo dedicado y no en el journal: mismo argumento que TermStore —
    // es state OPERACIONAL del Stage (membership), no business-data del actor.
    //
    // Atomicidad: write-temp + rename atomico, igual que TermStore.
    //
    // Layout disco (little-endian):
    //   [00..03] count: int32
    //   luego count * 16 bytes: PerformerId (Guid) cada uno
    //
    // Sin version header: si cambia el formato, bumpear el path (peers.bin → peers-v2.bin).
    //
    // Thread-safety: todas las operaciones bajo lock. Remember se llama desde
    // JoinCoordination, que corre desde varios listeners/coroutines.
    internal sealed class KnownPeersStore
    {
        private const string FileName = "peers.bin";

        private readonly string filePath;
        private readonly object writeLock = new object();
        private readonly HashSet<PerformerId> peers = new HashSet<PerformerId>();

        public KnownPeersStore(string stageStateDirectory)
        {
            if (stageStateDirectory == null) throw new ArgumentNullException(nameof(stageStateDirectory));
            this.filePath = Path.Combine(stageStateDirectory, FileName);
            Load();
        }

        // Registra un peer conocido. Idempotente: si ya estaba, no reescribe el archivo
        // (en steady-state, cero I/O — JoinCoordination con peers ya conocidos no toca disco).
        public void Remember(PerformerId peer)
        {
            lock (writeLock)
            {
                if (!peers.Add(peer)) return;
                PersistAtomic();
            }
        }

        public IReadOnlyList<PerformerId> All
        {
            get { lock (writeLock) return peers.ToArray(); }
        }

        private void Load()
        {
            lock (writeLock)
            {
                if (!File.Exists(filePath)) return;

                byte[] buffer = File.ReadAllBytes(filePath);
                // Best-effort: un peers.bin truncado/corrupto (p.ej. crash a mitad de
                // un write previo a que existiera el temp+rename) no debe impedir el
                // arranque — el recall es una optimizacion, no un invariante de safety.
                if (buffer.Length < 4) return;

                using var ms = new MemoryStream(buffer);
                using var reader = new BinaryReader(ms);
                int count = reader.ReadInt32();
                if (count < 0 || 4L + (long)count * 16 != buffer.Length) return;

                for (int i = 0; i < count; i++)
                {
                    byte[] idBytes = reader.ReadBytes(16);
                    var g = new Guid(idBytes);
                    if (g != Guid.Empty) peers.Add(new PerformerId(g));
                }
            }
        }

        private void PersistAtomic()
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(peers.Count);
                foreach (var p in peers)
                    writer.Write(p.ToBytes());
            }
            byte[] buffer = ms.ToArray();

            string tempPath = filePath + ".tmp";
            File.WriteAllBytes(tempPath, buffer);
            File.Move(tempPath, filePath, overwrite: true);
        }
    }
}
