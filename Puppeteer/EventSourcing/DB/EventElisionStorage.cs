using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal abstract class EventElisionStorage
	{
		protected readonly string ConnectionString;
		protected readonly IActorEventJournalClient EventJournalClient;

		protected EventElisionStorage(IActorEventJournalClient eventJournalClient, string connectionString)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connectionString);
			ArgumentNullException.ThrowIfNull(eventJournalClient);

			this.EventJournalClient = eventJournalClient;
			this.ConnectionString = connectionString;
		}
		protected internal abstract bool IsEventElided(long dairyId);
		protected internal abstract Task<bool> IsEventElidedAsync(long dairyId);
		protected internal abstract void MarkEventsAsElided(long[] dairyIds, int reactionId, DateTime timestamp);
		protected internal abstract Task MarkEventsAsElidedAsync(long[] dairyIds, int reactionId, DateTime timestamp);
		protected internal abstract void GetElidedEventsByReaction(int reactionId, List<long> result);
		protected internal abstract Task GetElidedEventsByReactionAsync(int reactionId, List<long> result);
		protected internal abstract void GetElidedEventsInRange(long fromDairyId, long toDairyId, HashSet<long> result);
		protected internal abstract Task GetElidedEventsInRangeAsync(long fromDairyId, long toDairyId, HashSet<long> result);

		// Paper 5 / Materialize v2 — Fase 3. Wire verb (d) DameElidedRange.
		// Lee elision markers en el rango [fromDairyId, toDairyId] inclusive,
		// ordenados por (Timestamp, DiaryId) — el orden de marcaje temporal
		// sale de EventElision.Timestamp con DiaryId como tie-break determinista.
		// Sin tabla nueva ni MarkingOrder autoincrement (decision firmada
		// 2026-05-13 PM por Alvaro: "no crear nuevos conceptos").
		protected internal virtual void ReadElisionMarkersInRange(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadElisionMarkersInRange yet (Materialize v2 Fase 3).");
		}

		protected internal virtual Task ReadElisionMarkersInRangeAsync(long fromDairyId, long toDairyId, List<MaterializationElisionMarker> result)
		{
			throw new NotImplementedException($"{GetType().Name} has not adopted ReadElisionMarkersInRangeAsync yet (Materialize v2 Fase 3).");
		}
	}
}
