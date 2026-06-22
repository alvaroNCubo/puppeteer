using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Choreography.StageManager
{
    // Bug 19 — Ex-Director surviving process-death does not rejoin.
    //
    // The in-band re-handshake (bug 18) presupposes a LIVE Coordination channel to
    // travel over (the "silent Director but live process" case). When the Stage
    // process dies (force-stop / OOM / crash / device reboot) and comes back, all its
    // channels — Coordination included — died with it. On rehydrating from the
    // journal, coordinationPeers starts empty: no DirectorAnnounce arrives, so
    // RequestRehandshakeIfRotated never fires and the node stays isolated.
    //
    // KnownPeersStore persists the MEMBERSHIP: the PerformerId of the peers with which
    // this Stage has established Coordination. It survives process-death in
    // StageStateDirectory/peers.bin (analogous to TermStore/term.bin). The host queries it
    // on startup (Stage.RecallKnownPeers) to reopen Coordination with each known
    // peer — regardless of the node's previous role, closing the ex-Director case.
    //
    // Why only IDs and not Address: the transport model is invitation-based; the
    // Stage receives already-open channels in JoinCoordination and NEVER sees the
    // reconnection Address. Addresses live in the host (which creates/publishes the invitations). The
    // Stage contributes WHO to reconnect to; the host contributes HOW. That division is what
    // keeps the Stage transport-agnostic.
    //
    // Why in a dedicated file and not in the journal: same argument as TermStore —
    // it is OPERATIONAL state of the Stage (membership), not business-data of the actor.
    //
    // Atomicity: write-temp + atomic rename, same as TermStore.
    //
    // Disk layout (little-endian):
    //   [00..03] count: int32
    //   then count * 16 bytes: PerformerId (Guid) each
    //
    // No version header: if the format changes, bump the path (peers.bin → peers-v2.bin).
    //
    // Thread-safety: all operations under lock. Remember is called from
    // JoinCoordination, which runs from several listeners/coroutines.
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

        // Registers a known peer. Idempotent: if it was already present, it does not rewrite the file
        // (in steady-state, zero I/O — JoinCoordination with already-known peers does not touch disk).
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
                // Best-effort: a truncated/corrupt peers.bin (e.g. crash in the middle of
                // a write prior to the temp+rename existing) must not prevent
                // startup — the recall is an optimization, not a safety invariant.
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
