namespace Puppeteer.EventSourcing.DB
{
	internal interface IActorEventJournalClient
	{
		string ActorName { get; }
		bool IsNew { set; }


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

		//string EscapeLiteralString(string input);
	}

}
