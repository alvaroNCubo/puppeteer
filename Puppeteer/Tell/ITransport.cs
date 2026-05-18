using System;
using System.Threading;
using System.Threading.Tasks;

namespace Puppeteer.Tell
{
	// Abstracción que separa la causación del transporte. Puppeteer journala la
	// oración del tell y le entrega el envelope al transporte; el transporte
	// decide cómo entregarlo (Kafka, REST, gRPC, etc.) y reporta de vuelta los
	// acks que recibe del lado receptor.
	//
	// Frase canónica firmada (project_puppeteer_tell_primitive_design.md):
	//   "Delivery is the transport's problem. Correlation is the journal's
	//    problem. The journal collects tells without bound; the transport
	//    collects regrets and decides what to do with them."
	//
	// Plan 4 of the Tell primitive roadmap introduces the interface. Plans 5, 6,
	// and 9 wire it to journaling, ack ingestion, and Choreography integration
	// respectively.
	public interface ITransport
	{
		// Entrega el envelope al receptor. La política de retry / timeout / dead
		// letter / backoff vive en la implementación, NO en Puppeteer.
		Task SendAsync(TellEnvelope envelope, CancellationToken cancellationToken = default);

		// Registra el handler que el transporte invocará cuando reciba un ack del
		// receptor (B). Plan 6 conectará este handler al journaling del actor A.
		void RegisterAckHandler(Action<AckEnvelope> handler);
	}
}
