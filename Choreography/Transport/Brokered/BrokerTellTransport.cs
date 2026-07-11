using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Puppeteer.Tell;

namespace Choreography.Transport.Brokered
{
	// Broker binding of the Tell primitive: a Puppeteer.Tell.ITransport that
	// delivers a tell envelope as a broker record and closes the round-trip via
	// an ack topic. It is the origin (A) side of the conversation:
	//
	//   * SendAsync produces the ordered payload values to the destination topic
	//     resolved from the (addressee, message) binding table. The tell id,
	//     message, addressee, and check travel as headers; the value is exactly the
	//     serialized payload. The transport route is never on the wire.
	//   * It subscribes (lazily, per destination topic) to "<topic>.acks" and,
	//     when the receiver reports success there, invokes the registered ack
	//     handler so the origin journals `tell ack '<id>' from <Addressee>(<id>)`.
	//   * A failure record on the ack topic — or a produce that faults — invokes the
	//     registered failure handler so the origin journals the LOGICAL non-delivery
	//     verdict (`unacknowledged by <Addressee>`). This transport's WitnessName is
	//     carried as telemetry only; the journal never names it.
	//
	// Correlation belongs to the journal; delivery belongs to the transport.
	// This type keeps an in-process map of issued ids to their last observed
	// fate so GetFateAsync can answer a recovery citation within the lifetime of
	// the process. A durable, replay-from-offset fate store is a later
	// iteration; after a process restart the map is empty and GetFateAsync
	// honestly answers InFlight (the journal then leaves the tell pending).
	public sealed class BrokerTellTransport : ITransport, IDisposable
	{
		private readonly IMessageBroker broker;
		private readonly TellBindingTable bindings;

		// Decides the byte shape of the record value. Defaults to today's behaviour
		// (ArgumentsAsString); a host may slot in a denser codec — the receiver's
		// BrokerTellConsumer MUST be wired with the same codec so the payload
		// round-trips A -> B. The transport stays payload-shape-agnostic either way.
		private readonly ITellPayloadCodec codec;

		private readonly object handlerLock = new object();
		private Action<AckEnvelope> ackHandler;
		private Action<TellFailure> failureHandler;

		private readonly ConcurrentDictionary<string, TellFate> fates =
			new ConcurrentDictionary<string, TellFate>(StringComparer.Ordinal);

		// Ack topics we have already wired a subscription for, so SendAsync only
		// subscribes once per destination topic.
		private readonly ConcurrentDictionary<string, IDisposable> ackSubscriptions =
			new ConcurrentDictionary<string, IDisposable>(StringComparer.Ordinal);

		private bool disposed;

		public BrokerTellTransport(IMessageBroker broker, TellBindingTable bindings, string witnessName = null, ITellPayloadCodec payloadCodec = null)
		{
			this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
			this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
			this.WitnessName = string.IsNullOrWhiteSpace(witnessName) ? "broker" : witnessName;
			this.codec = payloadCodec ?? DefaultTellPayloadCodec.Instance;
		}

		// Stable telemetry label for this transport. It rides on failure records and
		// is logged by the origin, but is NEVER journaled — the non-delivery verdict
		// the journal records is logical (it names the addressee, not the transport).
		public string WitnessName { get; }

		public Task SendAsync(TellEnvelope envelope, CancellationToken cancellationToken = default)
		{
			if (disposed) throw new ObjectDisposedException(nameof(BrokerTellTransport));
			cancellationToken.ThrowIfCancellationRequested();

			string topic = bindings.Resolve(envelope.Addressee, envelope.MessageName);
			EnsureAckSubscription(topic);

			Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				[BrokerTellWire.HeaderKind] = BrokerTellWire.KindTell,
				[BrokerTellWire.HeaderTellId] = envelope.Id,
				[BrokerTellWire.HeaderMessage] = envelope.MessageName,
				[BrokerTellWire.HeaderAddressee] = envelope.Addressee,
				[BrokerTellWire.HeaderAddresseeInstance] = envelope.AddresseeInstanceId,
				[BrokerTellWire.HeaderCheck] = envelope.Check,
				[BrokerTellWire.HeaderReaction] = envelope.ReactionName,
				[BrokerTellWire.HeaderCausalEvent] = envelope.CausalEventId
			};

			// Inject W3C trace context (ephemeral observability) so the receiver
			// re-parents its work onto this trace and the cross-actor tell chain renders
			// as ONE distributed trace. Prefer a context already stamped on the envelope
			// (the uniform carrier); otherwise capture the ambient Activity at send.
			// No active trace -> nothing injected (no header, zero overhead). Never
			// journaled — this rides the wire only.
			string traceParent = envelope.TraceParent;
			string traceState = envelope.TraceState;
			if (traceParent == null)
			{
				Puppeteer.Tell.TraceContext.TryCaptureAmbient(out traceParent, out traceState);
			}
			if (!string.IsNullOrEmpty(traceParent))
			{
				headers[BrokerTellWire.HeaderTraceParent] = traceParent;
				if (!string.IsNullOrEmpty(traceState))
					headers[BrokerTellWire.HeaderTraceState] = traceState;
			}

