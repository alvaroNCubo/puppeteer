using Puppeteer;

namespace Choreography.Transport.Brokered
{
	// Transport-level seam for how a tell's PAYLOAD (its ordered values) is
	// serialized onto the wire and read back — the one place the byte shape of the
	// record value is decided, symmetric across the origin (encode) and the receiver
	// (decode).
	//
	// The default (DefaultTellPayloadCodec) is byte-for-byte today's behaviour: the
	// record value is Parameters.ArgumentsAsString (ordered values, no parameter
	// names, no types, no command template) and decode is the identity. A host that
	// wants a denser PER-RECORD wire — a binary/packed layout, a fixed dictionary, or
	// a delta against a FIXED baseline — slots in its own codec WITHOUT rewriting a
	// whole ITransport, and WITHOUT the transport learning anything about the
	// payload's shape. The codec knows how to pack and unpack bytes; it does not know
	// that any field repeats. Repetition is application knowledge and stays there.
	//
	// Scope of this seam is ONE record: Encode sees a single envelope's values and
	// Decode a single wire value — it never sees the neighbouring records, so it
	// cannot (and must not) do cross-record encoding. Collapsing the prefixes that
	// repeat ACROSS records (the K,L,M / X,Y,Z of a hierarchical stream) is the job of
	// the broker's native compression/batching (BrokerProducerOptions), which the
	// broker decompresses transparently on the consume side so every record stays
	// self-contained and independently decodable for replay. A codec that delta-coded
	// against the PREVIOUS record would be model-dedup relocated into the codec — it
	// breaks on redelivery, reorder, and restart — and is exactly what this per-record
	// scope forbids.
	//
	// Contract: the SAME codec must be wired on both sides of one conversation — the
	// origin's BrokerTellTransport and the receiver's BrokerTellConsumer — so
	// Decode(Encode(values)) reproduces the ordered arguments the receiver applies
	// positionally to the command it already holds. Delivery stays the transport's
	// problem; correlation stays the journal's; the codec only decides the payload's
	// byte shape.
	public interface ITellPayloadCodec
	{
		// Encode the ordered payload values into the record value that crosses the
		// wire. values may be null when the message carries no payload; a codec
		// returns the empty string in that case (the default does).
		string Encode(Parameters values);

		// Decode a received record value back into the ordered-arguments string the
		// receiver applies positionally to the command it already holds. Inverse of
		// Encode for the same codec.
		string Decode(string wireValue);
	}

	// Default codec: the wire value is exactly Parameters.ArgumentsAsString in the
	// in-memory rendering, and decode is the identity — so a transport that does not
	// opt into a custom codec produces and reads the exact bytes it always has. This
	// is the fallback BrokerTellTransport / BrokerTellConsumer use when no codec is
	// supplied.
	public sealed class DefaultTellPayloadCodec : ITellPayloadCodec
	{
		public static readonly DefaultTellPayloadCodec Instance = new DefaultTellPayloadCodec();

		public string Encode(Parameters values)
		{
			return values != null
				? values.ArgumentsAsString(DatabaseType.IN_MEMORY)
				: string.Empty;
		}

		public string Decode(string wireValue)
		{
			return wireValue;
		}
	}
}
