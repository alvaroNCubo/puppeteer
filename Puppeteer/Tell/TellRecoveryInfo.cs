namespace Puppeteer.Tell
{
	// Minimal facts about an issued tell, captured during journal replay so the
	// post-rehydration recovery pass can resolve the tell's fate with the
	// transport even though the original in-memory TellEnvelope was lost to the
	// crash window (committed to the journal, never dispatched).
	//
	// TargetClass / TargetId let the recovery pass reconstruct an ack sentence
	// when the transport testifies Delivered; Witness names the transport in the
	// non-delivery sentence when it testifies Failed (the `through` literal of the
	// originating tell when present, else the transport's own WitnessName).
	//
	// This is framework-internal recovery bookkeeping — not part of the transport
	// contract — so it never travels on the wire.
	//
	// Plan 10 of the Tell primitive roadmap introduces the type.
	internal readonly record struct TellRecoveryInfo(
		string TargetClass,
		string TargetId,
		string Witness);
}
