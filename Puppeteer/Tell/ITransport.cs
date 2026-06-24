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
	// respectively. Plan 10 adds fate recovery (WitnessName / GetFateAsync /
	// RegisterFailureHandler) so the journal records the OUTCOME of every issued
	// tell, not just its issuance.
	public interface ITransport
	{
		// Delivers the envelope to the receiver. The retry / timeout / dead
		// letter / backoff policy lives in the implementation, NOT in Puppeteer.
		Task SendAsync(TellEnvelope envelope, CancellationToken cancellationToken = default);

		// Registers the handler that the transport will invoke when it receives an ack from the
		// receiver (B). Plan 6 will connect this handler to the journaling of actor A.
		void RegisterAckHandler(Action<AckEnvelope> handler);

		// Plan 10 — fate recovery. Self-identifying label of this transport, used
		// as the witness in the non-delivery verdict the origin journals when this
		// transport testifies a Failed fate (e.g. "Kafka:loyalty-v1"). Stable for
		// the transport instance. The default keeps transports that predate fate
		// recovery compiling while still producing a meaningful — if generic —
		// witness.
		string WitnessName => "transport";

		// Plan 10 — fate recovery, by citation (subpoena). On recovery the origin
		// actor summons the transport to testify the fate of a specific unacked
		// envelope id. Mirrors the courtroom metaphor: the journal records
		// issuance, but the transport owns delivery and is the only authority that
		// can testify whether the envelope crossed the boundary. The default
		// answers InFlight, so a transport that has not opted into recovery leaves
		// the tell pending rather than fabricating a verdict.
		Task<TellFate> GetFateAsync(string envelopeId, CancellationToken cancellationToken = default)
			=> Task.FromResult(TellFate.InFlight);

		// Plan 10 — fate recovery, by declaration. Registers the handler the
		// transport invokes when it proactively determines an envelope failed
		// (dead-letter / exhausted-retries callback). Mirrors RegisterAckHandler:
		// acks close the loop on success, failures on non-delivery. The default is
		// a no-op so transports that predate fate recovery compile unchanged.
		void RegisterFailureHandler(Action<TellFailure> handler) { }
	}
}
