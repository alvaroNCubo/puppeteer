namespace Puppeteer.Tell
{
	// Minimal facts about an issued tell, captured during journal replay so the
	// post-rehydration recovery pass can resolve the tell's fate with the transport
	// even though the original in-memory TellEnvelope was lost to the crash window
	// (committed to the journal, never dispatched).
	//
	// Addressee / AddresseeInstanceId let the recovery pass reconstruct the logical
	// verdict the origin journals when the transport testifies: an ack sentence on
	// Delivered, an `unacknowledged by <Addressee>` sentence on Failed. No transport
	// name is kept — the verdict the journal records is logical, and which broker
	// testified belongs to telemetry, not the journal.
	//
	// This is framework-internal recovery bookkeeping — not part of the transport
	// contract — so it never travels on the wire.
	internal readonly record struct TellRecoveryInfo(
		string Addressee,
		string AddresseeInstanceId);
}
