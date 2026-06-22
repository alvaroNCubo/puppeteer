using System;
using System.Collections.Generic;
using System.IO;

namespace Puppeteer.EventSourcing.Playbill
{
	// Storage parallel to DiaryStorage for the Performance's operational evidence.
	// Internal abstract — the 4 backends (InMemory, FileSystem, MySQL, SQLServer)
	// are also internal; extensibility for custom backends (e.g. Postgres)
	// requires recompiling Puppeteer, same model as DiaryStorage.
	//
	// Signed design (project_playbill_design.md, 2026-05-22):
	// - Same connection string as DiaryStorage; lives in the same DB (SQL) or in
	//   an actor subdir (FS) — one-actor-per-database principle preserved.
	// - Auto-provision: each backend creates its artifacts (tables / files) in
	//   the constructor if they do not exist — parallel to the Diary.
	// - Idempotent schema registry: RegisterSchema allows re-registering the same
	//   schema with the same signature; a different signature throws LanguageException.
	// - Autonomous Distill, asymmetric vs Diary: PlaybillStore.Distill() queries
	//   the actor's journal (external state) to find orphan records
	//   and remove them via rebuild-via-shadow-swap.
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

		// === Replication hooks (Phase 5) ===

		// Fires after a successful RegisterSchema (new or idempotent). The
		// subscriber is typically Stage (Choreography), which wraps the event
		// in a PlaybillSchemaCue and broadcasts it to the Casts.
		//
		// Since RegisterSchema is idempotent, this callback fires both for
		// new registrations and re-registrations. The cue receiver is also
		// idempotent, so a double-fire is safe.
		internal Action<string, string> OnSchemaRegistered;

		// Fires after a successful WriteRecord. The subscriber wraps it in
		// a PlaybillCue and broadcasts it.
		internal Action<long, string, string> OnRecordWritten;

		// === Schema registry (idempotente) ===

		// Registers a schema. If it already exists with the same signature: no-op (silent).
		// If it already exists with a different signature: LanguageException (schema drift
		// requires explicit migration by DevOps).
		internal abstract void RegisterSchema(string schemaName, string declarations);

		// Reads the schema's declarations text. Null if it does not exist.
		internal abstract string GetSchemaDeclarations(string schemaName);

		// Enumerates all registered schemas in stable order (by name).
		internal abstract IEnumerable<(string Name, string Declarations)> ListSchemas();

		// === Record writes (single path, append-only) ===

		// Writes a PlaybillRecord. UNIQUE EntryId — a second write of the same
		// EntryId throws LanguageException (each invocation produces exactly 1
		// playbill record by construction of Performance.PerformCommand).
		internal abstract void WriteRecord(long entryId, string schemaName, string serializedParameters);

		// === Forensic reads (NO rehydration) ===

		// Reads a specific record by EntryId. Null if it does not exist.
		internal abstract (string SchemaName, string SerializedParameters)? ReadRecord(long entryId);

		// Enumerates records of a schema in ascending EntryId order.
		internal abstract IEnumerable<(long EntryId, string SerializedParameters)> ReadRecordsForSchema(string schemaName);

		// === Replication source ===

		// Reads records with EntryId > afterEntryId, in ascending order. Wire-format
		// for PlaybillReplication (Phase 5). Parallel to DiaryStorage.ReadRecordsAfter
		// but operates over the playbill store, not over the actor's journal.
		internal abstract void ReadRecordsAfter(long afterEntryId, List<PlaybillRecord> result);

		// === Distill — autonomous, no arguments (asymmetric vs DiaryStorage) ===

		// Removes playbill records whose EntryId no longer exists in the actor's
		// journal (referential integrity). Implemented via rebuild-via-shadow-swap
		// uniformly across the 4 backends. Idempotent: if there are no orphans, no-op.
		internal abstract void Distill();

		// === Admin ===

		// Exports records within a date range as a zip. Optional per backend.
		internal abstract MemoryStream Archive(DateTime startDate, DateTime endDate);
	}
}