			fates[envelope.Id] = TellFate.InFlight;

			// The record value is the ordered payload VALUES (serialized in order) —
			// never the parameter names/types nor any command template. The command
			// shape is the receiver's; repeating it on every message would be
			// porosity. The host maps the message name (a header) to the command it
			// already holds and applies these values positionally. The codec decides
			// only the byte shape of that value; the default is ArgumentsAsString.
			string body = codec.Encode(envelope.Values);

			// Key by the addressee instance so all tells to one instance keep their
			// relative order on a partition.
			return broker
				.ProduceAsync(topic, envelope.AddresseeInstanceId, headers, body, cancellationToken)
				.ContinueWith(
					t => OnProduceCompleted(t, envelope),
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
		}

		private void OnProduceCompleted(Task produce, TellEnvelope envelope)
		{
			if (!produce.IsFaulted) return;

			// The transport gave up on this envelope: record the terminal fate and
			// declare the non-acknowledgement so the origin journals the LOGICAL
			// verdict (by addressee). The transport names itself only as telemetry.
			fates[envelope.Id] = TellFate.Failed;
			string reason = produce.Exception?.GetBaseException().Message;

			Action<TellFailure> handler;
			lock (handlerLock) { handler = failureHandler; }
			handler?.Invoke(new TellFailure(envelope.Id, envelope.Addressee, WitnessName, reason));
		}

		public void RegisterAckHandler(Action<AckEnvelope> handler)
		{
			ArgumentNullException.ThrowIfNull(handler);
			lock (handlerLock)
			{
				if (ackHandler != null)
					throw new InvalidOperationException("BrokerTellTransport already has an ack handler registered. Each transport instance accepts a single handler — share the instance, do not register twice.");
				ackHandler = handler;
			}
		}

		public void RegisterFailureHandler(Action<TellFailure> handler)
		{
			ArgumentNullException.ThrowIfNull(handler);
			lock (handlerLock)
			{
				if (failureHandler != null)
					throw new InvalidOperationException("BrokerTellTransport already has a failure handler registered. Each transport instance accepts a single handler — share the instance, do not register twice.");
				failureHandler = handler;
			}
		}

		public Task<TellFate> GetFateAsync(string envelopeId, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			TellFate fate = fates.TryGetValue(envelopeId ?? string.Empty, out TellFate known) ? known : TellFate.InFlight;
			return Task.FromResult(fate);
		}

		private void EnsureAckSubscription(string topic)
		{
			string ackTopic = BrokerTellWire.AckTopicOf(topic);
			ackSubscriptions.GetOrAdd(ackTopic, t => broker.Subscribe(t, OnAckRecord));
		}

		private void OnAckRecord(BrokerRecord record)
		{
			IReadOnlyDictionary<string, string> h = record.Headers;
			if (h == null) return;
			h.TryGetValue(BrokerTellWire.HeaderKind, out string kind);
			h.TryGetValue(BrokerTellWire.HeaderTellId, out string tellId);
			if (string.IsNullOrEmpty(tellId)) return;

			if (kind == BrokerTellWire.KindAck)
			{
				h.TryGetValue(BrokerTellWire.HeaderAddressee, out string addressee);
				h.TryGetValue(BrokerTellWire.HeaderAddresseeInstance, out string instance);
				fates[tellId] = TellFate.Delivered;

				Action<AckEnvelope> handler;
				lock (handlerLock) { handler = ackHandler; }
				handler?.Invoke(new AckEnvelope(tellId, addressee, instance));
			}
			else if (kind == BrokerTellWire.KindFailure)
			{
				h.TryGetValue(BrokerTellWire.HeaderAddressee, out string addressee);
				h.TryGetValue(BrokerTellWire.HeaderWitness, out string witness);
				h.TryGetValue(BrokerTellWire.HeaderReason, out string reason);
				fates[tellId] = TellFate.Failed;

				Action<TellFailure> handler;
				lock (handlerLock) { handler = failureHandler; }
				handler?.Invoke(new TellFailure(tellId, addressee, string.IsNullOrWhiteSpace(witness) ? WitnessName : witness, reason));
			}
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			foreach (IDisposable subscription in ackSubscriptions.Values)
			{
				subscription.Dispose();
			}
			ackSubscriptions.Clear();
		}
	}
}
