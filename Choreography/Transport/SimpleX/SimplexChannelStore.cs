using System;
using System.IO;
using System.Text;
using Choreography.StageManager;

namespace Choreography.Transport.SimpleX
{
    // Bug 19 (SMP) — Resume de un canal establecido tras process-death.
    //
    // El re-handshake in-band (bug 18) y el rejoin host-driven (RecallKnownPeers) presuponen
    // poder reabrir Coordination tras la muerte del proceso. En PortableHttps basta re-bindear
    // el listener; en SMP NO: una invitacion solo bootstrapea una vez (la queue queda KEY-secured
    // tras el primer handshake) y la recipient key vive solo en memoria (_pending). Re-hostear la
    // misma invitacion es imposible en SMP.
    //
    // La primitiva correcta para SMP es RESUME, no re-host: SMP es store-and-forward, asi que la
    // queue del canal sobrevive en el server con los mensajes que el peer publico mientras el nodo
    // estaba muerto. Si persistimos el estado COMPLETO de las dos queues del canal establecido
    // (outbound = donde enviamos, inbound = donde recibimos, con todas sus keys), al revivir
    // reconstruimos el SimplexChannel y re-SUBeamos el inbound — drenando lo encolado — SIN ningun
    // handshake y de forma UNILATERAL (el peer no hace nada).
    //
    // Este store persiste ese estado por (peerId, purpose) junto al StageStateDirectory, igual que
    // TermStore/term.bin y peers.bin. Atomico (temp + rename).
    //
    // NOTA DE SEGURIDAD: el archivo contiene las secret keys (sign + DH) de recepcion y envio del
    // canal. Es material tan sensible como el journal cifrado / el ContactSecret; vive en el mismo
    // StageStateDirectory bajo la misma frontera de confianza (el filesystem del device). Si el
    // journal se cifra at-rest, este store deberia heredar el mismo tratamiento.
    internal sealed class SimplexChannelStore
    {
        private const byte FormatVersion = 1;
        private const string SubdirName = "simplex-channels";

        private readonly string directory;
        private readonly object writeLock = new object();

        public SimplexChannelStore(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) throw new ArgumentNullException(nameof(baseDirectory));
            this.directory = Path.Combine(baseDirectory, SubdirName);
        }

        private string PathFor(PerformerId peer, ChannelPurpose purpose)
            => Path.Combine(directory, $"{peer.Value:N}-{(int)purpose}.smpch");

        // Persiste el estado de un canal establecido. outbound = queue donde este nodo ENVIA;
        // inbound = queue donde este nodo RECIBE. Idempotente: sobrescribe la entrada previa.
        public void Save(PerformerId peer, ChannelPurpose purpose, SmpQueue outbound, SmpQueue inbound)
        {
            if (outbound == null) throw new ArgumentNullException(nameof(outbound));
            if (inbound == null) throw new ArgumentNullException(nameof(inbound));

            lock (writeLock)
            {
                Directory.CreateDirectory(directory);
                using var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                {
                    w.Write(FormatVersion);
                    WriteQueue(w, outbound);
                    WriteQueue(w, inbound);
                }
                byte[] buffer = ms.ToArray();

                string filePath = PathFor(peer, purpose);
                string tempPath = filePath + ".tmp";
                File.WriteAllBytes(tempPath, buffer);
                File.Move(tempPath, filePath, overwrite: true);
            }
        }

        // Reconstruye (outbound, inbound) de un canal previamente persistido. Devuelve false si no
        // hay estado guardado (o esta corrupto: el resume es best-effort, no un invariante de safety).
        public bool TryLoad(PerformerId peer, ChannelPurpose purpose, out SmpQueue outbound, out SmpQueue inbound)
        {
            outbound = null;
            inbound = null;

            string filePath = PathFor(peer, purpose);
            byte[] buffer;
            lock (writeLock)
            {
                if (!File.Exists(filePath)) return false;
                buffer = File.ReadAllBytes(filePath);
            }

            try
            {
                using var ms = new MemoryStream(buffer);
                using var r = new BinaryReader(ms, Encoding.UTF8);
                byte version = r.ReadByte();
                if (version != FormatVersion) return false;
                outbound = ReadQueue(r);
                inbound = ReadQueue(r);
                return true;
            }
            catch
            {
                outbound = null;
                inbound = null;
                return false;
            }
        }

        private static void WriteQueue(BinaryWriter w, SmpQueue q)
        {
            w.Write(q.ServerHost);
            w.Write(q.ServerPort);
            WriteBytes(w, q.ServerFingerprint);
            WriteBytes(w, q.RecipientId);
            WriteBytes(w, q.SenderId);
            WriteBytes(w, q.RecipientSignPublicKey);
            WriteBytes(w, q.RecipientSignSecretKey);
            WriteBytes(w, q.RecipientDhPublicKey);
            WriteBytes(w, q.RecipientDhSecretKey);
            WriteBytes(w, q.SenderSignPublicKey);
            WriteBytes(w, q.SenderSignSecretKey);
            WriteBytes(w, q.SenderDhPublicKey);
            WriteBytes(w, q.SenderDhSecretKey);
            WriteBytes(w, q.ServerDhPublicKey);
            WriteBytes(w, q.PeerSenderDhPublicKey);
            w.Write((int)q.State);
            w.Write((int)q.Role);
        }

        private static SmpQueue ReadQueue(BinaryReader r)
        {
            string host = r.ReadString();
            int port = r.ReadInt32();
            var q = new SmpQueue(host, port)
            {
                ServerFingerprint = ReadBytes(r),
                RecipientId = ReadBytes(r),
                SenderId = ReadBytes(r),
                RecipientSignPublicKey = ReadBytes(r),
                RecipientSignSecretKey = ReadBytes(r),
                RecipientDhPublicKey = ReadBytes(r),
                RecipientDhSecretKey = ReadBytes(r),
                SenderSignPublicKey = ReadBytes(r),
                SenderSignSecretKey = ReadBytes(r),
                SenderDhPublicKey = ReadBytes(r),
                SenderDhSecretKey = ReadBytes(r),
                ServerDhPublicKey = ReadBytes(r),
                PeerSenderDhPublicKey = ReadBytes(r)
            };
            q.State = (SmpQueueState)r.ReadInt32();
            q.Role = (SmpQueueRole)r.ReadInt32();
            return q;
        }

        // byte[] anulable: [Int32 len] (-1 si null) + bytes.
        private static void WriteBytes(BinaryWriter w, byte[] value)
        {
            if (value == null) { w.Write(-1); return; }
            w.Write(value.Length);
            w.Write(value);
        }

        private static byte[] ReadBytes(BinaryReader r)
        {
            int len = r.ReadInt32();
            if (len < 0) return null;
            return r.ReadBytes(len);
        }
    }
}
