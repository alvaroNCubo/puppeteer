using System;

namespace Choreography.Transport.Brokered
{
	// Medium-native, domain-agnostic producer knobs a host sets on a broker binding
	// to make a stream of tells cheaper on the wire. These are the SAME levers every
	// message broker exposes — compression and batching — expressed once in
	// broker-neutral terms so any concrete IMessageBroker adapter can map them to its
	// own client (e.g. a Confluent.Kafka ProducerConfig).
	//
	// Why this is the PRIMARY wire-efficiency lever: successive tells in a
	// hierarchical stream repeat their leading fields (a header group that varies
	// slowly, subgroups that vary less than the leaf tuple). The transport keeps
	// every record self-contained — that is correct, events must stand alone for
	// replay — so the repetition lives on the WIRE, not in the model. A compressed,
	// batched producer collapses those repeated prefixes across the batch for free,
	// WITHOUT the transport (or a payload codec) ever learning which fields repeat.
	// The repetition is application knowledge; compression exploits it blindly.
	//
	// Nothing here is domain-aware: it names no field, no group, no hierarchy — only
	// the broker's own compression codec and batching window. Construction-time
	// configuration of a concrete adapter, never part of the runtime IMessageBroker
	// seam (which stays produce/subscribe only).
	public sealed class BrokerProducerOptions
	{
		// No compression, no added linger, library-default batch size: the historical
		// behaviour, so an adapter that falls back to this changes nothing on the wire.
		public static BrokerProducerOptions Default { get; } = new BrokerProducerOptions();

		// Wire compression codec the producer applies to a (batched) set of records.
		// Zstd and Lz4 give the best ratio/CPU trade-off for repetitive text payloads.
		public BrokerCompression Compression { get; init; } = BrokerCompression.None;

		// How long the producer may wait accumulating records before sending a batch.
		// A larger window batches more records together, so compression sees more
		// repetition to collapse. Null leaves the client default (no added linger).
		public TimeSpan? Linger { get; init; }

		// Target maximum size of a single produced batch, in bytes. Larger batches
		// amortize per-record overhead and give compression a wider window over which
		// to collapse repeated prefixes. Null leaves the client default.
		public int? BatchSizeBytes { get; init; }
	}

	// Broker-neutral compression codecs. An adapter maps each to its client's own
	// enum; an unsupported value should fault fast at adapter construction rather
	// than be silently downgraded.
	public enum BrokerCompression
	{
		None = 0,
		Gzip = 1,
		Snappy = 2,
		Lz4 = 3,
		Zstd = 4
	}
}
