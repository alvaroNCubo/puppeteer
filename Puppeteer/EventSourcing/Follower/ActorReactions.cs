using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.DB.FileSystem;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Puppeteer.EventSourcing.Follower
{
	internal class ActorReactions : IActorEventJournalClient
	{
		private readonly ActorHandler actorHandler;
		private readonly Reaction reaction;

		private ReactionExecutionMode executionMode = ReactionExecutionMode.Batch;
		private volatile bool shutdownRequested = false;
		private bool hasCompletedFirstReplay = false;

		private long lastSeenEntryId = 0;
		private int sleepMs = 50;
		private const int MAX_SLEEP_MS = 1000;

		private readonly ConcurrentQueue<(long entryId, byte[] record)> pushQueue = new ConcurrentQueue<(long, byte[])>();
		private readonly ManualResetEventSlim pushSignal = new ManualResetEventSlim(false);
		private volatile bool pushModeActive = false;

		internal ActorReactions(ActorHandler actorHandler, Reaction reaction)
		{
			ArgumentNullException.ThrowIfNull(actorHandler);
			ArgumentNullException.ThrowIfNull(reaction);

			this.actorHandler = actorHandler;
			this.reaction = reaction;
		}

		string IActorEventJournalClient.ActorName => actorHandler.Name;

		IPuppeteerLogger IActorEventJournalClient.Logger => actorHandler.Logger;

		bool IActorEventJournalClient.IsNew
		{
			set => (actorHandler as IActorEventJournalClient).IsNew = value;
		}

		bool IActorEventJournalClient.IsActionKnown(int actionId)
		{
			return (actorHandler as IActorEventJournalClient).IsActionKnown(actionId);
		}

		void IActorEventJournalClient.AddKnownAction(int actionId, string actionScript, string parameters)
		{
			(actorHandler as IActorEventJournalClient).AddKnownAction(actionId, actionScript, parameters);
		}

		void IActorEventJournalClient.AddKnownActionFromDefine(int actionId, string defineStatementText)
		{
			(actorHandler as IActorEventJournalClient).AddKnownActionFromDefine(actionId, defineStatementText);
		}

		long IActorEventJournalClient.GetLastProcessedEntryId(int followerId)
		{
			return 0;
		}

		void IActorEventJournalClient.BeginJournalReplay(long totalEventsToApply)
		{
			System.Diagnostics.Debug.WriteLine($"[ActorReactions] Begin replay for '{reaction.Name}' - {totalEventsToApply} events");
		}

		bool IActorEventJournalClient.CanContinueReplay(long currentEntryId)
		{
			if (shutdownRequested)
			{
				System.Diagnostics.Debug.WriteLine($"[ActorReactions] Shutdown requested, stopping replay");
				return false;
			}

			if (executionMode == ReactionExecutionMode.Batch)
			{
				return !hasCompletedFirstReplay;
			}

			if (currentEntryId > lastSeenEntryId)
			{
				lastSeenEntryId = currentEntryId;
				sleepMs = 50;
				return true;
			}
			else
			{
				// Signal-preemptible backoff. This is the catch-up poll that
				// RehydrateFromEvent drives before the steady-state RunPushLoop is
				// reached. When a Cue is freshly activated, a command journaled during
				// this window used to wait out the in-progress Thread.Sleep (50→…→1000ms
				// backoff) — that, not the push loop, is what dominated end-to-end Cue
				// latency. EnqueuePushEvent (the RecordWritten callback) and
				// RequestShutdown both Set pushSignal, so waiting on it instead lets a
				// newly journaled event (or a shutdown) wake the poll immediately; the
				// rehydrate re-read on the next turn then picks the event up.
				//
				// The signal is intentionally NOT Reset here: it is a level-triggered
				// "events may be pending" poke that RunPushLoop owns and clears at the
				// catch-up→push handoff. Leaving it set lets the rest of catch-up burst
				// through without re-sleeping once any write has arrived, and carries a
				// single harmless wake-up into RunPushLoop's first Wait. Delivery and
				// exactly-once are unaffected — both the rehydrate re-read and DrainQueue
				// gate on lastProcessedEntryId, so the wake is only a hint, never a
				// source of duplicate or skipped events. When no write is pending the
				// signal stays clear and Wait times out exactly like the old Thread.Sleep,
				// so historical batch catch-up and Job-mode polling are unchanged.
				bool signaled = pushSignal.Wait(sleepMs);
				if (signaled)
					sleepMs = 50;
				else
					sleepMs = Math.Min(sleepMs * 2, MAX_SLEEP_MS);
				return true;
			}
		}

		void IActorEventJournalClient.ReplayEvent(EventData eventData)
		{
			reaction.ReplayEvent(eventData);
		}

		void IActorEventJournalClient.EndJournalReplay(bool forcedToEnd)
		{
			hasCompletedFirstReplay = true;
			System.Diagnostics.Debug.WriteLine($"[ActorReactions] End replay for '{reaction.Name}' (forcedToEnd={forcedToEnd})");
		}

		internal void SetExecutionMode(ReactionExecutionMode mode)
		{
			this.executionMode = mode;
			this.hasCompletedFirstReplay = false;
			this.lastSeenEntryId = 0;
			this.sleepMs = 50;
			System.Diagnostics.Debug.WriteLine($"[ActorReactions] Execution mode set to: {mode}");
		}

		internal void RequestShutdown()
		{
			this.shutdownRequested = true;
			pushSignal.Set();
			System.Diagnostics.Debug.WriteLine($"[ActorReactions] Shutdown requested for '{reaction.Name}'");
		}

		internal void ResetReplayState()
		{
			this.hasCompletedFirstReplay = false;
			this.lastSeenEntryId = 0;
			this.sleepMs = 50;
		}

		internal void EnqueuePushEvent(long entryId, byte[] record)
		{
			pushQueue.Enqueue((entryId, record));
			pushSignal.Set();
		}

		internal void ActivatePushMode()
		{
			pushModeActive = true;
		}

		internal void RunPushLoop(EventDataPool eventDataPool)
		{
			if (eventDataPool == null) throw new ArgumentNullException(nameof(eventDataPool));

			while (!shutdownRequested)
			{
				pushSignal.Wait(TimeSpan.FromMilliseconds(MAX_SLEEP_MS));
				pushSignal.Reset();
				DrainQueue(eventDataPool);
			}

			DrainQueue(eventDataPool);

			System.Diagnostics.Debug.WriteLine($"[ActorReactions] Push loop ended for '{reaction.Name}' (queue drained)");
		}

		private void DrainQueue(EventDataPool eventDataPool)
		{
			while (pushQueue.TryDequeue(out var item))
			{
				var (entryId, record) = item;

				if (entryId <= reaction.LastProcessedEntryId)
					continue;

				int lengthPrefixSize = 4;
				int bodyLength = record.Length - lengthPrefixSize;
				byte[] body = new byte[bodyLength];
				Buffer.BlockCopy(record, lengthPrefixSize, body, 0, bodyLength);

				bool success = BinaryEventCodec.TryDecode(body, bodyLength,
					out EventRecordType eventType, out long decodedEntryId, out DateTime occurredAt,
					out string scriptOrArguments, out int actionId,
					out string exposeData);

				if (!success) continue;

				EventData eventData;
				if (eventType == EventRecordType.Script)
				{
					var scriptEvent = eventDataPool.RentScript();
					scriptEvent.EntryId = decodedEntryId;
					scriptEvent.OccurredAt = occurredAt;
					scriptEvent.Script = scriptOrArguments;
					scriptEvent.ExposeData = exposeData;
					eventData = scriptEvent;
				}
				else
				{
					var actionEvent = eventDataPool.RentAction();
					actionEvent.EntryId = decodedEntryId;
					actionEvent.OccurredAt = occurredAt;
					actionEvent.ActionId = actionId;
					actionEvent.Arguments = scriptOrArguments;
					actionEvent.ExposeData = exposeData;
					eventData = actionEvent;
				}

				reaction.ReplayEvent(eventData);
			}
		}
	}
}
