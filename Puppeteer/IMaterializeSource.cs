using Puppeteer.EventSourcing.DB;
using System.Collections.Generic;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Phase 4 (signed D1 2026-05-13). Interface from the
	// destination side to the primary side. Abstracts the 4 wire verbs (a)/(b)/(c)/(d):
	//
	//   (a) ReadRecordsAfter — Layer 1 (raw records).
	//   (b) ConfirmUntil      — Max-monotonic ack to the primary.
	//   (c) ReadReactions     — Layer 2a (registry + checkpoints snapshot).
	//   (d) ReadElidedRange   — Layer 2b (elision markers).
	//
	// The destination process in production implements this as an HTTP client to the
	// primary (out of I1 scope). In-process tests use LocalMaterializeSource
	// to wrap a primary actor.Materialization without a real transport.
	//
	// MaterializeMirror orchestrates the 4 verbs via this interface — transport-agnostic.
	public interface IMaterializeSource
	{
		// The destination symbolic name that this source represents (used for
		// log/debug; the primary receives this string in all calls).
		string DestinationName { get; }

		IReadOnlyList<MaterializationRecord> ReadRecordsAfter(long fromEntryId);

		MaterializationReactionsSnapshot ReadReactions();

		IReadOnlyList<MaterializationElisionMarker> ReadElidedRange(long fromEntryId, long toEntryId);

		bool ConfirmUntil(long entryId);
	}
}
