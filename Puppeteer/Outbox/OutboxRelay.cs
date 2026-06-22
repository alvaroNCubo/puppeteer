using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.DB;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Delivery side of the journal-outbox emit mode. The recording side
	// (`.Outbox.Emit(...)`) writes rows atomically with the reaction cursor; this
	// relay reads recorded-but-undelivered rows and pushes them to an IOutboxSink,
	// then marks each delivered. Host-driven (pull model, like the Materialize
	// mirror) — call Dispatch on whatever cadence the host wants, or once on
	// restart to drain the backlog a crash left behind.
	//
	// At-least-once delivery: if Send throws (or the process dies) after the sink
	// accepted a message but before MarkDelivered runs, the row stays undelivered
	// and the NEXT Dispatch redelivers it with the same OutboxMessage.IdempotencyKey.
	// Exactly-once EFFECT therefore requires the sink/consumer to dedup on that key
	// (residual requirement — see notes/reactions-outbox-emit.md).
	public sealed class OutboxRelay
	{
		private readonly ActorHandler handler;
		private OutboxStorage testStorageOverride;

		internal OutboxRelay(ActorHandler handler)
		{
			ArgumentNullException.ThrowIfNull(handler);
			this.handler = handler;
		}

		// Test seam parallel to Materialization.SetCheckpointStorage — wire the
		// relay to an in-memory storage without going through EventSourcingStorage.
		internal void SetOutboxStorage(OutboxStorage storage)
		{
			ArgumentNullException.ThrowIfNull(storage);
			this.testStorageOverride = storage;
		}

		private OutboxStorage Storage
		{
			get
			{
				if (testStorageOverride != null) return testStorageOverride;
				OutboxStorage storage = handler.TryGetDiaryStorage()?.OutboxStorage;
				if (storage == null)
					throw new LanguageException("Outbox requires EventSourcingStorage to be configured on the actor before the relay can run.");
				return storage;
			}
		}

		// Deliver every recorded-but-undelivered message to the sink, in recording
		// order, marking each delivered only after Send returns. Returns the number
		// of rows delivered in this pass. If Send throws, the row is left
		// undelivered and the exception propagates — the caller decides whether to
		// retry (a later Dispatch picks up where this one stopped).
		public int Dispatch(IOutboxSink sink)
		{
			ArgumentNullException.ThrowIfNull(sink);

			var pending = new List<OutboxRecord>();
			Storage.ReadUndelivered(pending);

			int delivered = 0;
			foreach (var row in pending)
			{
				sink.Send(row.ToMessage());
				// Reached only if Send did not throw. A crash between these two
				// lines is the at-least-once window: the row stays undelivered and
				// is redelivered next time.
				Storage.MarkDelivered(row.OutboxId, DateTime.Now);
				delivered++;
			}
			return delivered;
		}

		// Recorded-but-undelivered backlog, in recording order. For inspection and
		// tests; Dispatch is the delivery path.
		public IReadOnlyList<OutboxMessage> Pending()
		{
			var pending = new List<OutboxRecord>();
			Storage.ReadUndelivered(pending);
			var result = new List<OutboxMessage>(pending.Count);
			foreach (var row in pending)
				result.Add(row.ToMessage());
			return result;
		}

		public int PendingCount => Storage.PendingCount;
	}
}
