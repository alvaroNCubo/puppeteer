using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	// Paper 5 / claim 4 (firmado 2026-05-12 PM). El verbo DSL
	// .Metadata.Materialize(destination) escribe filas
	// (DiaryId, ReactionId, Destination, Timestamp) en este storage. El runtime
	// no entrega — la operacion vive fuera (delivery worker external que
	// escucha OnRecordWritten o que consulta esta tabla para recuperar tras un
	// reinicio). Por-actor-por-construccion: cross-ref
	// project_actor_per_db_principle.md — no requiere particion porque cada
	// actor vive en su propia DB.
	//
	// Diferencia ontologica vs EventElisionStorage: este storage acumula
	// markers asimetricos (el actor primary los produce; un consumidor
	// external los consume). EventElision es simetrico al mismo actor que
	// produce el marker. Por eso la API es mas chica: write + read-by-destination
	// + check; sin range queries (Distill las usa, aqui no aplica).
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
