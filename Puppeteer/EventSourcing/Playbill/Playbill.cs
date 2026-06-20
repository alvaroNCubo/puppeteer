using System;
using System.Collections.Generic;
using System.IO;
using Puppeteer.EventSourcing.DB;

namespace Puppeteer.EventSourcing.Playbill
{
	// Facade publico del Playbill. Choreography lo construye con la storage
	// config del actor (DatabaseType + connectionString + actorName + logger);
	// el facade selecciona el backend correcto y delega el resto de operaciones.
	//
	// Auto-provision: cada backend crea sus artefactos en el constructor si no
	// existen. Para actores nuevos en un sistema fresh, no hay accion manual de
	// DevOps; para actores legacy con journal preexistente, se corre una vez el
	// script de migracion (separado).
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

		// === Replication hooks (Fase 5) — proxy de los callbacks del store ===
		//
		// Choreography (Stage) se suscribe a estos eventos para broadcastear los
		// schemas y records nuevos como PlaybillSchemaCue / PlaybillCue. Internal:
		// solo el codigo dentro del solucion (Choreography + UnitTests) suscribe.
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

		// === Internal direct-write path (Fase 5) ===
		//
		// Permite a Cast aplicar PlaybillCue/PlaybillSchemaCue recibidos via wire
		// sin tener que re-construir un Parameters. La invariante de unicidad de
		// EntryId queda en el store (lanza LanguageException si ya existe).
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

		// Write entry point. Acepta una Parameters instance (configurada con
		// values via su indexer); la serializa con el wire format de V2 y la
		// persiste atomicamente. EntryId debe ser el del journal que se acaba
		// de escribir (Performance.PerformCommand lo obtiene del actor).
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

			// IN_MEMORY chosen as the wire-neutral serialization (Playbill no
			// hace SQL-quote escapes — el blob persiste via parametrized binding
			// o via UTF-8 en archivo, no via concat directa de string).
			string serialized = values.SerializeForTransport(DatabaseType.IN_MEMORY);
			store.WriteRecord(entryId, schemaName, serialized);
		}

		private static void CoerceNullValuesToDefaults(Parameters values)
		{
			foreach (var p in values)
			{
				// Playbill final refactor: ya no hay SystemParameter (incluido Now) — todo es user.
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

		// Replication source — usado por PlaybillReplication (Fase 5).
		public void ReadRecordsAfter(long afterEntryId, List<PlaybillRecord> result)
		{
			store.ReadRecordsAfter(afterEntryId, result);
		}

		// Distill autonomo — remueve huerfanos referenciales. Llamado por
		// Performance.Distill() despues de actor.Distill().
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
