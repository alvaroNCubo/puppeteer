using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	internal class DiaryStorageInMemory : DiaryStorage
	{
		private static readonly Dictionary<string, SharedStorageData> sharedStorages = new Dictionary<string, SharedStorageData>();
		private static readonly object sharedStoragesLock = new object();

		private class SharedStorageData
		{
			public List<EventData> Events = new List<EventData>();
			// Phase 6 of the Action refactor: dropped the lateral Actions dict.
			// The journal is the catalog now (Define entries replay populates the
			// actor cache directly via AddKnownActionFromDefine).
			public Dictionary<int, long> FollowerCheckpoints = new Dictionary<int, long>();
			public Dictionary<string, long> ReactionRegistry = new Dictionary<string, long>();
			public Dictionary<(long, int), (long detected, long confirmed)> ReactionCheckpoints = new Dictionary<(long, int), (long, long)>();
			public long NextEntryId = 1;
			public long NextReactionId = 1;
			public EventElisionStorageInMemory EventElisionStorage;
			public EventMaterializationStorageInMemory EventMaterializationStorage;
			public MaterializationCheckpointStorageInMemory MaterializationCheckpointStorage;
		}

		private readonly SharedStorageData storage;
		private readonly List<EventData> events;
		private readonly Dictionary<int, long> followerCheckpoints;
		private readonly Dictionary<string, long> reactionRegistry;
		private readonly Dictionary<(long, int), (long detected, long confirmed)> reactionCheckpoints;
		private ref long nextEntryId => ref storage.NextEntryId;
		private ref long nextReactionId => ref storage.NextReactionId;

		internal DiaryStorageInMemory(IActorEventJournalClient eventJournalClient)
			: base(eventJournalClient, "InMemory")
		{
			string actorName = eventJournalClient.ActorName;

			lock (sharedStoragesLock)
			{
				if (!sharedStorages.TryGetValue(actorName, out storage))
				{
					storage = new SharedStorageData();
					storage.EventElisionStorage = new EventElisionStorageInMemory(eventJournalClient);
					storage.EventMaterializationStorage = new EventMaterializationStorageInMemory(eventJournalClient);
					storage.MaterializationCheckpointStorage = new MaterializationCheckpointStorageInMemory(eventJournalClient);
					sharedStorages[actorName] = storage;
				}
			}

			events = storage.Events;
			followerCheckpoints = storage.FollowerCheckpoints;
			reactionRegistry = storage.ReactionRegistry;
			reactionCheckpoints = storage.ReactionCheckpoints;
			eventElisionStorage = storage.EventElisionStorage;
			eventMaterializationStorage = storage.EventMaterializationStorage;
			materializationCheckpointStorage = storage.MaterializationCheckpointStorage;
		}
		internal void AddScriptEvent(string script, string ip = "127.0.0.1", string user = "TestUser", DateTime? occurredAt = null, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(script);

			var scriptData = EventDataPool.RentScript();
			scriptData.EntryId = nextEntryId++;
			scriptData.Script = script;
			scriptData.Ip = ip;
			scriptData.User = user;
			scriptData.OccurredAt = occurredAt ?? DateTime.Now;
			scriptData.ExposeData = exposeData;

			events.Add(scriptData);
		}
		internal void AddActionEvent(int actionId, string arguments, string ip = "127.0.0.1", string user = "TestUser", DateTime? occurredAt = null, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(arguments);

			var actionData = EventDataPool.RentAction();
			actionData.EntryId = nextEntryId++;
			actionData.ActionId = actionId;
			actionData.Arguments = arguments;
			actionData.Ip = ip;
			actionData.User = user;
			actionData.OccurredAt = occurredAt ?? DateTime.Now;
			actionData.ExposeData = exposeData;

			events.Add(actionData);
		}
		// Phase 6 of the Action refactor: RegisterAction is gone (the lateral
		// actions dict is gone). AddActionEventWithRegistration survives as a
		// test seam — same signature so existing tests don't churn — and is now
		// thin sugar over AddKnownAction + AddActionEvent. The Define journal
		// record is intentionally NOT written here: the test seam mirrors what
		// the live cutover puts on the wire only as far as the assertions need
		// (invocation event + populated action cache). Tests that specifically
		// exercise the Define replay path use WriteDefineWithFirstInvocation
		// directly. Using the legacy AddKnownAction (not AddKnownActionFromDefine)
		// keeps the test seam usable for parameter shapes the Phase 1 Define
		// parser doesn't yet accept (e.g. array types like `int[]`).
		internal void AddActionEventWithRegistration(int actionId, string script, string parametersDeclaration, string arguments, string ip = "127.0.0.1", string user = "TestUser", DateTime? occurredAt = null)
		{
			ArgumentNullException.ThrowIfNull(script);
			ArgumentNullException.ThrowIfNull(parametersDeclaration);
			ArgumentNullException.ThrowIfNull(arguments);

			if (!EventJournalClient.IsActionKnown(actionId))
			{
				EventJournalClient.AddKnownAction(actionId, script, parametersDeclaration);
			}

			AddActionEvent(actionId, arguments, ip, user, occurredAt);
		}

		internal void Clear()
		{
			foreach (var evt in events)
			{
				evt.ReturnToEventDataPool();
			}
			events.Clear();
			followerCheckpoints.Clear();
			reactionRegistry.Clear();
			reactionCheckpoints.Clear();
			nextEntryId = 1;
			nextReactionId = 1;

			if (storage.EventElisionStorage != null)
			{
				storage.EventElisionStorage.Clear();
			}

			if (storage.EventMaterializationStorage != null)
			{
				storage.EventMaterializationStorage.Clear();
			}

			if (storage.MaterializationCheckpointStorage != null)
			{
				storage.MaterializationCheckpointStorage.Clear();
			}
		}

		internal EventData GetLastEvent()
		{
			if (events.Count == 0)
			{
				throw new InvalidOperationException("There are no events in the storage.");
			}
			return events[events.Count - 1];
		}

		protected internal override long RehydrateFromEvent(long afterEntryId, bool includeExposeData = false)
		{
			return RehydrateFromEvent(afterEntryId, RehydrateDirection.Forward, includeExposeData);
		}

		protected internal override Task<long> RehydrateFromEventAsync(long afterEntryId, bool includeExposeData = false)
		{
			return RehydrateFromEventAsync(afterEntryId, RehydrateDirection.Forward, includeExposeData);
		}

		internal string GetLastExposeData()
		{
			if (events.Count == 0)
			{
				throw new LanguageException("There are no events in the storage.");
			}
			return events[events.Count - 1].ExposeData;
		}

		internal int GetEventCount()
		{
			return events.Count;
		}

		internal EventData GetEvent(int index)
		{
			return events[index];
		}

		// Phase 6 of the Action refactor: dropped WriteActionEntry +
		// WriteNewActionEntry overrides. Use WriteInvocationEntry / WriteDefineEntry /
		// WriteDefineWithFirstInvocation.

		protected internal override void WriteScriptEntry(long entryId, string script, string ip, string user, DateTime now, string exposeData = null)
		{
			AddScriptEvent(script, ip, user, now, exposeData);
		}

		protected internal override Task WriteScriptEntryAsync(long entryId, string script, string ip, string user, DateTime now, string exposeData = null)
		{
			WriteScriptEntry(entryId, script, ip, user, now, exposeData);
			return Task.CompletedTask;
		}

		// Phase 3 of the Action refactor: new write APIs for the post-cutover path.
		// WriteDefineEntry materialises a Define statement directly in the journal
		// (no lateral _ACTION-equivalent registration); WriteInvocationEntry is the
		// invocation-only counterpart. Phase 4 flips the live caller and split
		// model firmado: Define entries carry no arguments — first invocation lives
		// in a separate Invocation entry written immediately after, so MarkAsSkip
		// on a first invocation cannot collaterally erase the Define.
		protected internal override void WriteDefineEntry(int actionId, string defineStatementText, long entryId, string ip, string user, DateTime now, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);

			var defineData = EventDataPool.RentDefine();
			defineData.EntryId = nextEntryId++;
			defineData.ActionId = actionId;
			defineData.DefineStatementText = defineStatementText;
			defineData.Ip = ip;
			defineData.User = user;
			defineData.OccurredAt = now;
			defineData.ExposeData = exposeData;

			events.Add(defineData);
		}

		protected internal override Task WriteDefineEntryAsync(int actionId, string defineStatementText, long entryId, string ip, string user, DateTime now, string exposeData = null)
		{
			WriteDefineEntry(actionId, defineStatementText, entryId, ip, user, now, exposeData);
			return Task.CompletedTask;
		}

		protected internal override void WriteInvocationEntry(int actionId, long entryId, string ip, string user, DateTime now, string arguments, string exposeData = null)
		{
			AddActionEvent(actionId, arguments, ip, user, now, exposeData);
		}

		protected internal override Task WriteInvocationEntryAsync(int actionId, long entryId, string ip, string user, DateTime now, string arguments, string exposeData = null)
		{
			WriteInvocationEntry(actionId, entryId, ip, user, now, arguments, exposeData);
			return Task.CompletedTask;
		}

		// Phase 4 atomic write — see DiaryStorage.cs for the contract. InMemory's
		// "atomic" guarantee is trivial: both Add calls happen sequentially with
		// no intermediate observers (the events list is mutated under the
		// SharedStorageData instance lock implicit in AppendAction usage; tests
		// rely on single-threaded semantics within a test method).
		protected internal override void WriteDefineWithFirstInvocation(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, string ip, string user, DateTime now, string arguments, string exposeData = null)
		{
			ArgumentNullException.ThrowIfNull(defineStatementText);
			ArgumentNullException.ThrowIfNull(arguments);

			var defineData = EventDataPool.RentDefine();
			defineData.EntryId = nextEntryId++;
			defineData.ActionId = actionId;
			defineData.DefineStatementText = defineStatementText;
			defineData.Ip = ip;
			defineData.User = user;
			defineData.OccurredAt = now;
			defineData.ExposeData = null; // expose data lives on the Invocation (it's the result of running the action body, not of declaring it).
			events.Add(defineData);

			var actionData = EventDataPool.RentAction();
			actionData.EntryId = nextEntryId++;
			actionData.ActionId = actionId;
			actionData.Arguments = arguments;
			actionData.Ip = ip;
			actionData.User = user;
			actionData.OccurredAt = now;
			actionData.ExposeData = exposeData;
			events.Add(actionData);
		}

		protected internal override Task WriteDefineWithFirstInvocationAsync(int actionId, string defineStatementText, long defineEntryId, long invocationEntryId, string ip, string user, DateTime now, string arguments, string exposeData = null)
		{
			WriteDefineWithFirstInvocation(actionId, defineStatementText, defineEntryId, invocationEntryId, ip, user, now, arguments, exposeData);
			return Task.CompletedTask;
		}

		protected internal override long GetLastProcessedEntryId(int followerId)
		{
			return followerCheckpoints.TryGetValue(followerId, out long entryId) ? entryId : 0;
		}

		protected internal override void SaveLastProcessedEntryId(int followerId, long entryId)
		{
			followerCheckpoints[followerId] = entryId;
		}

		// Paper 5 / Materialize v2 — Fase 2: read raw records (sin filtrar elided).
		// Itera la lista shared in-order y proyecta cada EventData a un
		// MaterializationRecord public segun su kind. Defensiva: copia los campos
		// porque los EventData en la lista son owned by SharedStorageData (no por
		// el caller).
		protected internal override void ReadRecordsAfter(long afterEntryId, List<MaterializationRecord> result)
		{
			ArgumentNullException.ThrowIfNull(result);
			if (afterEntryId < 0) throw new LanguageException($"afterEntryId {afterEntryId} must be zero or greater.");

			result.Clear();

			lock (sharedStoragesLock)
			{
				foreach (var evt in events)
				{
					if (evt.EntryId <= afterEntryId) continue;
					result.Add(ProjectToRecord(evt));
				}
			}

			result.Sort((a, b) => a.EntryId.CompareTo(b.EntryId));
		}

		protected internal override Task ReadRecordsAfterAsync(long afterEntryId, List<MaterializationRecord> result)
		{
			ReadRecordsAfter(afterEntryId, result);
			return Task.CompletedTask;
		}

		// Materialize v2 / Fase 3 — wire verb (c) DameCheckpointsHasta:
		// snapshot atomic del reaction registry + checkpoints.
		protected internal override void ReadReactionRegistry(List<MaterializationReactionDefinition> result)
		{
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			lock (sharedStoragesLock)
			{
				foreach (var kvp in reactionRegistry)
				{
					result.Add(new MaterializationReactionDefinition(kvp.Value, kvp.Key));
				}
			}

			result.Sort((a, b) => a.ReactionId.CompareTo(b.ReactionId));
		}

		protected internal override void ReadReactionCheckpoints(List<MaterializationReactionCheckpoint> result)
		{
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();

			lock (sharedStoragesLock)
			{
				foreach (var kvp in reactionCheckpoints)
				{
					result.Add(new MaterializationReactionCheckpoint(
						reactionId: kvp.Key.Item1,
						seekLevel: kvp.Key.Item2,
						detected: kvp.Value.detected,
						confirmed: kvp.Value.confirmed));
				}
			}

			result.Sort((a, b) =>
			{
				int cmp = a.ReactionId.CompareTo(b.ReactionId);
				if (cmp != 0) return cmp;
				return a.SeekLevel.CompareTo(b.SeekLevel);
			});
		}

		private static MaterializationRecord ProjectToRecord(EventData evt)
		{
			if (evt is ScriptEventData scriptEvt)
			{
				return new MaterializationRecord(
					entryId: evt.EntryId,
					kind: MaterializationRecordKind.Script,
					occurredAt: evt.OccurredAt,
					ip: evt.Ip,
					user: evt.User,
					script: scriptEvt.Script,
					actionId: 0,
					arguments: null,
					defineStatementText: null,
					exposeData: evt.ExposeData);
			}
			if (evt is ActionEventData actionEvt)
			{
				return new MaterializationRecord(
					entryId: evt.EntryId,
					kind: MaterializationRecordKind.Invocation,
					occurredAt: evt.OccurredAt,
					ip: evt.Ip,
					user: evt.User,
					script: null,
					actionId: actionEvt.ActionId,
					arguments: actionEvt.Arguments,
					defineStatementText: null,
					exposeData: evt.ExposeData);
			}
			if (evt is DefineEventData defineEvt)
			{
				return new MaterializationRecord(
					entryId: evt.EntryId,
					kind: MaterializationRecordKind.Define,
					occurredAt: evt.OccurredAt,
					ip: evt.Ip,
					user: evt.User,
					script: null,
					actionId: defineEvt.ActionId,
					arguments: null,
					defineStatementText: defineEvt.DefineStatementText,
					exposeData: evt.ExposeData);
			}
			throw new LanguageException($"Unknown EventData type: {evt.GetType().Name}");
		}

		protected internal override MemoryStream Archive(DateTime fechaInicio, DateTime fechaFin)
		{
			throw new NotImplementedException("Archive not supported in InMemory storage.");
		}

		protected internal override IEnumerable<string> ListActorNames(string name)
		{
			throw new NotImplementedException("Actor enumeration not supported in InMemory storage.");
		}

		protected internal override void Trim(DateTime trimmedDown)
		{
			throw new NotImplementedException("Trim not supported in InMemory storage.");
		}

		// Distill: ver contrato en DiaryStorage.cs. InMemory reescribe la lista de
		// eventos en sitio bajo el lock compartido, respetando la invariante "ultimo
		// record no se elimina fisicamente aunque su elision logica lo marque".
		protected internal override void Distill()
		{
			lock (sharedStoragesLock)
			{
				if (events.Count == 0) return;

				// "Ultimo record" = el de mayor EntryId. Es la unica granularidad de
				// preservacion: el resto de elididos se materializa.
				long lastEntryId = 0;
				for (int i = 0; i < events.Count; i++)
				{
					if (events[i].EntryId > lastEntryId) lastEntryId = events[i].EntryId;
				}

				var survivors = new List<EventData>(events.Count);
				var removed = new List<long>();
				foreach (var evt in events)
				{
					bool isLastRecord = evt.EntryId == lastEntryId;
					bool isElided = eventElisionStorage.IsEventElided(evt.EntryId);

					if (isLastRecord || !isElided)
					{
						survivors.Add(evt);
					}
					else
					{
						removed.Add(evt.EntryId);
						evt.ReturnToEventDataPool();
					}
				}

				if (removed.Count == 0) return;

				events.Clear();
				events.AddRange(survivors);

				// Limpiar el registro de elisiones: los IDs removidos ya no existen
				// fisicamente, dejan de ser "elididos" para volverse "ausentes".
				storage.EventElisionStorage.RemoveElidedIds(removed);
			}
		}

		internal override void ChangePrimaryKey()
		{
			throw new NotImplementedException("Primary key change not supported in InMemory storage.");
		}

		protected internal override long RehydrateFromEvent(long afterEntryId, RehydrateDirection direction, bool includeExposeData = false)
		{
			EventJournalClient.IsNew = events.Count == 0;

			// Phase 6 of the Action refactor: the lateral `actions` dict is
			// gone. Define entries in the events list populate the action cache
			// via AddKnownActionFromDefine in entry-id order — by construction
			// Define precedes any Invocation that references it.

			long lastEntryId = afterEntryId;
			bool forcedToEnd = false;
			bool canContinueReplay = false;
			bool firstPassCompleted = false; // Local flag to prevent an infinite loop in BATCH mode.

			// Outer loop for CONTINUOUS mode (mirrors SQLServer/MySQL behaviour).
			// In BATCH mode: terminate after processing every event once (firstPassCompleted).
			// In CONTINUOUS mode: CanContinueReplay() returns true until a shutdown signal arrives.
			while (!forcedToEnd && !firstPassCompleted && (canContinueReplay = EventJournalClient.CanContinueReplay(lastEntryId)))
			{
				// Sort events according to direction.
				IEnumerable<EventData> orderedEvents;
				if (direction == RehydrateDirection.Forward)
				{
					orderedEvents = events.Where(evt => evt.EntryId > lastEntryId).OrderBy(evt => evt.EntryId);
				}
				else if (direction == RehydrateDirection.Backward)
				{
					orderedEvents = events.Where(evt => evt.EntryId > lastEntryId).OrderByDescending(evt => evt.EntryId);
				}
				else
				{
					throw new ArgumentException($"Unknown search direction '{direction}'.", nameof(direction));
				}

				// Compute how many events still need to be processed.
				int eventCount = orderedEvents.Count();

				EventJournalClient.BeginJournalReplay(eventCount);

				// Replay de eventos en el orden especificado
				foreach (var evt in orderedEvents)
				{
					if (!EventJournalClient.CanContinueReplay(lastEntryId))
					{
						forcedToEnd = true;
						break;
					}

					// Skip events that have been marked as elided (MarkAsSkip).
					// These events were part of complete patterns detected by Reactions
					// and can be omitted during rehydration without affecting the final state.
					if (eventElisionStorage.IsEventElided(evt.EntryId))
					{
						lastEntryId = evt.EntryId;
						continue;
					}

					// Phase 4 of the Action refactor: process DefineEventData by parsing
					// its canonical sentence and populating the actionCommands cache.
					// The lateral `actions` dictionary is no longer the source of truth —
					// the journal is. (Phase 5 drops the lateral dict entirely; Phase 4
					// keeps it cohabiting and unread.)
					if (evt is DefineEventData defineEvt)
					{
						EventJournalClient.AddKnownActionFromDefine(defineEvt.ActionId, defineEvt.DefineStatementText);
						lastEntryId = evt.EntryId;
						continue;
					}

					// Deep copy of the event for rehydration.
					// Required because EventJournalClient.ReplayEvent() returns the event to the pool after processing it.
					// If we passed evt directly, our permanent storage would lose the data.
					EventData tempEvent;
					if (evt is ScriptEventData scriptEvt)
					{
						tempEvent = EventDataPool.RentScript();
						((ScriptEventData)tempEvent).Script = scriptEvt.Script;
					}
					else if (evt is ActionEventData actionEvt)
					{
						tempEvent = EventDataPool.RentAction();
						((ActionEventData)tempEvent).ActionId = actionEvt.ActionId;
						((ActionEventData)tempEvent).Arguments = actionEvt.Arguments;
					}
					else
					{
						throw new LanguageException($"Unknown event type: {evt.GetType().Name}");
					}

					tempEvent.EntryId = evt.EntryId;
					tempEvent.Ip = evt.Ip;
					tempEvent.User = evt.User;
					tempEvent.OccurredAt = evt.OccurredAt;
					tempEvent.ExposeData = evt.ExposeData;

					EventJournalClient.ReplayEvent(tempEvent);
					lastEntryId = evt.EntryId;
				}

				// If there are no more new events.
				if (eventCount == 0)
				{
					// Mark that the first pass completed (in BATCH mode this exits the loop).
					firstPassCompleted = true;

					// In CONTINUOUS mode: sleep and continue.
					// In BATCH mode the while (!firstPassCompleted) condition will exit the loop.
					if (!EventJournalClient.CanContinueReplay(lastEntryId))
					{
						System.Threading.Thread.Sleep(100); // CONTINUOUS mode only.
					}
					else
					{

						break; // Shutdown requested.
					}
				}
			}

			EventJournalClient.EndJournalReplay(forcedToEnd);

			return lastEntryId;
		}

		protected internal override Task<long> RehydrateFromEventAsync(long afterEntryId, RehydrateDirection direction, bool includeExposeData = false)
		{
			return Task.FromResult(RehydrateFromEvent(afterEntryId, direction, includeExposeData));
		}

		protected internal override long GetOrCreateReactionId(string formattedReaction)
		{
			ArgumentNullException.ThrowIfNull(formattedReaction);

			if (reactionRegistry.TryGetValue(formattedReaction, out long existingId))
			{
				return existingId;
			}

			// Assign a new id.
			long newId = nextReactionId++;
			reactionRegistry[formattedReaction] = newId;
			return newId;
		}

		// Phase 5A: two-phase checkpoint — returns the (detected, confirmed) tuple in a single access.
		protected internal override (long detected, long confirmed) GetReactionCheckpoint(long reactionId, int seekLevel)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException("seekLevel must be zero or greater.");

			if (reactionCheckpoints.TryGetValue((reactionId, seekLevel), out var checkpoint))
			{
				return checkpoint; // Return the (detected, confirmed) tuple.
			}

			return (0, 0); // Checkpoint not found — return zero for both.
		}

		// PHASE 5A: only save Confirmed after PerformCommand executes successfully.
		protected internal override void SaveReactionConfirmedCheckpoint(long reactionId, int seekLevel, long entryId)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException("seekLevel must be zero or greater.");

			// Read the current checkpoint or create a new one.
			var (detected, _) = GetReactionCheckpoint(reactionId, seekLevel);

			// Save with the updated Confirmed, keeping Detected.
			reactionCheckpoints[(reactionId, seekLevel)] = (detected, entryId);
		}

		// DEPRECATED: kept for backwards compatibility, returns Detected.
		protected internal override long GetReactionLastProcessedEntryId(long reactionId, int pattern)
		{
			var (detected, _) = GetReactionCheckpoint(reactionId, pattern);
			return detected; // Return only Detected for backwards compatibility.
		}

		// DEPRECATED: kept for backwards compatibility, saves both (detected = confirmed = entryId).
		protected internal override void SaveReactionLastProcessedEntryId(long reactionId, int pattern, long entryId)
		{
			if (pattern < 0) throw new LanguageException("pattern must be zero or greater.");
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			// For backwards compatibility, save the same value in both (detected and confirmed).
			reactionCheckpoints[(reactionId, pattern)] = (entryId, entryId);
		}

		protected internal override long? NextNonElided(long entryId, RehydrateDirection direction)
		{
			if (entryId < 0) throw new LanguageException("entryId must be zero or greater.");

			if (direction == RehydrateDirection.Forward)
			{
				for (long id = entryId + 1; id < nextEntryId; id++)
				{
					if (!eventElisionStorage.IsEventElided(id) && events.Any(e => e.EntryId == id))
					{
						return id;
					}
				}
			}
			else if (direction == RehydrateDirection.Backward)
			{
				for (long id = entryId - 1; id >= 1; id--)
				{
					if (!eventElisionStorage.IsEventElided(id) && events.Any(e => e.EntryId == id))
					{
						return id;
					}
				}
			}

			return null;
		}

		protected internal override bool MarkEventsAsElidedWithCheckpoint(Follower.CheckpointCommit commit)
		{
			ArgumentNullException.ThrowIfNull(commit);

			long reactionId = commit.ReactionId;
			long[] eventIds = commit.EventIds;
			DateTime timestamp = commit.Timestamp;
			Follower.CheckpointVector newCheckpoint = commit.CheckpointVector;

			lock (reactionCheckpoints)
			{
				// Phase 5A: compare using Detected (not Confirmed).
				bool isGreater = false;
				for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
				{
					long newDetected = newCheckpoint.Get(seekLevel);
					var (currentDetected, _) = GetReactionCheckpoint(reactionId, seekLevel);

					if (newDetected > currentDetected)
					{
						isGreater = true;
						break;
					}
					else if (newDetected < currentDetected)
					{
						isGreater = false;
						break;
					}
				}

				if (!isGreater)
				{
					return false;
				}

				eventElisionStorage.MarkEventsAsElided(eventIds, (int)reactionId, timestamp);

				// Phase 5A: save ONLY Detected (Confirmed is saved after PerformCommand succeeds).
				for (int seekLevel = 0; seekLevel < newCheckpoint.SeekCount; seekLevel++)
				{
					var (_, confirmed) = GetReactionCheckpoint(reactionId, seekLevel);
					long newDetected = newCheckpoint.Get(seekLevel);
					reactionCheckpoints[(reactionId, seekLevel)] = (newDetected, confirmed);
				}

				return true;
			}
		}

		// Test helper: check whether an event is elided.
		internal bool IsElided(long entryId)
		{
			return eventElisionStorage.IsEventElided(entryId);
		}
	}
}
