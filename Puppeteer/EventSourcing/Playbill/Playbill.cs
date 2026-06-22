using System;
using System.Collections.Generic;
using System.IO;
using Puppeteer.EventSourcing.DB;

namespace Puppeteer.EventSourcing.Playbill
{
	// Public facade of the Playbill. Choreography constructs it with the actor's
	// storage config (DatabaseType + connectionString + actorName + logger);
	// the facade selects the correct backend and delegates the rest of the operations.
	//
	// Auto-provision: each backend creates its artifacts in the constructor if they
	// do not exist. For new actors on a fresh system, there is no manual DevOps
	// action; for legacy actors with a pre-existing journal, the migration
	// script (separate) is run once.
	public sealed class Playbill
	{
		private readonly PlaybillStore store;

		public Playbill(DatabaseType dbType, string connectionString, string actorName, IPuppeteerLogger logger)
		{
			ArgumentNullException.ThrowIfNull(connectionString);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(actorName);
			ArgumentNullException.ThrowIfNull(logger);

			if (dbType == DatabaseType.IN_MEMORY)
			{
				store = new PlaybillStoreInMemory(actorName, logger);
			}
			else if (dbType == DatabaseType.MySQL)
			{
				store = new PlaybillStoreMySQL(actorName, connectionString, logger);
			}
			else if (dbType == DatabaseType.SQLServer)
			{
				store = new PlaybillStoreSQLServer(actorName, connectionString, logger);
			}
			else if (dbType == DatabaseType.FileSystem)
			{
				store = new PlaybillStoreFileSystem(actorName, connectionString, logger);
			}
			else
			{
				throw new LanguageException($"DatabaseType '{dbType}' not supported by Playbill.");
			}
		}

		// === Replication hooks (Phase 5) — proxy of the store's callbacks ===
		//
		// Choreography (Stage) subscribes to these events to broadcast the new
		// schemas and records as PlaybillSchemaCue / PlaybillCue. Internal:
		// only code within the solution (Choreography + UnitTests) subscribes.
		internal event Action<string, string> OnSchemaRegistered
		{
			add { store.OnSchemaRegistered += value; }
			remove { store.OnSchemaRegistered -= value; }
		}

		internal event Action<long, string, string> OnRecordWritten
		{
			add { store.OnRecordWritten += value; }
			remove { store.OnRecordWritten -= value; }
		}

		// === Internal direct-write path (Phase 5) ===
		//
		// Lets the Cast apply PlaybillCue/PlaybillSchemaCue received via wire
		// without having to rebuild a Parameters. The EntryId uniqueness invariant
		// stays in the store (throws LanguageException if it already exists).
		internal void RegisterSchemaRaw(string schemaName, string declarations)
		{
			store.RegisterSchema(schemaName, declarations);
		}

		internal void WriteRecordRaw(long entryId, string schemaName, string serializedParameters)
		{
			store.WriteRecord(entryId, schemaName, serializedParameters);
		}

		public void RegisterSchema(string schemaName, string declarations)
		{
			store.RegisterSchema(schemaName, declarations);
		}

		public string GetSchemaDeclarations(string schemaName)
		{
			return store.GetSchemaDeclarations(schemaName);
		}

		public IEnumerable<(string Name, string Declarations)> ListSchemas()
		{
			return store.ListSchemas();
		}

		// Write entry point. Accepts a Parameters instance (configured with
		// values via its indexer); serializes it with the V2 wire format and
		// persists it atomically. EntryId must be that of the journal entry just
		// written (Performance.PerformCommand obtains it from the actor).
		public void WriteRecord(long entryId, string schemaName, Parameters values)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			ArgumentNullException.ThrowIfNull(values);

			// Optional fields that the dev did not explicitly set arrive with
			// Parameter.Value == null. The shared serializer rejects null values,
			// so coerce to type defaults here before serializing. This is the
			// Playbill-level interpretation of "optional without value":
			// store the type default; the read path can re-interpret defaults
			// as "not provided" by convention.
			CoerceNullValuesToDefaults(values);

			// IN_MEMORY chosen as the wire-neutral serialization (Playbill does
			// not do SQL-quote escapes — the blob persists via parametrized binding
			// or via UTF-8 in a file, not via direct string concat).
			string serialized = values.SerializeForTransport(DatabaseType.IN_MEMORY);
			store.WriteRecord(entryId, schemaName, serialized);
		}

		private static void CoerceNullValuesToDefaults(Parameters values)
		{
			foreach (var p in values)
			{
				// Playbill final refactor: there is no longer a SystemParameter (including Now) — everything is user.
				if (!p.IsEmpty) continue;

				var t = p.ParameterType;
				if (t == typeof(string)) values[p.Name, typeof(string)] = string.Empty;
				else if (t == typeof(int)) values[p.Name, typeof(int)] = 0;
				else if (t == typeof(long)) values[p.Name, typeof(long)] = 0L;
				else if (t == typeof(bool)) values[p.Name, typeof(bool)] = false;
				else if (t == typeof(decimal)) values[p.Name, typeof(decimal)] = 0m;
				else if (t == typeof(double)) values[p.Name, typeof(double)] = 0.0;
				else if (t == typeof(DateTime)) values[p.Name, typeof(DateTime)] = DateTime.MinValue;
			}
		}

		public (string SchemaName, string SerializedParameters)? ReadRecord(long entryId)
		{
			return store.ReadRecord(entryId);
		}

		public IEnumerable<(long EntryId, string SerializedParameters)> ReadRecordsForSchema(string schemaName)
		{
			return store.ReadRecordsForSchema(schemaName);
		}

		// Replication source — used by PlaybillReplication (Phase 5).
		public void ReadRecordsAfter(long afterEntryId, List<PlaybillRecord> result)
		{
			store.ReadRecordsAfter(afterEntryId, result);
		}

		// Autonomous Distill — removes referential orphans. Called by
		// Performance.Distill() after actor.Distill().
		public void Distill()
		{
			store.Distill();
		}

		public MemoryStream Archive(DateTime startDate, DateTime endDate)
		{
			return store.Archive(startDate, endDate);
		}
	}
}
