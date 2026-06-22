using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	// Paper 5 / claim 4 (signed 2026-05-12 PM). The DSL verb
	// .Metadata.Materialize(destination) writes rows
	// (DiaryId, ReactionId, Destination, Timestamp) into this storage. The runtime
	// does not deliver — the operation lives outside (an external delivery worker
	// that listens to OnRecordWritten or queries this table to recover after a
	// restart). Per-actor-by-construction: cross-ref
	// project_actor_per_db_principle.md — no partition required because each
	// actor lives in its own DB.
	//
	// Ontological difference vs EventElisionStorage: this storage accumulates
	// asymmetric markers (the primary actor produces them; an external consumer
	// consumes them). EventElision is symmetric to the same actor that
	// produces the marker. That is why the API is smaller: write + read-by-destination
	// + check; no range queries (Distill uses those, not applicable here).
	internal abstract class EventMaterializationStorage
	{
		protected readonly string ConnectionString;
		protected readonly IActorEventJournalClient EventJournalClient;

		protected EventMaterializationStorage(IActorEventJournalClient eventJournalClient, string connectionString)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connectionString);
			ArgumentNullException.ThrowIfNull(eventJournalClient);

			this.EventJournalClient = eventJournalClient;
			this.ConnectionString = connectionString;
		}

		protected internal abstract bool IsEventMaterialized(long dairyId, string destination);
		protected internal abstract Task<bool> IsEventMaterializedAsync(long dairyId, string destination);
		protected internal abstract void MarkEventsAsMaterialized(long[] dairyIds, int reactionId, string destination, DateTime timestamp);
		protected internal abstract Task MarkEventsAsMaterializedAsync(long[] dairyIds, int reactionId, string destination, DateTime timestamp);
		protected internal abstract void GetMaterializedEventsByDestination(string destination, List<long> result);
		protected internal abstract Task GetMaterializedEventsByDestinationAsync(string destination, List<long> result);
	}
}
