using Puppeteer.EventSourcing.DB;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Phase 4. In-process implementation of
	// IMaterializeSource that wraps a primary actor.Materialization for integration
	// tests. In production the equivalent would be an HttpMaterializeSource
	// that calls wire HTTP to the primary (out of I1 scope).
	//
	// This implementation adds no logic — only a direct proxy to the primary's
	// APIs, with destinationName as fixed context.
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
