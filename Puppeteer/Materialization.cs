using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.DB;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Fase 0 (firmado D1 2026-05-13). Sub-namespace
	// administrativo del actor para registrar destinations que van a recibir
	// transferencia de estado. Una vez registrada una destination, el actor
	// asume el contrato Materialize-then-Distill: Fase 1 hara que Distill
	// falle si la destination no confirmo expresamente.
	//
	// Naming sub-namespace firmado por andragogia de DSL — evita verb-soup en
	// autocomplete cuando se agreguen Confirm(...) / AsProgramMirror(...) en
	// fases posteriores. Paralelo al patron de actor.Reactions.
	public class Materialization
	{
		private readonly ActorHandler handler;
		private MaterializationCheckpointStorage testStorageOverride;

		internal Materialization(ActorHandler handler)
		{
			ArgumentNullException.ThrowIfNull(handler);
			this.handler = handler;
		}

		// Test seam paralelo a actor.Reactions.SetDairyStorage(...) — permite
		// conectar la API a un storage in-memory sin pasar por EventSourcingStorage.
		// Produccion no llama este metodo; lo llama el ActorHandler internamente
		// cuando EventSourcingStorage configura el Diary.
		internal void SetCheckpointStorage(MaterializationCheckpointStorage storage)
		{
			ArgumentNullException.ThrowIfNull(storage);
			this.testStorageOverride = storage;
		}

		private MaterializationCheckpointStorage Storage
		{
			get
			{
				if (testStorageOverride != null) return testStorageOverride;
				MaterializationCheckpointStorage storage = handler.TryGetMaterializationCheckpointStorage();
				if (storage == null)
				{
					throw new LanguageException("Materialization requires EventSourcingStorage to be configured on the actor before registering destinations.");
				}
				return storage;
			}
		}

		// Registra una destination con watermark inicial = head actual del actor
		// (decision D1 #12: nueva registracion = head al momento, no genesis).
		// Idempotente: si la destination ya esta registrada, retorna false y
		// preserva el watermark existente (decision firmada). El caller debe
		// hacer Deregister + Register explicito para resetear.
		public bool Register(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			long head = handler.EntryId;
			return Storage.Register(destination, head, DateTime.Now);
		}

		// Unilateral (decision D1 #11): no falla si la destination no existe.
		// El otro lado (destination process) tolera el estado huerfano — los
		// handshakes bidireccionales se cuelgan en redes reales.
		public bool Deregister(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			return Storage.Deregister(destination);
		}

		// Fase 1 — wire verb (b) ConfirmoUntil (decision D1 #14). Llamado por el
		// destination process (push del destination al actor, decision D1 #10) para
		// declarar que recibio events hasta entryId inclusive. Max-monotonic: si
		// el watermark ya estaba en N o mas, no-op (retorna false). Si avanza,
		// retorna true y habilita Distill().Until(entryId) hasta ese punto.
		//
		// Lanza LanguageException si la destination no esta registrada — la
		// registracion es prerequisito (decisiones D1 #11/#12 sobre forward-fidelity
		// desde registration time, no genesis).
		public bool ConfirmUntil(string destination, long entryId)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (entryId < 0) throw new LanguageException($"entryId {entryId} must be zero or greater.");
			return Storage.ConfirmUntil(destination, entryId, DateTime.Now);
		}

		// Snapshot inmutable del registry. Orden alfabetico estable por
		// destination para determinismo en tests.
		public IReadOnlyList<MaterializationCheckpointRow> List()
		{
			List<MaterializationCheckpointRow> result = new List<MaterializationCheckpointRow>();
			Storage.List(result);
			return result;
		}

		// Fase 2 — wire verb (a) EnviameDesde (Capa 1). Llamado por el destination
		// process via HTTP (en produccion) o directo (in-process tests) para pedir
		// los records del journal desde fromEntryId (exclusivo) hasta el head actual.
		// Capa 1 = records solos, raw (sin filtrar Skip). El destination decide si
		// combina con (c)+(d) en Fase 3 para Capa 2 (derived state).
		//
		// Validaciones:
		// - Destination debe estar registrada (LanguageException si no).
		// - fromEntryId debe ser >= RegisteredAtEntryId de la destination (decision D1
		//   #12 forward-fidelity: una destination registrada en EntryId R no puede
		//   pedir records anteriores a R — esa historia no es responsabilidad del
		//   actor hacia ella).
		//
		// Caso edge: si primary distilo entre registration y read (e.g. con
		// .Forced()), los records distilados estaran fisicamente ausentes y el
		// resultado tendra "hole" silencioso. Responsabilidad del operador que
		// uso .Forced() (decision D1 #7).
		public IReadOnlyList<MaterializationRecord> ReadRecordsAfter(string destination, long fromEntryId)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (fromEntryId < 0) throw new LanguageException($"fromEntryId {fromEntryId} must be zero or greater.");

			MaterializationCheckpointStorage checkpointStorage = Storage;

			List<MaterializationCheckpointRow> registry = new List<MaterializationCheckpointRow>();
			checkpointStorage.List(registry);

			MaterializationCheckpointRow? row = null;
			foreach (var r in registry)
			{
				if (string.Equals(r.Destination, destination, StringComparison.Ordinal))
				{
					row = r;
					break;
				}
			}

			if (row == null)
			{
				throw new LanguageException($"Destination '{destination}' is not registered. Register it before calling ReadRecordsAfter.");
			}

			if (fromEntryId < row.Value.RegisteredAtEntryId)
			{
				throw new LanguageException(
					$"Destination '{destination}' is forward-fidelity from EntryId {row.Value.RegisteredAtEntryId}; cannot read records before its registration point (requested fromEntryId={fromEntryId}).");
			}

			DiaryStorage diaryStorage = handler.TryGetDiaryStorage();
			if (diaryStorage == null)
			{
				throw new LanguageException("Materialization requires EventSourcingStorage to be configured on the actor before calling ReadRecordsAfter.");
			}

			List<MaterializationRecord> result = new List<MaterializationRecord>();
			diaryStorage.ReadRecordsAfter(fromEntryId, result);
			return result;
		}

		// Fase 3 — wire verb (c) DameCheckpointsHasta (decision D1 #13). Snapshot
		// atomic del reaction registry + checkpoints. AS-IS, sin filtering por
		// watermark (el matcher en destination side controla via GetMinimum +
		// IsCheckpointGreater). Validacion: destination registrada.
		public MaterializationReactionsSnapshot ReadReactions(string destination)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			RequireRegistered(destination);

			DiaryStorage diaryStorage = handler.TryGetDiaryStorage();
			if (diaryStorage == null)
			{
				throw new LanguageException("Materialization requires EventSourcingStorage to be configured on the actor before calling ReadReactions.");
			}

			List<MaterializationReactionDefinition> reactions = new List<MaterializationReactionDefinition>();
			List<MaterializationReactionCheckpoint> checkpoints = new List<MaterializationReactionCheckpoint>();
			diaryStorage.ReadReactionRegistry(reactions);
			diaryStorage.ReadReactionCheckpoints(checkpoints);

			return new MaterializationReactionsSnapshot(reactions, checkpoints);
		}

		// Fase 3 — wire verb (d) DameElidedRange. Lee elision markers en el rango
		// [fromEntryId, toEntryId] inclusive, ordenados por (Timestamp, DiaryId).
		// Capa 2 = derived state — combinado con (c) ReadReactions y (a) ReadRecordsAfter
		// permite al destination reconstruir su EventElision local sin replay del
		// pattern matcher.
		//
		// Validaciones:
		// - Destination registrada.
		// - fromEntryId >= RegisteredAtEntryId (forward-fidelity D1 #12).
		// - fromEntryId <= toEntryId.
		public IReadOnlyList<MaterializationElisionMarker> ReadElidedRange(string destination, long fromEntryId, long toEntryId)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (fromEntryId <= 0) throw new LanguageException($"fromEntryId {fromEntryId} must be greater than zero.");
			if (toEntryId <= 0) throw new LanguageException($"toEntryId {toEntryId} must be greater than zero.");
			if (fromEntryId > toEntryId) throw new LanguageException($"fromEntryId {fromEntryId} must be less than or equal to toEntryId {toEntryId}.");

			MaterializationCheckpointRow row = RequireRegistered(destination);

			if (fromEntryId < row.RegisteredAtEntryId)
			{
				throw new LanguageException(
					$"Destination '{destination}' is forward-fidelity from EntryId {row.RegisteredAtEntryId}; cannot read elision markers before its registration point (requested fromEntryId={fromEntryId}).");
			}

			DiaryStorage diaryStorage = handler.TryGetDiaryStorage();
			if (diaryStorage == null)
			{
				throw new LanguageException("Materialization requires EventSourcingStorage to be configured on the actor before calling ReadElidedRange.");
			}

			List<MaterializationElisionMarker> result = new List<MaterializationElisionMarker>();
			diaryStorage.EventElisionStorage.ReadElisionMarkersInRange(fromEntryId, toEntryId, result);
			return result;
		}

		// Helper compartido por ReadReactions/ReadElidedRange para verificar que la
		// destination esta registrada. Retorna la row del registry para que el caller
		// pueda usar RegisteredAtEntryId si lo necesita.
		private MaterializationCheckpointRow RequireRegistered(string destination)
		{
			MaterializationCheckpointStorage checkpointStorage = Storage;
			List<MaterializationCheckpointRow> registry = new List<MaterializationCheckpointRow>();
			checkpointStorage.List(registry);

			foreach (var r in registry)
			{
				if (string.Equals(r.Destination, destination, StringComparison.Ordinal))
				{
					return r;
				}
			}

			throw new LanguageException($"Destination '{destination}' is not registered.");
		}
	}
}
