using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class MaterializationCheckpointStorageFileSystem : MaterializationCheckpointStorage
	{
		// "PPMC" = Puppeteer Materialization Checkpoint. Distinct from "PPMT"
		// (EventMaterialization v1 markers) so that a file mismatch is detectable.
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'M', (byte)'C' };
		private const ushort FORMAT_VERSION = 1;
		private const int HEADER_SIZE = 10;
		// Per record:
		//   ushort destLen
		//   bytes  dest (UTF-8)
		//   long   registeredAtEntryId
		//   long   lastConfirmedEntryId
		//   long   registeredAtTicks
		//   long   confirmedAtTicks

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;
		private readonly ReaderWriterLockSlim checkpointLock = new ReaderWriterLockSlim();

		private readonly Dictionary<string, MaterializationCheckpointRow> rowsByDestination
			= new Dictionary<string, MaterializationCheckpointRow>(StringComparer.Ordinal);

		internal MaterializationCheckpointStorageFileSystem(
			IActorEventJournalClient eventJournalClient,
			string connectionString,
			string filePath,
			IAtomicFileOperation atomicOp)
			: base(eventJournalClient, connectionString)
		{
			ArgumentNullException.ThrowIfNull(filePath);
			ArgumentNullException.ThrowIfNull(atomicOp);

			this.filePath = filePath;
			this.atomicOp = atomicOp;

			Load();
		}

		private void Load()
		{
			atomicOp.RecoverFromIncompleteOperation(filePath);

			if (!File.Exists(filePath)) return;

			byte[] data = File.ReadAllBytes(filePath);
			if (data.Length < HEADER_SIZE) return;

			if (data[0] != MAGIC[0] || data[1] != MAGIC[1] || data[2] != MAGIC[2] || data[3] != MAGIC[3])
				return;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;

			for (int i = 0; i < count && offset + 2 <= data.Length; i++)
			{
				ushort destLen = BitConverter.ToUInt16(data, offset); offset += 2;
				if (offset + destLen + 32 > data.Length) break;

				string destination = Encoding.UTF8.GetString(data, offset, destLen);
				offset += destLen;

				long registeredAtEntryId = BitConverter.ToInt64(data, offset); offset += 8;
				long lastConfirmedEntryId = BitConverter.ToInt64(data, offset); offset += 8;
				long registeredAtTicks = BitConverter.ToInt64(data, offset); offset += 8;
				long confirmedAtTicks = BitConverter.ToInt64(data, offset); offset += 8;

				rowsByDestination[destination] = new MaterializationCheckpointRow(
					destination,
					registeredAtEntryId,
					lastConfirmedEntryId,
					new DateTime(registeredAtTicks),
					new DateTime(confirmedAtTicks));
			}
		}

		private void Save()
		{
			int totalPayload = 0;
			foreach (var kvp in rowsByDestination)
			{
				byte[] destBytes = Encoding.UTF8.GetBytes(kvp.Key);
				totalPayload += 2 + destBytes.Length + 32;
			}

			byte[] data = new byte[HEADER_SIZE + totalPayload];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), rowsByDestination.Count); offset += 4;

			foreach (var kvp in rowsByDestination)
			{
				var row = kvp.Value;
				byte[] destBytes = Encoding.UTF8.GetBytes(kvp.Key);
				ushort destLen = (ushort)destBytes.Length;

				BitConverter.TryWriteBytes(data.AsSpan(offset, 2), destLen); offset += 2;
				Buffer.BlockCopy(destBytes, 0, data, offset, destBytes.Length); offset += destBytes.Length;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), row.RegisteredAtEntryId); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), row.LastConfirmedEntryId); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), row.RegisteredAt.Ticks); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), row.ConfirmedAt.Ticks); offset += 8;
			}

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);
		}

		protected internal override bool Register(string destination, long registeredAtEntryId, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (registeredAtEntryId < 0) throw new LanguageException($"RegisteredAtEntryId {registeredAtEntryId} must be zero or greater.");

			checkpointLock.EnterWriteLock();
			try
			{
				if (rowsByDestination.ContainsKey(destination))
				{
					return false;
				}

				rowsByDestination[destination] = new MaterializationCheckpointRow(
					destination,
					registeredAtEntryId,
					registeredAtEntryId,
					now,
					now);

				Save();
				return true;
			}
			finally
			{
				checkpointLock.ExitWriteLock();
			}
		}

		protected internal override Task<bool> RegisterAsync(string destination, long registeredAtEntryId, DateTime now)
		{
			return Task.FromResult(Register(destination, registeredAtEntryId, now));
		}

		protected internal override bool Deregister(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			checkpointLock.EnterWriteLock();
			try
			{
				if (!rowsByDestination.Remove(destination)) return false;

				Save();
				return true;
			}
			finally
			{
				checkpointLock.ExitWriteLock();
			}
		}

		protected internal override Task<bool> DeregisterAsync(string destination)
		{
			return Task.FromResult(Deregister(destination));
		}

		protected internal override bool TryGetWatermark(string destination, out long lastConfirmedEntryId)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			checkpointLock.EnterReadLock();
			try
			{
				if (rowsByDestination.TryGetValue(destination, out var row))
				{
					lastConfirmedEntryId = row.LastConfirmedEntryId;
					return true;
				}

				lastConfirmedEntryId = 0;
				return false;
			}
			finally
			{
				checkpointLock.ExitReadLock();
			}
		}

		protected internal override Task<(bool found, long lastConfirmedEntryId)> TryGetWatermarkAsync(string destination)
		{
			bool found = TryGetWatermark(destination, out long lastConfirmedEntryId);
			return Task.FromResult((found, lastConfirmedEntryId));
		}

		protected internal override bool ConfirmUntil(string destination, long entryId, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (entryId < 0) throw new LanguageException($"entryId {entryId} must be zero or greater.");

			checkpointLock.EnterWriteLock();
			try
			{
				if (!rowsByDestination.TryGetValue(destination, out var existing))
				{
					throw new LanguageException($"Destination '{destination}' is not registered. Register it before calling ConfirmUntil.");
				}

				if (entryId <= existing.LastConfirmedEntryId)
				{
					return false;
				}

				rowsByDestination[destination] = new MaterializationCheckpointRow(
					destination,
					existing.RegisteredAtEntryId,
					entryId,
					existing.RegisteredAt,
					now);

				Save();
				return true;
			}
			finally
			{
				checkpointLock.ExitWriteLock();
			}
		}

		protected internal override Task<bool> ConfirmUntilAsync(string destination, long entryId, DateTime now)
		{
			return Task.FromResult(ConfirmUntil(destination, entryId, now));
		}

		protected internal override void List(List<MaterializationCheckpointRow> result)
		{
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			checkpointLock.EnterReadLock();
			try
			{
				foreach (var row in rowsByDestination.Values)
				{
					result.Add(row);
				}
			}
			finally
			{
				checkpointLock.ExitReadLock();
			}

			result.Sort((a, b) => string.CompareOrdinal(a.Destination, b.Destination));
		}

		protected internal override Task ListAsync(List<MaterializationCheckpointRow> result)
		{
			List(result);
			return Task.CompletedTask;
		}
	}
}
