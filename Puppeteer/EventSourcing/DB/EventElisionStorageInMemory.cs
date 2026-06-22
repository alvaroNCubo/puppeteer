using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class EventElisionStorageInMemory : EventElisionStorage
	{
		private readonly HashSet<long> elidedEvents = new HashSet<long>();
		private readonly Dictionary<int, HashSet<long>> eventsByReaction = new Dictionary<int, HashSet<long>>();
		// Materialize v2 / Fase 3: per-marker (reactionId, timestamp) to support
		// wire verb (d) DameElidedRange with temporal ordering. Re-marking the same EntryId
		// overwrites the value (equivalent to an UPDATE in SQL).
		private readonly Dictionary<long, (int ReactionId, DateTime Timestamp)> markerMetadata
			= new Dictionary<long, (int, DateTime)>();
		private readonly object lockObject = new object();

		internal EventElisionStorageInMemory(IActorEventJournalClient eventJournalClient)
			: base(eventJournalClient, "InMemory")
		{
		}

		protected internal override bool IsEventElided(long dairyId)
		{
			if (dairyId <= 0) throw new LanguageException($"DiaryId {dairyId} must be greater than zero.");

			lock (lockObject)
			{
				return elidedEvents.Contains(dairyId);
			}
		}

		protected internal override Task<bool> IsEventElidedAsync(long dairyId)
		{
			return Task.FromResult(IsEventElided(dairyId));
		}

		protected internal override void MarkEventsAsElided(long[] dairyIds, int reactionId, DateTime timestamp)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(dairyIds);
			if (dairyIds.Length == 0) return;

			lock (lockObject)
			{
				foreach (var dairyId in dairyIds)
				{
					elidedEvents.Add(dairyId);

					if (!eventsByReaction.ContainsKey(reactionId))
					{
						eventsByReaction[reactionId] = new HashSet<long>();
					}
					eventsByReaction[reactionId].Add(dairyId);

					// Materialize v2 / Fase 3: record (reactionId, timestamp) to
					// reconstruct the marking order in ReadElisionMarkersInRange.
					markerMetadata[dairyId] = (reactionId, timestamp);
				}
			}
		}

		protected internal override Task MarkEventsAsElidedAsync(long[] dairyIds, int reactionId, DateTime timestamp)
		{
			MarkEventsAsElided(dairyIds, reactionId, timestamp);
			return Task.CompletedTask;
		}

		protected internal override void GetElidedEventsByReaction(int reactionId, List<long> result)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			lock (lockObject)
			{
				if (eventsByReaction.ContainsKey(reactionId))
				{
					result.AddRange(eventsByReaction[reactionId].OrderBy(x => x));
				}
			}
		}

		protected internal override Task GetElidedEventsByReactionAsync(int reactionId, List<long> result)
		{
			GetElidedEventsByReaction(reactionId, result);
			return Task.CompletedTask;
		}

		protected internal override void GetElidedEventsInRange(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();

			lock (lockObject)
			{
				foreach (var dairyId in elidedEvents)
				{
					if (dairyId >= fromDairyId && dairyId <= toDairyId)
					{
						result.Add(dairyId);
					}
				}
			}
		}

		protected internal override Task GetElidedEventsInRangeAsync(long fromDairyId, long toDairyId, HashSet<long> result)
		{
			GetElidedEventsInRange(fromDairyId, toDairyId, result);
			return Task.CompletedTask;
		}

		// Materialize v2 / Fase 3 — wire verb (d) DameElidedRange. Ordered by
		// (Timestamp, EntryId) — the temporal marking order.
		protected internal override void ReadElisionMarkersInRange(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (fromDairyId <= 0) throw new LanguageException($"fromDairyId {fromDairyId} must be greater than zero.");
			if (toDairyId <= 0) throw new LanguageException($"toDairyId {toDairyId} must be greater than zero.");
			if (fromDairyId > toDairyId) throw new LanguageException($"fromDairyId {fromDairyId} must be less than or equal to toDairyId {toDairyId}.");

			result.Clear();

			lock (lockObject)
			{
				foreach (var dairyId in elidedEvents)
				{
					if (dairyId < fromDairyId || dairyId > toDairyId) continue;

					if (markerMetadata.TryGetValue(dairyId, out var meta))
					{
						result.Add(new MaterializationElisionMarker(dairyId, meta.ReactionId, meta.Timestamp));
					}
				}
			}

			result.Sort((a, b) =>
			{
				int cmp = a.Timestamp.CompareTo(b.Timestamp);
				if (cmp != 0) return cmp;
				return a.EntryId.CompareTo(b.EntryId);
			});
		}

		protected internal override Task ReadElisionMarkersInRangeAsync(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			ReadElisionMarkersInRange(fromDairyId, toDairyId, result);
			return Task.CompletedTask;
		}

		internal void Clear()
		{
			lock (lockObject)
			{
				elidedEvents.Clear();
				eventsByReaction.Clear();
				markerMetadata.Clear();
			}
		}

		// Used by Distill: once the elided records have been physically
		// materialized (outside the journal), their EntryIds stop being "logically
		// elided" and become "non-existent". Keeping them in elidedEvents would be
		// functionally harmless but grows without bound. Distill routes them through
		// here for cleanup.
		internal void RemoveElidedIds(IEnumerable<long> dairyIds)
		{
			ArgumentNullException.ThrowIfNull(dairyIds);

			lock (lockObject)
			{
				foreach (var id in dairyIds)
				{
					elidedEvents.Remove(id);
					markerMetadata.Remove(id);
					foreach (var set in eventsByReaction.Values)
					{
						set.Remove(id);
					}
				}
			}
		}
	}
}
