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
		private readonly object handlerLock = new object();

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

		// Clears the accumulated envelopes without disconnecting the handler. Useful when
		// a test reuses the transport across phases.
		public void ClearSent()
		{
			while (sent.TryDequeue(out _)) { }
		}
	}
}
