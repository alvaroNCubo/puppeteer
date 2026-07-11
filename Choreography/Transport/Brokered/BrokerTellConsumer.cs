using System;
using System.Collections.Generic;
using Choreography.Input;
using Puppeteer;

namespace Choreography.Transport.Brokered
{
	// A tell as the receiver sees it off the wire: the correlation needed to ack,
	// the message name (the sender's vocabulary), the addressee, plus the ordered
	// payload VALUES. The parameter names/types and any command template never
	// travel — the receiver already holds the command shape; only the values cross.
	// The host maps the message name to the command it owns and applies these values
	// to it. The directive is the receiver's: A asserted a fact, B decides what to do.
	public readonly record struct ReceivedTell(
		string Id,
		string MessageName,
		string Addressee,
		string AddresseeInstanceId,
		string Arguments,
		string Check,
		// W3C trace context extracted off the wire (ephemeral observability). When
		// present, the receiver opens its uptake span as a CHILD of this remote trace
		// so the sender's and receiver's work share ONE distributed trace. Null when
		// the sender injected none (tracing off). Never journaled.
		string TraceParent = null,
		string TraceState = null);

	// Receiver (B) side of a broker tell conversation: consumes the tell topic
	// through an IInputSource, hands each tell to the host's handler, and — only
	// after the handler reports the receiver command committed (returns true) —
	// emits an ack to "<topic>.acks" so the origin can close the loop. This is the
	// automation of the hand-written drain-and-ack bridge the early Tell tests
	// carried inline.
	//
	// Reconciled with the input seam: the receiver no longer calls
	// IMessageBroker.Subscribe directly — it reads from an IInputSource (the same
	// medium-agnostic seam Dispatch.ConsumeFrom uses), so the Tell receiver and the
	// Dispatch/Saga consume are now siblings over one input abstraction. The default
	// constructor still takes (broker, topic) and wraps it in a BrokerInputSource,
	// so existing hosts are unchanged; an overload accepts any IInputSource. The ack
	// is a PRODUCE (Tell's output side), so the broker reference is retained only for
	// acking, not for consuming.
	//
	// The handler is the host's: it maps the message (by topic, or its own tag) to
	// the command it already holds, applies the carried arguments to that command's
	// shape (reusing the Parameters pool — e.g. via WithParameters), and returns
	// true once the command has committed. Returning false / throwing leaves the
	// record unacked for redelivery (the receiver's own Check guards a double-apply).
	//
	// Start may be called once; a second call throws.
	public sealed class BrokerTellConsumer : IDisposable
	{
		private readonly IMessageBroker broker;
		private readonly IInputSource source;
		private readonly string ackTopic;
		private readonly IPuppeteerLogger logger;

		// Decodes the record value back into the ordered-arguments string the host
		// applies to its command. MUST be the same codec the origin's
		// BrokerTellTransport encoded with, so the payload round-trips A -> B.
		// Defaults to today's behaviour (the wire value is the arguments verbatim).
		private readonly ITellPayloadCodec codec;
		private readonly object startLock = new object();
		private IDisposable subscription;
		private bool disposed;

		// Convenience binding to a broker topic: the receiver draws from a
		// BrokerInputSource over the broker, and acks back onto the same broker.
		public BrokerTellConsumer(IMessageBroker broker, string topic, IPuppeteerLogger logger = null, ITellPayloadCodec payloadCodec = null)
		{
			this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
			ArgumentException.ThrowIfNullOrWhiteSpace(topic);
			this.ackTopic = BrokerTellWire.AckTopicOf(topic);
			this.logger = logger;
			this.source = new BrokerInputSource(broker, topic);
			this.codec = payloadCodec ?? DefaultTellPayloadCodec.Instance;
		}

