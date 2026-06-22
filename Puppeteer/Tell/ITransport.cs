using System;
using System.Threading;
using System.Threading.Tasks;

namespace Puppeteer.Tell
{
	// Abstraction that separates causation from transport. Puppeteer journals the
	// sentence of the tell and hands the envelope to the transport; the transport
	// decides how to deliver it (Kafka, REST, gRPC, etc.) and reports back the
	// acks it receives from the receiver side.
	//
	// Canonical statement:
	//   "Delivery is the transport's problem. Correlation is the journal's
	//    problem. The journal collects tells without bound; the transport
	//    collects regrets and decides what to do with them."
	//
	// Plan 4 of the Tell primitive roadmap introduces the interface. Plans 5, 6,
	// and 9 wire it to journaling, ack ingestion, and Choreography integration
	// respectively.
	public interface ITransport
	{
		// Delivers the envelope to the receiver. The retry / timeout / dead
		// letter / backoff policy lives in the implementation, NOT in Puppeteer.
		Task SendAsync(TellEnvelope envelope, CancellationToken cancellationToken = default);

		// Registers the handler that the transport will invoke when it receives an ack from the
		// receiver (B). Plan 6 will connect this handler to the journaling of actor A.
		void RegisterAckHandler(Action<AckEnvelope> handler);
	}
}
