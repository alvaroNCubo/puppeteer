using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	// Paper 5 / Materialize v2 — Fase 2. Tipo de un record materializado del
	// journal. Public porque actor.Materialization.ReadRecordsAfter lo expone
	// al destination side (proxy HTTP en produccion). Tres kinds por construccion
	// del journal: Script (codigo DSL libre), Invocation (llamada a action por id),
	// Define (declaracion de action).
	public enum MaterializationRecordKind
	{
		Script,
		Invocation,
		Define
	}

	// Snapshot inmutable de un record del journal. Wire-format-agnostic — el
	// transport (HTTP/binario/JSON) serializa esto. Capa 1 del wire (records
	// solos, sin elision markers ni checkpoints — esos vienen por (c) y (d) en
	// Fase 3). Para destination que quiere Capa 1 only, este record es la
	// unidad transferida via wire verb (a) EnviameDesde.
	//
	// Polimorfismo aplanado: cada kind tiene sus campos relevantes y los otros
	// quedan en default. RecordKind selecciona la rama valida.
	public readonly struct MaterializationRecord
	{
		public long EntryId { get; }
		public MaterializationRecordKind Kind { get; }
		public DateTime OccurredAt { get; }
		public string Ip { get; }
		public string User { get; }
		public string Script { get; }
		public int ActionId { get; }
		public string Arguments { get; }
		public string DefineStatementText { get; }
		public string ExposeData { get; }

		internal MaterializationRecord(
			long entryId,
			MaterializationRecordKind kind,
			DateTime occurredAt,
			string ip,
			string user,
			string script,
			int actionId,
			string arguments,
			string defineStatementText,
			string exposeData)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			this.EntryId = entryId;
			this.Kind = kind;
			this.OccurredAt = occurredAt;
			this.Ip = ip;
			this.User = user;
			this.Script = script;
			this.ActionId = actionId;
			this.Arguments = arguments;
			this.DefineStatementText = defineStatementText;
			this.ExposeData = exposeData;
		}
	}

	// Paper 5 / Materialize v2 — Fase 3. Una entrada del reaction registry del
	// actor primary, ship via wire verb (c) DameCheckpointsHasta. El destination
	// recibe el formatted reaction (string canonico del DSL) y su reactionId
	// asignado por el primary; con esto puede reconstruir su propio registry
	// local con el mismo mapping.
	public readonly struct MaterializationReactionDefinition
	{
		public long ReactionId { get; }
		public string FormattedReaction { get; }

		internal MaterializationReactionDefinition(long reactionId, string formattedReaction)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(formattedReaction);
			this.ReactionId = reactionId;
			this.FormattedReaction = formattedReaction;
		}
	}

	// Estado de un seek level de una reaction (decision D1 #13: ship AS-IS,
	// snapshot atomic read). detected = match detectado y persistido; confirmed
	// = PerformCommand ejecutado con exito. La asimetria justifica el shipping
	// AS-IS sin clipping: valores adelantados al record watermark no causan dano
	// porque GetMinimum + IsCheckpointGreater controlan el comportamiento en
	// failover; el matcher no fabrica matches inexistentes.
	public readonly struct MaterializationReactionCheckpoint
	{
		public long ReactionId { get; }
		public int SeekLevel { get; }
		public long Detected { get; }
		public long Confirmed { get; }

		internal MaterializationReactionCheckpoint(long reactionId, int seekLevel, long detected, long confirmed)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException($"SeekLevel {seekLevel} must be zero or greater.");
			if (detected < 0) throw new LanguageException($"Detected {detected} must be zero or greater.");
			if (confirmed < 0) throw new LanguageException($"Confirmed {confirmed} must be zero or greater.");
			this.ReactionId = reactionId;
			this.SeekLevel = seekLevel;
			this.Detected = detected;
			this.Confirmed = confirmed;
		}
	}

	// Snapshot atomic combinado del estado de reactions: registry + checkpoints.
	// Resultado de (c) DameCheckpointsHasta. El destination usa esto para
	// reconstruir su propio EventElision storage local — junto con (d) ElidedRange
	// para los markers concretos.
	public readonly struct MaterializationReactionsSnapshot
	{
		public IReadOnlyList<MaterializationReactionDefinition> Reactions { get; }
		public IReadOnlyList<MaterializationReactionCheckpoint> Checkpoints { get; }

		internal MaterializationReactionsSnapshot(
			IReadOnlyList<MaterializationReactionDefinition> reactions,
			IReadOnlyList<MaterializationReactionCheckpoint> checkpoints)
		{
			ArgumentNullException.ThrowIfNull(reactions);
			ArgumentNullException.ThrowIfNull(checkpoints);
			this.Reactions = reactions;
			this.Checkpoints = checkpoints;
		}
	}

	// Un elision marker — un EntryId del journal marcado como elidido por una
	// reaction especifica. Ship via wire verb (d) DameElidedRange ordenados por
	// (Timestamp, EntryId) — el orden de marcaje temporal sale de
	// EventElision.Timestamp + DiaryId tie-break (sin MarkingOrder autoincrement
	// adicional, decision firmada 2026-05-13 PM: "no crear nuevos conceptos").
	public readonly struct MaterializationElisionMarker
	{
		public long EntryId { get; }
		public int ReactionId { get; }
		public DateTime Timestamp { get; }

		internal MaterializationElisionMarker(long entryId, int reactionId, DateTime timestamp)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			this.EntryId = entryId;
			this.ReactionId = reactionId;
			this.Timestamp = timestamp;
		}
	}

	// Paper 5 / Materialize v2 — Fase 0 (firmado D1 2026-05-13). Una row por
	// destination registrada; modela el contrato de presencia entre el actor
	// primary y un destination simbolico (ver decision #17). Habilita el
	// invariante Materialize-then-Distill de Fase 1: Distill(Until N) falla si
	// alguna destination registrada no confirmo expresamente haber recibido
	// hasta N. Fase 0 solo cubre la presencia (register/deregister/list); el
	// watermark monotonico (LastConfirmedEntryId via ConfirmoUntil) se ejercita
	// en Fase 1.
	//
	// Por-actor-por-construccion: cross-ref project_actor_per_db_principle.md
	// — cada actor vive en su propia DB, ninguna columna de particion necesaria.
	//
	// Diferencia ontologica vs EventMaterializationStorage (v1, marker queue):
	// aquel acumula filas (DiaryId, ReactionId, Destination) — N markers por
	// destination. Este es un registry: una sola row por destination con su
	// estado de delivery. v1 sigue vivo como capa de notificacion push; v2 le
	// agrega encima la capa de contrato de transferencia.
	internal abstract class MaterializationCheckpointStorage
	{
		protected readonly string ConnectionString;
		protected readonly IActorEventJournalClient EventJournalClient;

		protected MaterializationCheckpointStorage(IActorEventJournalClient eventJournalClient, string connectionString)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connectionString);
			ArgumentNullException.ThrowIfNull(eventJournalClient);

			this.EventJournalClient = eventJournalClient;
			this.ConnectionString = connectionString;
		}

		// Idempotente: si la destination ya existe, no-op (preserva watermark y
		// registeredAtEntryId existentes). Decision firmada — el caller debe
		// hacer Deregister + Register explicito para resetear. Retorna true si
		// se inserto una row nueva, false si la destination ya estaba registrada.
		protected internal abstract bool Register(string destination, long registeredAtEntryId, DateTime now);
		protected internal abstract Task<bool> RegisterAsync(string destination, long registeredAtEntryId, DateTime now);

		// Unilateral (decision D1 #11): no falla si la destination no existe.
		// Retorna true si se removio una row, false si no habia nada que remover.
		protected internal abstract bool Deregister(string destination);
		protected internal abstract Task<bool> DeregisterAsync(string destination);

		// Para Fase 1: lectura del watermark. Si la destination no esta
		// registrada, lastConfirmed sale 0 y retorna false. Decision firmada
		// D1 #14: Max-monotonic — el caller (ConfirmoUntil) lo subira solo cuando
		// el nuevo valor sea estrictamente mayor.
		protected internal abstract bool TryGetWatermark(string destination, out long lastConfirmedEntryId);
		protected internal abstract Task<(bool found, long lastConfirmedEntryId)> TryGetWatermarkAsync(string destination);

		// Fase 1 — wire verb (b) ConfirmoUntil (decision D1 #14). Max-monotonic
		// idempotente: actor.watermark[destination] = Max(existing, entryId). Si la
		// destination no esta registrada, lanza LanguageException (no se permite
		// confirm para destinations desconocidas — habria que registrar primero,
		// decisiones D1 #11/#12 sobre forward-fidelity desde registration time).
		//
		// Retorna true si el watermark avanzo (entryId > existing), false si fue
		// no-op (entryId <= existing). Recovery natural: retry de (a)(c)(d)(b)
		// recupera correctamente porque el segundo ConfirmoUntil con el mismo
		// entryId es no-op (decision D1 #14).
		//
		// Tambien actualiza ConfirmedAt al timestamp del avance (solo si avanza).
		protected internal abstract bool ConfirmUntil(string destination, long entryId, DateTime now);
		protected internal abstract Task<bool> ConfirmUntilAsync(string destination, long entryId, DateTime now);

		// Snapshot del registry: una row por destination registrada. Result
		// se rellena en orden estable (alfabetico por destination) para
		// determinismo en tests.
		protected internal abstract void List(List<MaterializationCheckpointRow> result);
		protected internal abstract Task ListAsync(List<MaterializationCheckpointRow> result);
	}

	// Row inmutable para evitar mutacion accidental tras List(). Snapshot del
	// estado de una destination registered en un instante. Public porque la
	// API actor.Materialization.List() la expone como elemento del resultado.
	public readonly struct MaterializationCheckpointRow : IEquatable<MaterializationCheckpointRow>
	{
		public string Destination { get; }
		public long RegisteredAtEntryId { get; }
		public long LastConfirmedEntryId { get; }
		public DateTime RegisteredAt { get; }
		public DateTime ConfirmedAt { get; }

		internal MaterializationCheckpointRow(
			string destination,
			long registeredAtEntryId,
			long lastConfirmedEntryId,
			DateTime registeredAt,
			DateTime confirmedAt)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (registeredAtEntryId < 0) throw new LanguageException($"RegisteredAtEntryId {registeredAtEntryId} must be zero or greater.");
			if (lastConfirmedEntryId < registeredAtEntryId) throw new LanguageException($"LastConfirmedEntryId {lastConfirmedEntryId} cannot be less than RegisteredAtEntryId {registeredAtEntryId}.");

			this.Destination = destination;
			this.RegisteredAtEntryId = registeredAtEntryId;
			this.LastConfirmedEntryId = lastConfirmedEntryId;
			this.RegisteredAt = registeredAt;
			this.ConfirmedAt = confirmedAt;
		}

		public bool Equals(MaterializationCheckpointRow other)
		{
			return string.Equals(Destination, other.Destination, StringComparison.Ordinal)
				&& RegisteredAtEntryId == other.RegisteredAtEntryId
				&& LastConfirmedEntryId == other.LastConfirmedEntryId
				&& RegisteredAt == other.RegisteredAt
				&& ConfirmedAt == other.ConfirmedAt;
		}

		public override bool Equals(object obj) => obj is MaterializationCheckpointRow row && Equals(row);
		public override int GetHashCode() => HashCode.Combine(Destination, RegisteredAtEntryId, LastConfirmedEntryId, RegisteredAt, ConfirmedAt);
	}
}
