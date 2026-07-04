using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Choreography.Transport.Brokered
{
	// SAME-PROCESS IMessageBroker for tests and single-process harnesses of the
	// full tell -> broker -> receiver -> ack flow without a live broker. Records
	// produced to a topic are delivered synchronously, in order, to every
	// subscriber of that topic, and are retained so a subscription created after
	// the produce still sees them (mirrors a broker with a retained log, which
	// keeps the round-trip deterministic in a unit test).
	//
	// It does NOT cross the process boundary — sender and receiver must be in the
	// same process. For two separately-launched processes on one dev box use
	// LoopbackBroker (TCP loopback, RAM-only). It does not reorder, does not retry,
	// and adds no latency. It is NOT a model of broker durability or partitioning —
	// those concerns belong to the production ConfluentKafkaBroker.
	public sealed class InProcessBroker : IMessageBroker
	{
		private sealed class TopicState
		{
			internal readonly List<BrokerRecord> Retained = new List<BrokerRecord>();
			internal readonly List<Action<BrokerRecord>> Subscribers = new List<Action<BrokerRecord>>();
			internal readonly object Gate = new object();
		}

		private readonly ConcurrentDictionary<string, TopicState> topics =
			new ConcurrentDictionary<string, TopicState>(StringComparer.Ordinal);

		private TopicState StateOf(string topic) => topics.GetOrAdd(topic, _ => new TopicState());

		public Task ProduceAsync(string topic, string key, IReadOnlyDictionary<string, string> headers, string value, CancellationToken cancellationToken = default)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(topic);
			ArgumentNullException.ThrowIfNull(value);
			cancellationToken.ThrowIfCancellationRequested();

			// Defensive copy: headers are immutable once on the wire.
			IReadOnlyDictionary<string, string> headerSnapshot =
				headers == null
					? new Dictionary<string, string>(0)
					: new Dictionary<string, string>(headers, StringComparer.Ordinal);

			BrokerRecord record = new BrokerRecord(topic, key, headerSnapshot, value);

			TopicState state = StateOf(topic);
			Action<BrokerRecord>[] subscribers;
			lock (state.Gate)
			{
				state.Retained.Add(record);
				subscribers = state.Subscribers.ToArray();
			}

			foreach (Action<BrokerRecord> subscriber in subscribers)
			{
				DeliverSafely(subscriber, record);
			}

			return Task.CompletedTask;
		}

		public IDisposable Subscribe(string topic, Action<BrokerRecord> onRecord)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(topic);
			ArgumentNullException.ThrowIfNull(onRecord);

			TopicState state = StateOf(topic);
			BrokerRecord[] backlog;
			lock (state.Gate)
			{
				backlog = state.Retained.ToArray();
				state.Subscribers.Add(onRecord);
			}

			// Replay the retained log so a late subscriber still observes records
			// produced before it joined — the deterministic loopback behaviour the
			// E2E tests rely on.
			foreach (BrokerRecord record in backlog)
			{
				DeliverSafely(onRecord, record);
			}

			return new Subscription(state, onRecord);
		}

		private static void DeliverSafely(Action<BrokerRecord> subscriber, BrokerRecord record)
		{
			try
			{
				subscriber(record);
			}
			catch (Exception ex)
			{
				// A subscriber that throws must not tear down the loopback — the
				// real broker would keep delivering to other consumers too.
				System.Diagnostics.Debug.WriteLine($"[InProcessBroker] subscriber threw for topic '{record.Topic}': {ex.Message}");
			}
		}

		private sealed class Subscription : IDisposable
		{
			private readonly TopicState state;
			private readonly Action<BrokerRecord> handler;
			private bool disposed;

			internal Subscription(TopicState state, Action<BrokerRecord> handler)
			{
				this.state = state;
				this.handler = handler;
			}

			public void Dispose()
			{
				if (disposed) return;
				disposed = true;
				lock (state.Gate)
				{
					state.Subscribers.Remove(handler);
				}
			}
		}
	}
}
