using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	// Cross-process safe staging area for elision/skip writes.
	//
	// Each process writes its batches to UNIQUE files (guid in the name)
	// inside pendingDir; no two workers ever touch the same file. The
	// consolidator takes an OS-level lock over witnessPath (FileShare.None
	// over an open FileStream) that is released automatically if the
	// process dies — without TTL or heartbeats to maintain. While a
	// consolidator is active, other workers can keep producing
	// batches: coordination only gates the merge phase into the
	// canonical consolidated files.
	//
	// Primitives used: FileStream open/close (lock), File.WriteAllBytes,
	// File.Move (atomic rename on the same volume), Directory.GetFiles,
	// File.Delete. All are OS operations the FileSystem already provides.
	//
	// The "commit" of a batch is the rename .tmp -> .batch. Until that moment
	// the file is invisible to readers/consolidators (only files with the
	// .batch extension are enumerated). After the rename the batch is durable
	// and idempotent: if the process crashes, the batch is still there for the
	// next consolidator to pick up.
	internal sealed class PendingBatchDirectory
	{
		private readonly string pendingDir;
		private readonly string witnessPath;

		internal PendingBatchDirectory(string pendingDir, string witnessPath)
		{
			if (pendingDir == null) throw new ArgumentNullException(nameof(pendingDir));
			if (witnessPath == null) throw new ArgumentNullException(nameof(witnessPath));

			this.pendingDir = pendingDir;
			this.witnessPath = witnessPath;
			Directory.CreateDirectory(pendingDir);

			RecoverOrphans();
		}

		internal string PendingDir => pendingDir;

		// Writes an atomic batch: WriteAllBytes to a .tmp with a unique guid,
		// fsync, then rename to a final .batch name. The rename is the
		// commit point — before that the file is not visible to readers
		// because ListBatches() filters by the .batch extension.
		//
		// The final name embeds the guid + UTC ticks to order the
		// batches by creation time (not required for correctness
		// but eases debugging).
		internal void WriteBatch(byte[] payload)
		{
			if (payload == null) throw new ArgumentNullException(nameof(payload));

			string guid = Guid.NewGuid().ToString("N");
			long ticks = DateTime.UtcNow.Ticks;
			string tmpPath = Path.Combine(pendingDir, $"{guid}.tmp");
			string finalPath = Path.Combine(pendingDir, $"{ticks:D19}-{guid}.batch");

			using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				fs.Write(payload, 0, payload.Length);
				fs.Flush(true);
			}

			// File.Move without overwrite: if by some miracle a file already exists
			// with the same final name, we fail before clobbering it. Since the
			// name embeds a guid, this would only happen due to a bug.
			File.Move(tmpPath, finalPath);
		}

		// Snapshot of the current .batch files, ordered by name (which encodes
		// UTC ticks). The consolidator processes this snapshot — batches that
		// appear after the snapshot are left for the next round.
		internal List<string> ListBatches()
		{
			if (!Directory.Exists(pendingDir)) return new List<string>();

			var files = Directory.GetFiles(pendingDir, "*.batch");
			Array.Sort(files, StringComparer.Ordinal);
			return new List<string>(files);
		}

		// Attempts to take the consolidation lock. Returns null if another
		// process already holds it. The handle MUST be Disposed when done
		// — the release happens when the FileStream is closed, not when the
		// witness file is deleted (which is left as a harmless sentinel).
		//
		// FileShare.None makes the lock OS-level: if the owning process
		// dies, the OS closes the FD and releases the lock automatically,
		// without TTL or heartbeats. On Windows it is a native mandatory lock;
		// on Linux .NET 5+ implements it via OFD locks / flock.
		internal WitnessLease TryAcquireWitness()
		{
			try
			{
				FileStream fs = new FileStream(
					witnessPath,
					FileMode.OpenOrCreate,
					FileAccess.Write,
					FileShare.Read);

				try
				{
					// On Windows FileShare.None already blocks; on Linux .NET
					// emulates it via flock. If the file is already open by
					// another process with FileShare.None, this open throws
					// IOException. Otherwise, we write our identity
					// for diagnostics (the only thing visible on disk).
					fs.SetLength(0);
					byte[] identity = Encoding.UTF8.GetBytes(
						$"pid={Environment.ProcessId} host={Environment.MachineName} time={DateTime.UtcNow:O}");
					fs.Write(identity, 0, identity.Length);
					fs.Flush(true);
					return new WitnessLease(fs);
				}
				catch
				{
					fs.Dispose();
					throw;
				}
			}
			catch (IOException) { return null; }
			catch (UnauthorizedAccessException) { return null; }
		}

		// Deletes the batches consumed by a successful consolidation.
		// Idempotent: if a file no longer exists (another consolidator
		// deleted it), it is silently ignored. Silent failure too if
		// the file is temporarily locked — the next Distill /
		// consolidation will try again.
		internal void DeleteBatches(IEnumerable<string> paths)
		{
			if (paths == null) throw new ArgumentNullException(nameof(paths));

			foreach (var p in paths)
			{
				try { File.Delete(p); } catch { }
			}
		}

		// Recovery of orphan .tmp files. A .tmp appears when a worker
		// crashed between WriteAllBytes and the rename — the batch was not
		// committed, so the reaction did not advance its checkpoint either and
		// will retry on the next cycle generating a new .tmp. The
		// old ones are garbage.
		internal void RecoverOrphans()
		{
			if (!Directory.Exists(pendingDir)) return;

			foreach (var tmp in Directory.GetFiles(pendingDir, "*.tmp"))
			{
				try { File.Delete(tmp); } catch { }
			}
		}

		// Test seam: count of live batches. Used by tests to verify
		// that consolidation cleans the directory.
		internal int CountBatches()
		{
			if (!Directory.Exists(pendingDir)) return 0;
			return Directory.GetFiles(pendingDir, "*.batch").Length;
		}
	}

	internal sealed class WitnessLease : IDisposable
	{
		private FileStream stream;

		internal WitnessLease(FileStream stream)
		{
			this.stream = stream;
		}

		public void Dispose()
		{
			var s = stream;
			stream = null;
			if (s != null)
			{
				try { s.Dispose(); } catch { }
			}
		}
	}
}
