namespace Puppeteer.Tell
{
	// Sobre que viaja del actor de origen (A) al transport, y del transport al
	// receptor (B). Es DTO operacional — no es la oración del journal. La oración
	// vive en el journal de A; el envelope es solo el sobre que el transport
	// entrega para que B procese por su endpoint público.
	//
	// Plan 4 of the Tell primitive roadmap introduces the type. Plan 5 wires it
	// to the journal entry of the executing TellStatement.
	public readonly record struct TellEnvelope(
		string Id,
		string TargetClass,
		string TargetId,
		string CommandText,
		string Transport,
		string CausalEventId,
		string ReactionName);
}
