using System;
using Choreography.Input;

namespace Choreography.Transport.Brokered
{
	// Models a broker topic AS an InputSource — the input seam's broker adapter.
	// This is what lets Kafka, the InProcessBroker, and the LoopbackBroker stop
	// being special-cased inside the consume-processing: each is an IMessageBroker,
	// and ONE adapter turns any IMessageBroker subscription into the medium-agnostic
	// IInputSource the consume side (Dispatch / Saga) reads from.
	//
	//   ConfluentKafkaBroker  ─┐
	//   InProcessBroker        ├─►  BrokerInputSource (IInputSource)  ─►  Dispatch.ConsumeFrom
	//   LoopbackBroker        ─┘
	//
	// The MEDIUM (which broker, which topic) is fixed here, in the SOURCE; the
	// consume-processing never names it. Subscribe's retained-log replay (the
	// behaviour InProcessBroker and the LoopbackBroker hub both implement, and that
	// Kafka's earliest-offset group gives) flows straight through, so a late-bound
	// consumer still observes records produced before it joined.
	//
	// A BrokerRecord maps to an InputSignal field-for-field. The signal Id defaults
	// to the record Key (the broker's per-instance ordering key); a host whose
	// records carry a distinct correlation id in a header should supply an idFrom
	// selector so idempotency keys on that instead.
	public sealed class BrokerInputSource : IInputSource
	{
		private readonly IMessageBroker broker;
		private readonly string topic;
		private readonly Func<BrokerRecord, string> idFrom;
		private readonly object startLock = new object();
		private bool started;

		// broker  — any IMessageBroker (Kafka in prod; InProc/Loopback in dev/test).
		// topic   — the topic this source draws from. Also the SourceName.
		// idFrom  — optional: derive the idempotency id from a record (e.g. read a
		//           correlation header). Defaults to the record Key.
		public BrokerInputSource(IMessageBroker broker, string topic, Func<BrokerRecord, string> idFrom = null)
		{
			this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
			ArgumentException.ThrowIfNullOrWhiteSpace(topic);
			this.topic = topic;
			this.idFrom = idFrom ?? (r => r.Key);
		}

		public string SourceName => topic;

		public IDisposable Start(Action<InputSignal> onSignal)
		{
			ArgumentNullException.ThrowIfNull(onSignal);
			lock (startLock)
			{
				if (started)
					throw new InvalidOperationException($"BrokerInputSource for topic '{topic}' already started. Bind a source once; share the broker for multiple topics.");
				started = true;
			}

			return broker.Subscribe(topic, record => onSignal(ToSignal(record)));
		}

		private InputSignal ToSignal(BrokerRecord record)
		{
			string id = idFrom(record);
			return new InputSignal(id, record.Key, record.Headers, record.Value);
		}
	}
}
