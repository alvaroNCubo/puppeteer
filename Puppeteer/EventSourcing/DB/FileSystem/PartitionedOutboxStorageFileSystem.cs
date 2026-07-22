using System;
using System.Collections.Generic;
using System.IO;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	// Per-destination partitioned outbox: one OutboxStorageFileSystem file per
	// logical destination under a single outbox/ directory, so rows for different
	// outputTargets never share a file. Two `.Outbox.Emit(destination, ...)`
	// destinations produce two files, e.g.
	//
	//   <basePath>/outbox/outbox-Kafka.bin
	//   <basePath>/outbox/outbox-MySql.bin
	//
	// Routing is by OutboxRecord.Destination. Per the framework policy "validate, do
	// not transform" (see StorageActorName), the destination is used verbatim as the
	// file-name segment and must be a legal one; an illegal destination is rejected
	// rather than silently mapped to a surrogate that could collapse two distinct
	// destinations onto one file.
	//
	// OutboxId is unique only WITHIN a partition (each per-destination file numbers
	// its own rows from 1). The relay marks delivery by row (MarkDelivered overload)
	// so it routes to the right partition; the global message identity for dedup is
	// the IdempotencyKey, which is unique across partitions by construction.
	internal sealed class PartitionedOutboxStorageFileSystem : OutboxStorage
	{
		private const string FilePrefix = "outbox-";
		private const string FileSuffix = ".bin";
		private const int MaxDestinationLength = 100;

		private static readonly string[] ReservedDeviceNames =
		{
			"CON", "PRN", "AUX", "NUL",
			"COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
			"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
		};

		private readonly string outboxDir;
		private readonly IAtomicFileOperation atomicOp;
		private readonly object storeLock = new();
		private readonly Dictionary<string, OutboxStorageFileSystem> byDestination =
			new Dictionary<string, OutboxStorageFileSystem>(StringComparer.Ordinal);

		internal PartitionedOutboxStorageFileSystem(string outboxDir, IAtomicFileOperation atomicOp)
		{
			ArgumentNullException.ThrowIfNull(outboxDir);
			ArgumentNullException.ThrowIfNull(atomicOp);

			this.outboxDir = outboxDir;
			this.atomicOp = atomicOp;

			Load();
		}

		// Rebuild the destination -> partition map from the files already on disk. The
		// destination round-trips from the file name because it was validated to a
		// legal segment when the partition was created (verbatim, no transform).
		private void Load()
		{
			if (!Directory.Exists(outboxDir)) return;

			foreach (string path in Directory.GetFiles(outboxDir, FilePrefix + "*" + FileSuffix))
			{
				string fileName = Path.GetFileName(path);
				string destination = fileName.Substring(
					FilePrefix.Length, fileName.Length - FilePrefix.Length - FileSuffix.Length);
				if (destination.Length == 0) continue;

				byDestination[destination] = new OutboxStorageFileSystem(path, atomicOp);
			}
		}

		private OutboxStorageFileSystem GetOrCreatePartition(string destination)
		{
			if (byDestination.TryGetValue(destination, out var existing))
				return existing;

			ValidateDestination(destination);
			string path = Path.Combine(outboxDir, FilePrefix + destination + FileSuffix);
			var store = new OutboxStorageFileSystem(path, atomicOp);
			byDestination[destination] = store;
			return store;
		}

		internal override bool TryInsert(OutboxRecord record)
		{
			ArgumentNullException.ThrowIfNull(record);

			lock (storeLock)
				return GetOrCreatePartition(record.Destination).TryInsert(record);
		}

		internal override void ReadUndelivered(List<OutboxRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();

			lock (storeLock)
			{
				var buffer = new List<OutboxRecord>();
				foreach (var store in byDestination.Values)
				{
					store.ReadUndelivered(buffer);
					result.AddRange(buffer);
				}
			}

			// Deterministic aggregate order. OutboxId collides across partitions, so
			// break ties by destination to keep the order stable.
			result.Sort((a, b) =>
			{
				int byId = a.OutboxId.CompareTo(b.OutboxId);
				return byId != 0 ? byId : string.CompareOrdinal(a.Destination, b.Destination);
			});
		}

		internal override void ReadUndelivered(string destination, List<OutboxRecord> result)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(destination);
			ArgumentNullException.ThrowIfNull(result);
			result.Clear();

			lock (storeLock)
			{
				if (byDestination.TryGetValue(destination, out var store))
					store.ReadUndelivered(result);
			}
		}

		internal override bool MarkDelivered(OutboxRecord record, DateTime deliveredAt)
		{
			ArgumentNullException.ThrowIfNull(record);

			lock (storeLock)
			{
				return byDestination.TryGetValue(record.Destination, out var store)
					&& store.MarkDelivered(record.OutboxId, deliveredAt);
			}
		}

		// Destination-less fallback for the base contract. OutboxId is unique only
		// within a partition, so scan; the relay uses MarkDelivered(OutboxRecord, ...)
		// which routes directly.
		internal override bool MarkDelivered(long outboxId, DateTime deliveredAt)
		{
			lock (storeLock)
			{
				foreach (var store in byDestination.Values)
					if (store.MarkDelivered(outboxId, deliveredAt))
						return true;
				return false;
			}
		}

		internal override bool IsRecorded(string idempotencyKey)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

			lock (storeLock)
			{
				foreach (var store in byDestination.Values)
					if (store.IsRecorded(idempotencyKey))
						return true;
				return false;
			}
		}

		internal override int PendingCount
		{
			get
			{
				lock (storeLock)
				{
					int n = 0;
					foreach (var store in byDestination.Values)
						n += store.PendingCount;
					return n;
				}
			}
		}

		// The destination doubles as the outbox partition file-name segment, so it
		// must be a legal one on any OS. Validate, never transform (a surrogate would
		// have to be injective and stable forever, and a naive rewrite could collapse
		// two destinations onto one file — the very mixing this partitioning prevents).
		private static void ValidateDestination(string destination)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(destination);

			foreach (char c in destination)
			{
				if (c < 32 || c == '/' || c == '\\' || c == ':' || c == '*' ||
					c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
				{
					throw new LanguageException(
						$"Outbox destination '{destination}' is not a valid outbox partition name. It is used " +
						"as a per-destination file name, so it may not contain path separators, any of " +
						": * ? \" < > | , or control characters.");
				}
			}

			if (destination != destination.Trim() || destination.EndsWith(".", StringComparison.Ordinal))
				throw new LanguageException(
					$"Outbox destination '{destination}' is not a valid outbox partition name: a file name may " +
					"not have leading/trailing whitespace or a trailing dot.");

			if (destination == "." || destination == "..")
				throw new LanguageException($"Outbox destination '{destination}' is not a valid outbox partition name.");

			foreach (string reserved in ReservedDeviceNames)
			{
				if (string.Equals(destination, reserved, StringComparison.OrdinalIgnoreCase))
					throw new LanguageException(
						$"Outbox destination '{destination}' is a reserved device name and can not be used as an outbox partition file.");
			}

			if (destination.Length > MaxDestinationLength)
				throw new LanguageException(
					$"Outbox destination '{destination}' is too long for an outbox partition file name " +
					$"({destination.Length} chars); max {MaxDestinationLength}.");
		}
	}
}
