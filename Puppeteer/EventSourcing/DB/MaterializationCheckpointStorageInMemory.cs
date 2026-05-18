using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class MaterializationCheckpointStorageInMemory : MaterializationCheckpointStorage
	{
		private readonly Dictionary<string, MaterializationCheckpointRow> rowsByDestination
			= new Dictionary<string, MaterializationCheckpointRow>(StringComparer.Ordinal);
		private readonly object lockObject = new object();

		internal MaterializationCheckpointStorageInMemory(IActorEventJournalClient eventJournalClient)
			: base(eventJournalClient, "InMemory")
		{
		}

		protected internal override bool Register(string destination, long registeredAtEntryId, DateTime now)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (registeredAtEntryId < 0) throw new LanguageException($"RegisteredAtEntryId {registeredAtEntryId} must be zero or greater.");

			lock (lockObject)
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
				return true;
			}
		}

		protected internal override Task<bool> RegisterAsync(string destination, long registeredAtEntryId, DateTime now)
		{
			return Task.FromResult(Register(destination, registeredAtEntryId, now));
		}

		protected internal override bool Deregister(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			lock (lockObject)
			{
				return rowsByDestination.Remove(destination);
			}
		}

		protected internal override Task<bool> DeregisterAsync(string destination)
		{
			return Task.FromResult(Deregister(destination));
		}

		protected internal override bool TryGetWatermark(string destination, out long lastConfirmedEntryId)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			lock (lockObject)
			{
				if (rowsByDestination.TryGetValue(destination, out var row))
				{
					lastConfirmedEntryId = row.LastConfirmedEntryId;
					return true;
				}

				lastConfirmedEntryId = 0;
				return false;
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

			lock (lockObject)
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
				return true;
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

			lock (lockObject)
			{
				foreach (var row in rowsByDestination.Values)
				{
					result.Add(row);
				}
			}

			result.Sort((a, b) => string.CompareOrdinal(a.Destination, b.Destination));
		}

		protected internal override Task ListAsync(List<MaterializationCheckpointRow> result)
		{
			List(result);
			return Task.CompletedTask;
		}

		internal void Clear()
		{
			lock (lockObject)
			{
				rowsByDestination.Clear();
			}
		}
	}
}
