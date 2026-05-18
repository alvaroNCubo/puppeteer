using Puppeteer.EventSourcing.DB;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Fase 4. Implementacion in-process de
	// IMaterializeSource que envuelve un primary actor.Materialization para tests
	// integradores. En produccion el equivalente seria un HttpMaterializeSource
	// que llama wire HTTP al primary (fuera de scope I1).
	//
	// Esta implementacion no agrega logica — solo proxy directo a las APIs del
	// primary, con destinationName como contexto fijo.
	public class LocalMaterializeSource : IMaterializeSource
	{
		private readonly Materialization primary;

		public string DestinationName { get; }

		public LocalMaterializeSource(Materialization primary, string destinationName)
		{
			ArgumentNullException.ThrowIfNull(primary);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destinationName);
			this.primary = primary;
			this.DestinationName = destinationName;
		}

		public IReadOnlyList<MaterializationRecord> ReadRecordsAfter(long fromEntryId)
		{
			return primary.ReadRecordsAfter(DestinationName, fromEntryId);
		}

		public MaterializationReactionsSnapshot ReadReactions()
		{
			return primary.ReadReactions(DestinationName);
		}

		public IReadOnlyList<MaterializationElisionMarker> ReadElidedRange(long fromEntryId, long toEntryId)
		{
			return primary.ReadElidedRange(DestinationName, fromEntryId, toEntryId);
		}

		public bool ConfirmUntil(long entryId)
		{
			return primary.ConfirmUntil(DestinationName, entryId);
		}
	}
}
