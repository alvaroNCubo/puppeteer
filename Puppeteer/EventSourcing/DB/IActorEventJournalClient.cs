namespace Puppeteer.EventSourcing.DB
{
	internal interface IActorEventJournalClient
	{
		string ActorName { get; }
		bool IsNew { set; }

		// Logger per-actor expuesto al Diary/storage/JournalReader/ReplicationAgent.
		// Cada uno ya recibe IActorEventJournalClient por ctor, asi que con esta
		// extension no hay que tocar firmas ni plumbing adicional.
		IPuppeteerLogger Logger { get; }


		bool IsActionKnown(int actionId);
		void AddKnownAction(int actionId, string actionScript, string parameters);

		// Phase 4 of the Action refactor: populate the action cache from a Define
		// entry encountered during replay (canonical DSL sentence with body
		// canonicalised by the parser).
		void AddKnownActionFromDefine(int actionId, string defineStatementText);


		void BeginJournalReplay(long totalEventsToApply);
		bool CanContinueReplay(long currentEntryId);
		void ReplayEvent(EventData retornableEventData);
		void EndJournalReplay(bool forcedToEnd);


		long GetLastProcessedEntryId(int followerId);
	}

}
