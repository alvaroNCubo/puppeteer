using System;
using System.Collections.Generic;
using System.IO;

namespace Puppeteer.EventSourcing.Playbill
{
	// Storage paralelo a DiaryStorage para la evidencia operacional del Performance.
	// Internal abstract — los 4 backends (InMemory, FileSystem, MySQL, SQLServer)
	// son tambien internal; extensibilidad para backends custom (ej. Postgres)
	// requiere recompilar Puppeteer, mismo modelo que DiaryStorage.
	//
	// Diseno firmado (project_playbill_design.md, 2026-05-22):
	// - Mismo connection string que DiaryStorage; vive en la misma DB (SQL) o en
	//   un subdir del actor (FS) — one-actor-per-database principle preservado.
	// - Auto-provision: cada backend crea sus artefactos (tablas / archivos) en
	//   el constructor si no existen — paralelo al Diary.
	// - Schema registry idempotente: RegisterSchema admite re-registrar el mismo
	//   schema con misma firma; firma distinta lanza LanguageException.
	// - Distill autonomo y asimetrico vs Diary: PlaybillStore.Distill() consulta
	//   el journal del actor (estado externo) para encontrar registros huerfanos
	//   y removerlos via rebuild-via-shadow-swap.
	internal abstract class PlaybillStore
	{
		protected readonly string ConnectionString;
		protected readonly string ActorName;
		protected readonly IPuppeteerLogger Logger;

		protected PlaybillStore(string actorName, string connectionString, IPuppeteerLogger logger)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(actorName);
			ArgumentNullException.ThrowIfNull(connectionString);
			ArgumentNullException.ThrowIfNull(logger);

			this.ActorName = actorName;
			this.ConnectionString = connectionString;
			this.Logger = logger;
		}

		// === Replication hooks (Fase 5) ===

		// Fires despues de un RegisterSchema exitoso (nuevo o idempotent). El
		// subscriber es tipicamente Stage (Choreography), que envuelve el evento
		// en un PlaybillSchemaCue y lo broadcastea a las Casts.
		//
		// Como RegisterSchema es idempotent, este callback se invoca tanto para
		// registros nuevos como para re-registros. El receptor del cue tambien
		// es idempotent, asi que doble-fire es seguro.
		internal Action<string, string> OnSchemaRegistered;

		// Fires despues de un WriteRecord exitoso. El subscriber lo envuelve en
		// un PlaybillCue y lo broadcastea.
		internal Action<long, string, string> OnRecordWritten;

		// === Schema registry (idempotente) ===

		// Registra un schema. Si ya existe con misma firma: no-op (silencioso).
		// Si ya existe con firma distinta: LanguageException (drift de schema
		// requiere migracion explicita por DevOps).
		internal abstract void RegisterSchema(string schemaName, string declarations);

		// Lee declarations text del schema. Null si no existe.
		internal abstract string GetSchemaDeclarations(string schemaName);

		// Enumera todos los schemas registrados en orden estable (por nombre).
		internal abstract IEnumerable<(string Name, string Declarations)> ListSchemas();

		// === Record writes (single path, append-only) ===

		// Escribe un PlaybillRecord. EntryId UNICO — segunda escritura del mismo
		// EntryId lanza LanguageException (cada invocacion produce exactamente 1
		// playbill record por construccion del Performance.PerformCommand).
		internal abstract void WriteRecord(long entryId, string schemaName, string serializedParameters);

		// === Forensic reads (NO rehidratacion) ===

		// Lee un record especifico por EntryId. Null si no existe.
		internal abstract (string SchemaName, string SerializedParameters)? ReadRecord(long entryId);

		// Enumera records de un schema en orden de EntryId ascendente.
		internal abstract IEnumerable<(long EntryId, string SerializedParameters)> ReadRecordsForSchema(string schemaName);

		// === Replication source ===

		// Lee records con EntryId > afterEntryId, en orden ascendente. Wire-format
		// para PlaybillReplication (Fase 5). Paralelo a DiaryStorage.ReadRecordsAfter
		// pero opera sobre el playbill store, no sobre el journal del actor.
		internal abstract void ReadRecordsAfter(long afterEntryId, List<PlaybillRecord> result);

		// === Distill — autonomo, sin argumentos (asimetrico vs DiaryStorage) ===

		// Remueve playbill records cuyos EntryId ya no existen en el journal del
		// actor (referential integrity). Implementacion via rebuild-via-shadow-swap
		// uniformemente en los 4 backends. Idempotente: si no hay huerfanos, no-op.
		internal abstract void Distill();

		// === Admin ===

		// Exporta records en rango de fechas como zip. Opcional por backend.
		internal abstract MemoryStream Archive(DateTime startDate, DateTime endDate);
	}
}
