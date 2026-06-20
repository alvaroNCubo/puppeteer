using System;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.DB
{
	internal abstract class EventData
	{
		private readonly EventDataPool pool;
		internal long EntryId { get; set; }
		internal DateTime OccurredAt { get; set; }
		internal string ExposeData { get; set; }


		protected EventData(EventDataPool pool)
		{
			ArgumentNullException.ThrowIfNull(pool);

			this.pool = pool;
		}

		internal void ReturnToEventDataPool()
		{
			pool.Return(this);
		}
	}

	internal sealed class ScriptEventData : EventData
	{
		internal string Script { get; set; }

		internal ScriptEventData(EventDataPool pool) : base(pool) { }
	}

	internal sealed class ActionEventData : EventData
	{
		internal int ActionId { get; set; }
		internal string Arguments { get; set; }

		internal ActionEventData(EventDataPool pool) : base(pool) { }
	}

	// Phase 3 of the Action refactor (project_puppeteer_action_refactor_plan.md):
	// third polymorphic event type for the Define statement materialised in the
	// journal. Serialised representation in SQL backends is a row with both
	// `script` (the canonical DSL sentence `define action <id> (...) as ... end;`)
	// and `action` (actionId) populated. In InMemory and FileSystem the polymorphic
	// type carries the same payload directly. Phase 4 flips ActorHandler to emit
	// these for the first invocation; Phase 5 wires them into RehydrateFromEvent.
	internal sealed class DefineEventData : EventData
	{
		internal int ActionId { get; set; }
		internal string DefineStatementText { get; set; }

		internal DefineEventData(EventDataPool pool) : base(pool) { }
	}

	internal sealed class EventDataPool
	{
		private readonly Queue<ScriptEventData> legacyPool = new();
		private readonly Queue<ActionEventData> actionPool = new();
		private readonly Queue<DefineEventData> definePool = new();
		// Paper 5 Lab 1: the JournalReader thread rents while the rehydration
		// pipeline tasks Return concurrently — Queue<T> is not thread-safe,
		// producing intermittent NullReferenceException at higher event counts
		// (~300+ events). One lock per pool serialises rent/return for that
		// kind; the contention is irrelevant compared to the disk I/O cost of
		// reading the journal.
		private readonly object legacyGate = new();
		private readonly object actionGate = new();
		private readonly object defineGate = new();
		private readonly int maxSize;
		private int legacyCount;
		private int actionCount;
		private int defineCount;

		internal EventDataPool(int maxSize = 1024)
		{
			if (maxSize <= 0) throw new LanguageException("The maximum size of the EventData pool must be greater than zero.");
			this.maxSize = maxSize;
		}

		internal ScriptEventData RentScript()
		{
			lock (legacyGate)
			{
				if (legacyPool.Count > 0)
				{
					return legacyPool.Dequeue();
				}
				legacyCount++;
				return new ScriptEventData(this);
			}
		}

		internal ActionEventData RentAction()
		{
			lock (actionGate)
			{
				if (actionPool.Count > 0)
				{
					return actionPool.Dequeue();
				}
				actionCount++;
				return new ActionEventData(this);
			}
		}

		internal DefineEventData RentDefine()
		{
			lock (defineGate)
			{
				if (definePool.Count > 0)
				{
					return definePool.Dequeue();
				}
				defineCount++;
				return new DefineEventData(this);
			}
		}

		internal void Return(EventData eventData)
		{
			eventData.EntryId = 0;
			eventData.OccurredAt = default;
			eventData.ExposeData = null;

			switch (eventData)
			{
				case ScriptEventData legacy:
					legacy.Script = null;
					lock (legacyGate)
					{
						if (legacyCount <= maxSize)
							legacyPool.Enqueue(legacy);
						else
							legacyCount--;
					}
					break;
				case ActionEventData action:
					action.ActionId = 0;
					action.Arguments = null;
					lock (actionGate)
					{
						if (actionCount <= maxSize)
							actionPool.Enqueue(action);
						else
							actionCount--;
					}
					break;
				case DefineEventData define:
					define.ActionId = 0;
					define.DefineStatementText = null;
					lock (defineGate)
					{
						if (defineCount <= maxSize)
							definePool.Enqueue(define);
						else
							defineCount--;
					}
					break;
			}
		}
	}
}
