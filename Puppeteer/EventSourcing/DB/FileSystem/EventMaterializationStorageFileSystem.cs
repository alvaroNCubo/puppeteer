using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class EventMaterializationStorageFileSystem : EventMaterializationStorage
	{
		// "PPMT" = Puppeteer Materialization. Distinto del marker "PPEL" del
		// EventElision para que un mismatch de archivo sea detectable.
		private static readonly byte[] MAGIC = new byte[] { (byte)'P', (byte)'P', (byte)'M', (byte)'T' };
		private const ushort FORMAT_VERSION = 1;
		private const int HEADER_SIZE = 10;
		// Record: ReactionId (4 bytes) + EntryId (8 bytes) + DestLength (2 bytes) + Dest bytes (UTF-8 length-prefixed).

		private readonly string filePath;
		private readonly IAtomicFileOperation atomicOp;
		private readonly ReaderWriterLockSlim materializationLock = new ReaderWriterLockSlim();

		// destination -> set of entryIds materialized to that destination.
		private readonly Dictionary<string, HashSet<long>> eventsByDestination = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
		// Indice paralelo por reactionId para auditoria / debugging futuros.
		// reactionId -> (destination -> set of entryIds).
		private readonly Dictionary<int, Dictionary<string, HashSet<long>>> eventsByReaction = new Dictionary<int, Dictionary<string, HashSet<long>>>();

		internal EventMaterializationStorageFileSystem(
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

			for (int i = 0; i < count && offset + 14 <= data.Length; i++)
			{
				int reactionId = BitConverter.ToInt32(data, offset); offset += 4;
				long entryId = BitConverter.ToInt64(data, offset); offset += 8;
				ushort destLen = BitConverter.ToUInt16(data, offset); offset += 2;

				if (offset + destLen > data.Length) break;

				string destination = Encoding.UTF8.GetString(data, offset, destLen);
				offset += destLen;

				AddToIndexes(reactionId, entryId, destination);
			}
		}

		private void AddToIndexes(int reactionId, long entryId, string destination)
		{
			if (!eventsByDestination.TryGetValue(destination, out var destSet))
			{
				destSet = new HashSet<long>();
				eventsByDestination[destination] = destSet;
			}
			destSet.Add(entryId);

			if (!eventsByReaction.TryGetValue(reactionId, out var byDestForReaction))
			{
				byDestForReaction = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
				eventsByReaction[reactionId] = byDestForReaction;
			}
			if (!byDestForReaction.TryGetValue(destination, out var entrySet))
			{
				entrySet = new HashSet<long>();
				byDestForReaction[destination] = entrySet;
			}
			entrySet.Add(entryId);
		}

		private void Save()
		{
			int totalRecords = 0;
			int totalPayload = 0;
			foreach (var kvp in eventsByReaction)
			{
				foreach (var destKvp in kvp.Value)
				{
					byte[] destBytes = Encoding.UTF8.GetBytes(destKvp.Key);
					int recordSize = 14 + destBytes.Length;
					totalRecords += destKvp.Value.Count;
					totalPayload += destKvp.Value.Count * recordSize;
				}
			}

			byte[] data = new byte[HEADER_SIZE + totalPayload];
			int offset = 0;

			Buffer.BlockCopy(MAGIC, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), totalRecords); offset += 4;

			foreach (var kvp in eventsByReaction)
			{
				int reactionId = kvp.Key;
				foreach (var destKvp in kvp.Value)
				{
					byte[] destBytes = Encoding.UTF8.GetBytes(destKvp.Key);
					ushort destLen = (ushort)destBytes.Length;
					foreach (long entryId in destKvp.Value)
					{
						BitConverter.TryWriteBytes(data.AsSpan(offset, 4), reactionId); offset += 4;
						BitConverter.TryWriteBytes(data.AsSpan(offset, 8), entryId); offset += 8;
						BitConverter.TryWriteBytes(data.AsSpan(offset, 2), destLen); offset += 2;
						Buffer.BlockCopy(destBytes, 0, data, offset, destBytes.Length); offset += destBytes.Length;
					}
				}
			}

			string tempPath = filePath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, filePath);
		}

		protected internal override bool IsEventMaterialized(long dairyId, string destination)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			materializationLock.EnterReadLock();
			try
			{
				return eventsByDestination.TryGetValue(destination, out var set) && set.Contains(dairyId);
			}
			finally
			{
				materializationLock.ExitReadLock();
			}
		}

		protected internal override Task<bool> IsEventMaterializedAsync(long dairyId, string destination)
		{
			return Task.FromResult(IsEventMaterialized(dairyId, destination));
		}

		protected internal override void MarkEventsAsMaterialized(long[] dairyIds, int reactionId, string destination, DateTime timestamp)
		{
			ArgumentNullException.ThrowIfNull(dairyIds);
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (dairyIds.Length == 0) return;

			materializationLock.EnterWriteLock();
			try
			{
				foreach (long id in dairyIds)
				{
					if (id <= 0) throw new LanguageException($"DiaryId {id} must be greater than zero.");
					AddToIndexes(reactionId, id, destination);
				}

				Save();
			}
			finally
			{
				materializationLock.ExitWriteLock();
			}
		}

		protected internal override Task MarkEventsAsMaterializedAsync(long[] dairyIds, int reactionId, string destination, DateTime timestamp)
		{
			MarkEventsAsMaterialized(dairyIds, reactionId, destination, timestamp);
			return Task.CompletedTask;
		}

		protected internal override void GetMaterializedEventsByDestination(string destination, List<long> result)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			materializationLock.EnterReadLock();
			try
			{
				if (eventsByDestination.TryGetValue(destination, out var set))
				{
					result.AddRange(set);
					result.Sort();
				}
			}
			finally
			{
				materializationLock.ExitReadLock();
			}
		}

		protected internal override Task GetMaterializedEventsByDestinationAsync(string destination, List<long> result)
		{
			GetMaterializedEventsByDestination(destination, result);
			return Task.CompletedTask;
		}
	}
}
