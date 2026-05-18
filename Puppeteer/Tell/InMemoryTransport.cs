using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Puppeteer.Tell
{
	// Implementación en memoria de ITransport, útil para tests unitarios de actores
	// que cooperan sin red ni broker. Acumula los envelopes enviados en una lista
	// observable (Sent), e invoca al handler registrado cuando los tests llaman a
	// TriggerAck para simular la recepción de un ack.
	//
	// No reordena, no reintenta, no introduce latencia. Tests de comportamiento real
	// del transport (Kafka, REST) van por implementaciones específicas en
	// Choreography (Plan 9 of the Tell primitive roadmap).
	public sealed class InMemoryTransport : ITransport
	{
		private readonly ConcurrentQueue<TellEnvelope> sent = new ConcurrentQueue<TellEnvelope>();
		private Action<AckEnvelope> ackHandler;
		private readonly object handlerLock = new object();

		// Snapshot de los envelopes que el transport ha recibido vía SendAsync.
		// Tests pueden inspeccionar el contenido para verificar que el actor entregó
		// la oración correcta.
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

		// Test hook: simula la recepción de un ack del receptor. Invoca el handler
		// registrado, que en Plan 6 será el que journaliza la oración `tell ack ...
		// from <Target>(<id>)` y emite MarkAsSkip implícito sobre la pareja.
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

		// Limpia los envelopes acumulados sin desconectar el handler. Útil cuando
		// un test reutiliza el transport entre fases.
		public void ClearSent()
		{
			while (sent.TryDequeue(out _)) { }
		}
	}
}
