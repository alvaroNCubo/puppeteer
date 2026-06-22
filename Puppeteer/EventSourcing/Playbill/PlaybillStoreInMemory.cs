using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Puppeteer.EventSourcing.DB;

namespace Puppeteer.EventSourcing.Playbill
{
	// In-memory Playbill backend. Tests-only by construction (parallel to
	// DiaryStorageInMemory). Shared storage by actor name — multiple
	// instances of the same actor share the same storage dict so the
	// "build Performance several times pointing at the same actor in
	// tests" pattern works consistently.
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

		// Test seam: clears the shared storage for an actor. Only for tests
		// that want isolation between cases.
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

		// Autonomous Distill: in InMemory we need to query the actor's journal
		// (which lives in DiaryStorageInMemory.sharedStorages under the same
		// actorName). Access via a public API of DiaryStorageInMemory is not
		// available, but since both live in the same namespace and are
		// internal, we can do a direct query. Pragmatic: if in the
		// future the integration needs to be more formal, an
		// IJournalLiveSet abstraction is introduced — for now the asymmetry is accepted.
		internal override void Distill()
		{
			// Get the set of live EntryIds in the actor's journal.
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
		// Pragmatic coupling: both storages live in the same process for tests,
		// so the query goes directly to DiaryStorageInMemory's shared dict.
		private HashSet<long> JournalAlive()
		{
			var alive = new HashSet<long>();
			// Cross-storage peek via DiaryStorageInMemory's internal static getter.
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