		// Explicit-seam binding: consume from any IInputSource, ack onto the given
		// broker/topic. Lets a host compose a different input medium (or a decorated
		// source) while keeping Tell's ack-after-commit contract.
		public BrokerTellConsumer(IInputSource source, IMessageBroker ackBroker, string ackTopic, IPuppeteerLogger logger = null, ITellPayloadCodec payloadCodec = null)
		{
			this.source = source ?? throw new ArgumentNullException(nameof(source));
			this.broker = ackBroker ?? throw new ArgumentNullException(nameof(ackBroker));
			ArgumentException.ThrowIfNullOrWhiteSpace(ackTopic);
			this.ackTopic = ackTopic;
			this.logger = logger;
			this.codec = payloadCodec ?? DefaultTellPayloadCodec.Instance;
		}

		// Register the host's receiver. The handler applies the tell's arguments to
		// the command it owns and returns true when that command has committed (the
		// signal to ack the origin).
		public void OnReceive(Func<ReceivedTell, bool> handle)
		{
			ArgumentNullException.ThrowIfNull(handle);
			if (disposed) throw new ObjectDisposedException(nameof(BrokerTellConsumer));
			lock (startLock)
			{
				if (subscription != null)
					throw new InvalidOperationException("BrokerTellConsumer already started.");
				subscription = source.Start(signal => Handle(signal, handle));
			}
		}

		private void Handle(InputSignal signal, Func<ReceivedTell, bool> handle)
		{
			if (!IsTell(signal)) return;
			ReceivedTell received = ToReceived(signal);

			bool committed;
			try
			{
				committed = handle(received);
			}
			catch (Exception ex)
			{
				// Leave the record unacked: the origin stays pending and a real
				// broker redelivers. The receiver's own Check guards a double-apply.
				logger?.Error($"[BrokerTellConsumer] handler threw for tell '{received.Id}' on '{source.SourceName}'; not acking", ex);
				return;
			}

			if (committed) Ack(received);
		}

		private void Ack(ReceivedTell received)
		{
			Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				[BrokerTellWire.HeaderKind] = BrokerTellWire.KindAck,
				[BrokerTellWire.HeaderTellId] = received.Id,
				[BrokerTellWire.HeaderAddressee] = received.Addressee,
				[BrokerTellWire.HeaderAddresseeInstance] = received.AddresseeInstanceId
			};

			try
			{
				// Key by tell id so acks for one envelope stay ordered. Value empty —
				// the ack is carried entirely by headers.
				broker.ProduceAsync(ackTopic, received.Id, headers, string.Empty).GetAwaiter().GetResult();
			}
			catch (Exception ex)
			{
				// A lost ack only delays settlement: the origin stays pending and
				// resolves on the next recovery citation. Never fatal here.
				logger?.Error($"[BrokerTellConsumer] failed to emit ack for tell '{received.Id}' to '{ackTopic}'", ex);
			}
		}

		private static bool IsTell(InputSignal signal)
		{
			return signal.Headers != null
				&& signal.Headers.TryGetValue(BrokerTellWire.HeaderKind, out string kind)
				&& kind == BrokerTellWire.KindTell;
		}

		private ReceivedTell ToReceived(InputSignal signal)
		{
			IReadOnlyDictionary<string, string> h = signal.Headers;
			return new ReceivedTell(
				Id: Header(h, BrokerTellWire.HeaderTellId),
				MessageName: Header(h, BrokerTellWire.HeaderMessage),
				Addressee: Header(h, BrokerTellWire.HeaderAddressee),
				AddresseeInstanceId: Header(h, BrokerTellWire.HeaderAddresseeInstance),
				Arguments: codec.Decode(signal.Value),
				Check: Header(h, BrokerTellWire.HeaderCheck),
				TraceParent: Header(h, BrokerTellWire.HeaderTraceParent),
				TraceState: Header(h, BrokerTellWire.HeaderTraceState));
		}

		private static string Header(IReadOnlyDictionary<string, string> headers, string key)
		{
			return headers != null && headers.TryGetValue(key, out string value) ? value : null;
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			subscription?.Dispose();
			subscription = null;
		}
	}
}
