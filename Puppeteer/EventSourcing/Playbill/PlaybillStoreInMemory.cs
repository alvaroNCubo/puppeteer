using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Puppeteer.EventSourcing.DB;

namespace Puppeteer.EventSourcing.Playbill
{
	// Backend de Playbill en memoria. Tests-only por construccion (paralelo a
	// DiaryStorageInMemory). Shared storage por nombre de actor — multiples
	// instancias del mismo actor comparten el mismo storage dict para que el
	// patron "construir Performance varias veces apuntando al mismo actor en
	// tests" funcione consistentemente.
	internal sealed class PlaybillStoreInMemory : PlaybillStore
	{
		private static readonly Dictionary<string, SharedStorageData> sharedStorages = new Dictionary<string, SharedStorageData>();
		private static readonly object sharedStoragesLock = new object();

		private sealed class SharedStorageData
		{
			internal readonly Dictionary<string, string> Schemas = new Dictionary<string, string>();
			internal readonly SortedDictionary<long, (string SchemaName, string SerializedParameters)> Records = new SortedDictionary<long, (string, string)>();
			internal readonly object gate = new object();
		}

		private readonly SharedStorageData storage;

		internal PlaybillStoreInMemory(string actorName, IPuppeteerLogger logger)
			: base(actorName, "InMemory", logger)
		{
			lock (sharedStoragesLock)
			{
				if (!sharedStorages.TryGetValue(actorName, out storage))
				{
					storage = new SharedStorageData();
					sharedStorages[actorName] = storage;
				}
			}
		}

		// Test seam: limpia el storage compartido para un actor. Solo para tests
		// que quieren aislamiento entre cases.
		internal static void ClearForTesting(string actorName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(actorName);
			lock (sharedStoragesLock)
			{
				sharedStorages.Remove(actorName);
			}
		}

		internal override void RegisterSchema(string schemaName, string declarations)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			ArgumentNullException.ThrowIfNull(declarations);

			lock (storage.gate)
			{
				if (storage.Schemas.TryGetValue(schemaName, out var existing))
				{
					if (existing != declarations)
					{
						throw new LanguageException(
							$"Playbill schema '{schemaName}' is already registered with a different shape. " +
							$"Existing: '{existing}'. New: '{declarations}'. Schema drift requires migration.");
					}
					OnSchemaRegistered?.Invoke(schemaName, declarations); // idempotent — Cast receivers also dedupe
					return;
				}
				storage.Schemas[schemaName] = declarations;
			}
			OnSchemaRegistered?.Invoke(schemaName, declarations);
		}

		internal override string GetSchemaDeclarations(string schemaName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			lock (storage.gate)
			{
				return storage.Schemas.TryGetValue(schemaName, out var d) ? d : null;
			}
		}

		internal override IEnumerable<(string Name, string Declarations)> ListSchemas()
		{
			lock (storage.gate)
			{
				return storage.Schemas
					.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
					.Select(kvp => (kvp.Key, kvp.Value))
					.ToList();
			}
		}

		internal override void WriteRecord(long entryId, string schemaName, string serializedParameters)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			ArgumentNullException.ThrowIfNull(serializedParameters);

			bool fireCallback = false;
			lock (storage.gate)
			{
				if (!storage.Schemas.ContainsKey(schemaName))
				{
					throw new LanguageException($"Cannot write Playbill record: schema '{schemaName}' is not registered.");
				}
				if (storage.Records.ContainsKey(entryId))
				{
					throw new LanguageException($"Playbill record for EntryId {entryId} already exists (expected at most one per entry).");
				}
				storage.Records[entryId] = (schemaName, serializedParameters);
				fireCallback = true;
			}
			if (fireCallback) OnRecordWritten?.Invoke(entryId, schemaName, serializedParameters);
		}

		internal override (string SchemaName, string SerializedParameters)? ReadRecord(long entryId)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			lock (storage.gate)
			{
				return storage.Records.TryGetValue(entryId, out var rec) ? rec : null;
			}
		}

		internal override IEnumerable<(long EntryId, string SerializedParameters)> ReadRecordsForSchema(string schemaName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			lock (storage.gate)
			{
				var copy = new List<(long, string)>();
				foreach (var kvp in storage.Records)
				{
					if (kvp.Value.SchemaName == schemaName)
						copy.Add((kvp.Key, kvp.Value.SerializedParameters));
				}
				return copy;
			}
		}

		internal override void ReadRecordsAfter(long afterEntryId, List<PlaybillRecord> result)
		{
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();
			lock (storage.gate)
			{
				foreach (var kvp in storage.Records)
				{
					if (kvp.Key <= afterEntryId) continue;
					result.Add(new PlaybillRecord(kvp.Key, kvp.Value.SchemaName, kvp.Value.SerializedParameters));
				}
			}
		}

		// Distill autonomo: en InMemory necesitamos consultar el journal del
		// actor (que vive en DiaryStorageInMemory.sharedStorages bajo el mismo
		// actorName). Acceso por API publica del DiaryStorageInMemory no esta
		// disponible, pero como ambos viven en el mismo namespace y son
		// internal, podemos hacer una consulta directa. Pragmatica: si en el
		// futuro la integracion necesita ser mas formal, se introduce un
		// IJournalLiveSet abstraction — por ahora la asimetria se acepta.
		internal override void Distill()
		{
			// Obtener set de EntryIds vivos en el journal del actor.
			HashSet<long> aliveJournalEntries = JournalAlive();

			lock (storage.gate)
			{
				var orphans = new List<long>();
				foreach (var kvp in storage.Records)
				{
					if (!aliveJournalEntries.Contains(kvp.Key))
						orphans.Add(kvp.Key);
				}
				foreach (var id in orphans)
				{
					storage.Records.Remove(id);
				}
			}
		}

		// Helper: peek into the actor's journal InMemory storage to get alive EntryIds.
		// Coupling pragmatico: ambos storages viven en el mismo proceso para tests,
		// asi que la consulta es directa al dict shared de DiaryStorageInMemory.
		private HashSet<long> JournalAlive()
		{
			var alive = new HashSet<long>();
			// Cross-storage peek via internal static getter del DiaryStorageInMemory.
			var events = DiaryStorageInMemory.PeekEventsForActor(ActorName);
			if (events == null) return alive;
			foreach (var evt in events)
			{
				alive.Add(evt.EntryId);
			}
			return alive;
		}

		internal override MemoryStream Archive(DateTime startDate, DateTime endDate)
		{
			throw new NotImplementedException("Archive not supported in InMemory storage.");
		}
	}
}
