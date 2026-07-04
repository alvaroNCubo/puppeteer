using System;
using System.IO;
using System.Text;
using Choreography.StageManager;

namespace Choreography.Transport.SimpleX
{
    // Bug 19 (SMP) — Resume of an established channel after process-death.
    //
    // The in-band re-handshake (bug 18) and the host-driven rejoin (RecallKnownPeers) assume
    // Coordination can be reopened after process death. In PortableHttps it is enough to re-bind
    // the listener; in SMP it is NOT: an invitation bootstraps only once (the queue becomes
    // KEY-secured after the first handshake) and the recipient key lives only in memory (_pending).
    // Re-hosting the same invitation is impossible in SMP.
    //
    // The correct primitive for SMP is RESUME, not re-host: SMP is store-and-forward, so the
    // channel's queue survives on the server with the messages the peer published while the node
    // was dead. If we persist the COMPLETE state of the established channel's two queues
    // (outbound = where we send, inbound = where we receive, with all their keys), on revival we
    // rebuild the SimplexChannel and re-SUB the inbound — draining what was queued — WITHOUT any
    // handshake and UNILATERALLY (the peer does nothing).
    //
    // This store persists that state per (peerId, purpose) alongside the StageStateDirectory, just
    // like TermStore/term.bin and peers.bin. Atomic (temp + rename).
    //
    // SECURITY NOTE: the file contains the channel's secret keys (sign + DH) for both receiving and
    // sending. It is material as sensitive as the encrypted journal / the ContactSecret; it lives in
    // the same StageStateDirectory under the same trust boundary (the device filesystem). If the
    // journal is encrypted at-rest, this store should inherit the same treatment.
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

        // Persists the state of an established channel. outbound = queue where this node SENDS;
        // inbound = queue where this node RECEIVES. Idempotent: overwrites the previous entry.
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

        // Rebuilds (outbound, inbound) from a previously persisted channel. Returns false if there
        // is no saved state (or it is corrupt: resume is best-effort, not a safety invariant).
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

        // Nullable byte[]: [Int32 len] (-1 if null) + bytes.
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
