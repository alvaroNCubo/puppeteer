using System;

namespace Puppeteer.EventSourcing.Playbill
{
	// Immutable snapshot of a playbill entry. Public because the forensic
	// query path (future Performance.Playbill queries, replication via wire)
	// exposes it to the consumer. However, SerializedParameters is opaque —
	// only the corresponding schema (lookup via SchemaName in PlaybillStore)
	// allows deserializing it to types.
	//
	// Playbill Phase 1 guideline: ip/user from the journal no longer exist; the
	// contextual evidence of the presentation lives here, schema-named, referenced by EntryId.
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
