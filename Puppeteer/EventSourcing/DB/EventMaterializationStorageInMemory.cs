using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class EventMaterializationStorageInMemory : EventMaterializationStorage
	{
		private readonly Dictionary<string, HashSet<long>> eventsByDestination = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
		private readonly object lockObject = new object();

		internal EventMaterializationStorageInMemory(IActorEventJournalClient eventJournalClient)
			: base(eventJournalClient, "InMemory")
		{
		}

		protected internal override bool IsEventMaterialized(long dairyId, string destination)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);

			lock (lockObject)
			{
				return eventsByDestination.TryGetValue(destination, out var set) && set.Contains(dairyId);
			}
		}

		protected internal override Task<bool> IsEventMaterializedAsync(long dairyId, string destination)
		{
			return Task.FromResult(IsEventMaterialized(dairyId, destination));
		}

		protected internal override void MarkEventsAsMaterialized(long[] dairyIds, int reactionId, string destination, DateTime timestamp)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(dairyIds);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (dairyIds.Length == 0) return;

			lock (lockObject)
			{
				if (!eventsByDestination.TryGetValue(destination, out var set))
				{
					set = new HashSet<long>();
					eventsByDestination[destination] = set;
				}

				foreach (var dairyId in dairyIds)
				{
					if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");
					set.Add(dairyId);
				}
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

			lock (lockObject)
			{
				if (eventsByDestination.TryGetValue(destination, out var set))
				{
					result.AddRange(set.OrderBy(x => x));
				}
			}
		}

		protected internal override Task GetMaterializedEventsByDestinationAsync(string destination, List<long> result)
		{
			GetMaterializedEventsByDestination(destination, result);
			return Task.CompletedTask;
		}

		internal void Clear()
		{
			lock (lockObject)
			{
				eventsByDestination.Clear();
			}
		}
	}
}
