using System;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class NullActorEventJournalClient : IActorEventJournalClient
	{
		private readonly string actorName;

		internal NullActorEventJournalClient(string actorName)
		{
			if (string.IsNullOrWhiteSpace(actorName)) throw new ArgumentNullException(nameof(actorName));
			this.actorName = actorName;
		}

		public string ActorName => actorName;
		public bool IsNew { set { } }

		public bool IsActionKnown(int actionId) => false;
		public void AddKnownAction(int actionId, string actionScript, string parameters) { }
		public void AddKnownActionFromDefine(int actionId, string defineStatementText) { }

		public void BeginJournalReplay(long totalEventsToApply) { }
		public bool CanContinueReplay(long currentEntryId) => true;
		public void ReplayEvent(EventData retornableEventData) { }
		public void EndJournalReplay(bool forcedToEnd) { }

		public long GetLastProcessedEntryId(int followerId) => 0;
	}
}
