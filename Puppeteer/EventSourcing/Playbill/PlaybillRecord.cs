using System;

namespace Puppeteer.EventSourcing.Playbill
{
	// Snapshot inmutable de una entrada del playbill. Public porque el forensic
	// query path (Performance.Playbill consultas a futuro, replicacion via wire)
	// lo expone al consumidor. Sin embargo el SerializedParameters es opaco —
	// solo el schema correspondiente (lookup via SchemaName en PlaybillStore)
	// permite deserializarlo a tipos.
	//
	// Lineamiento Fase 1 Playbill: ip/user del journal ya no existen; la evidencia
	// contextual de la presentation vive aqui, schema-named, referenciada por EntryId.
	public readonly struct PlaybillRecord
	{
		public long EntryId { get; }
		public string SchemaName { get; }
		public string SerializedParameters { get; }

		public PlaybillRecord(long entryId, string schemaName, string serializedParameters)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
			ArgumentNullException.ThrowIfNull(serializedParameters);

			this.EntryId = entryId;
			this.SchemaName = schemaName;
			this.SerializedParameters = serializedParameters;
		}
	}
}
