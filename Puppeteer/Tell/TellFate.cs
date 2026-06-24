namespace Puppeteer.Tell
{
	// The transport's verdict on the fate of an issued tell. Returned by
	// ITransport.GetFateAsync when the origin actor cites (subpoenas) the
	// transport during recovery for a specific unacked envelope id.
	//
	// Delivery is the transport's authority; this enum is its testimony, which
	// the journal then records as a verdict. It closes the crash window between
	// a tell's journal commit and its post-commit dispatch: after rehydration
	// the journal is self-sufficient about the OUTCOME of every issued tell, not
	// merely its issuance.
	//
	// Plan 10 of the Tell primitive roadmap introduces the type.
	public enum TellFate
	{
		// The transport has no terminal outcome yet: the envelope is still in its
		// retry / backoff pipeline, or the transport simply does not know about it.
		// The origin actor leaves the tell pending — a later ack or failure
		// declaration through the transport's handlers will settle it.
		InFlight,

		// The transport confirms the receiver processed the envelope; only the ack
		// round-trip was lost. The origin journals the ack to close the loop.
		Delivered,

		// The transport gave up (dead-letter / exhausted retries). The origin
		// journals a non-delivery verdict with the transport as witness.
		Failed
	}
}
