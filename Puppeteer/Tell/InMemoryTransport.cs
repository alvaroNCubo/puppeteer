using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Puppeteer.Tell
{
	// In-memory implementation of ITransport, useful for unit tests of actors
	// that cooperate without network or broker. It accumulates the sent envelopes in an
	// observable list (Sent), and invokes the registered handler when tests call
	// TriggerAck to simulate the reception of an ack.
	//
	// It does not reorder, does not retry, does not introduce latency. Tests of real
	// transport behavior (Kafka, REST) go through specific implementations in
	// Choreography (Plan 9 of the Tell primitive roadmap).
	public sealed class InMemoryTransport : ITransport
	{
		private readonly ConcurrentQueue<TellEnvelope> sent = new ConcurrentQueue<TellEnvelope>();
		private Action<AckEnvelope> ackHandler;
		private Action<TellFailure> failureHandler;
		private readonly object handlerLock = new object();

		// Plan 10 — fate recovery. Map of envelope id -> fate the transport will
		// testify when cited via GetFateAsync. Tests pre-seed it through SetFate to
		// stage a crash-window recovery (e.g. SetFate(id, TellFate.Failed)). Any id
		// not present answers InFlight — the transport "does not know", so the
		// origin leaves it pending.
		private readonly ConcurrentDictionary<string, TellFate> fates = new ConcurrentDictionary<string, TellFate>(StringComparer.Ordinal);

		// Plan 10 — fate recovery. Self-identifying witness label for the
		// non-delivery verdict. Configurable so a test can assert the witness that
		// ends up in the journal sentence; defaults to a neutral name.
		public string WitnessName { get; set; } = "InMemory";

		// Snapshot of the envelopes that the transport has received via SendAsync.
		// Tests can inspect the content to verify that the actor delivered
		// the correct sentence.
		public IReadOnlyCollection<TellEnvelope> Sent
		{
			get
			{
				return sent.ToArray();
			}
		}

		public Task SendAsync(TellEnvelope envelope, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			sent.Enqueue(envelope);
			return Task.CompletedTask;
		}

		public void RegisterAckHandler(Action<AckEnvelope> handler)
		{
			ArgumentNullException.ThrowIfNull(handler);
			lock (handlerLock)
			{
				if (ackHandler != null)
				{
					throw new InvalidOperationException("InMemoryTransport already has an ack handler registered. Each transport instance accepts a single handler — share the instance, do not register twice.");
				}
				ackHandler = handler;
			}
		}

		// Test hook: simulates the reception of an ack from the receiver. Invokes the
		// registered handler, which in Plan 6 will be the one that journals the sentence `tell ack ...
		// from <Target>(<id>)` and emits an implicit MarkAsSkip over the pair.
		public void TriggerAck(AckEnvelope envelope)
		{
			Action<AckEnvelope> handler;
			lock (handlerLock)
			{
				handler = ackHandler;
			}
			if (handler == null)
			{
				throw new InvalidOperationException("InMemoryTransport.TriggerAck called before any ack handler was registered. Tests must register a handler before triggering acks.");
			}
			handler(envelope);
		}

		// Plan 10 — fate recovery, by declaration. Registers the handler the
		// transport invokes when it declares an envelope failed. Single-slot, same
		// discipline as RegisterAckHandler.
		public void RegisterFailureHandler(Action<TellFailure> handler)
		{
			ArgumentNullException.ThrowIfNull(handler);
			lock (handlerLock)
			{
				if (failureHandler != null)
				{
					throw new InvalidOperationException("InMemoryTransport already has a failure handler registered. Each transport instance accepts a single handler — share the instance, do not register twice.");
				}
				failureHandler = handler;
			}
		}

		// Plan 10 — fate recovery, by citation. Answers the fate the test staged
		// for this envelope id, or InFlight when none was staged.
		public Task<TellFate> GetFateAsync(string envelopeId, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			TellFate fate = fates.TryGetValue(envelopeId ?? string.Empty, out TellFate staged) ? staged : TellFate.InFlight;
			return Task.FromResult(fate);
		}

		// Test hook: stage the fate the transport will testify for an envelope id
		// when cited via GetFateAsync. Models the durable record a real transport
		// keeps about the delivery outcome.
		public void SetFate(string envelopeId, TellFate fate)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
			fates[envelopeId] = fate;
		}

		// Test hook: simulates the transport proactively declaring a non-delivery,
		// mirroring TriggerAck on the failure side.
		public void TriggerFailure(TellFailure failure)
		{
			Action<TellFailure> handler;
			lock (handlerLock)
			{
				handler = failureHandler;
			}
			if (handler == null)
			{
				throw new InvalidOperationException("InMemoryTransport.TriggerFailure called before any failure handler was registered. Tests must register a handler before triggering failures.");
			}
			handler(failure);
		}

		// Clears the accumulated envelopes without disconnecting the handler. Useful when
		// a test reuses the transport across phases.
		public void ClearSent()
		{
			while (sent.TryDequeue(out _)) { }
		}
	}
}
