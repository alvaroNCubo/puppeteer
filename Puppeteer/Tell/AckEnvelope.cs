namespace Puppeteer.Tell
{
	// Sobre que el transport entrega de regreso al actor de origen cuando el
	// receptor (B) reportó procesamiento. B no sabe que existe la primitiva tell —
	// emite un evento normal por su endpoint, y el transport (KafkaTransport,
	// RestTransport, etc.) lo mapea al canal de retorno hacia A. Plan 6 lo
	// ingiere como oración `tell ack <id> from <Target>(<id>)` en el journal de A.
	public readonly record struct AckEnvelope(
		string Id,
		string TargetClass,
		string TargetId);
}
