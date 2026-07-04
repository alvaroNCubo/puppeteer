using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Choreography.Transport.Brokered
{
	// Minimal seam over a message broker. The Tell transport and its
	// receiver speak only this interface, so the end-to-end flow can be
	// exercised against an in-process loopback (InProcessBroker) without a
	// live broker, and against a real cluster (ConfluentKafkaBroker) in
	// production. Durability, partitioning, retry, and offset management live
	// behind the implementation; none of it leaks into the transport.
	//
	// A record carries a partition key (drives per-instance ordering), a set of
	// string headers (the tell correlation metadata), and a UTF-8 string value
	// (the rendered command text). Headers — not the value — carry correlation
	// so the value stays exactly the payload a receiver consumes.
	public interface IMessageBroker
	{
		// Produce a record to a topic. Completes when the broker has accepted
		// the record. A delivery failure surfaces as a faulted task — the
		// transport translates that into a non-delivery verdict.
		Task ProduceAsync(string topic, string key, IReadOnlyDictionary<string, string> headers, string value, CancellationToken cancellationToken = default);

		// Subscribe a handler to every record that lands on a topic from the
		// subscription point onward. Disposing the returned token cancels the
		// subscription. A handler that throws must not tear down the
		// subscription — the implementation logs and continues.
		IDisposable Subscribe(string topic, Action<BrokerRecord> onRecord);
	}

	// Immutable record as seen by a subscriber. Topic is included so a single
	// handler can serve more than one subscription if a host wires it that way.
	public readonly record struct BrokerRecord(
		string Topic,
		string Key,
		IReadOnlyDictionary<string, string> Headers,
		string Value);
}
