using Puppeteer.EventSourcing.DB;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Phase 4 (signed D1 2026-05-13). Destination-side
	// client that orchestrates the 4 primary-side wire verbs. Signed fluent
	// pattern:
	//
	//   mirror.Sync();                       // Layer 1 — orchestrates (a) + (b).
	//   mirror.AsProgramMirror().Sync();     // Layer 2 — orchestrates (a) + (c) + (d) + (b).
	//
	// Transport-agnostic Sync: the caller provides an IMaterializeSource that
	// abstracts HTTP / in-process / loopback. MaterializeMirror only coordinates the
	// cycle and updates the local watermark.
	//
	// IMPORTANT — design signed in Phase 4: MaterializeMirror does NOT apply the
	// fetched data to a local storage. It returns a MirrorSyncResult with everything
	// received — the caller decides what to do with the records / reactions /
	// elision markers. This separates "fetch + confirm" (responsibility of the
	// mirror client) from "apply locally" (responsibility of the destination
	// operator who knows whether it has a local journal, async replication, a pure
	// passive consumer, etc.). Local application is a Hole for a future phase, when
	// the complete destination-side model is signed.
	public class MaterializeMirror
	{
		private readonly IMaterializeSource source;
		private long watermark;

		// Current watermark: the last EntryId confirmed to the primary via (b).
		// Starts at startingFrom (default 0); advances with each successful Sync.
		public long Watermark => watermark;

		public IMaterializeSource Source => source;

		public MaterializeMirror(IMaterializeSource source, long startingFrom = 0)
		{
			ArgumentNullException.ThrowIfNull(source);
			if (startingFrom < 0) throw new LanguageException($"startingFrom {startingFrom} must be zero or greater.");
			this.source = source;
			this.watermark = startingFrom;
		}

		// Layer 1 sync: orchestrates (a) ReadRecordsAfter + (b) ConfirmUntil. Does
		// not include reaction state or elision markers — the caller only receives
		// the raw records. Useful for a destination that does NOT need to re-execute
		// reactions locally (e.g. archive-only mirror).
		public MirrorSyncResult Sync()
		{
			return SyncInternal(includeCapa2: false);
		}

		// Activates Layer 2 for the next .Sync() — adds (c) + (d) to the
		// orchestration. Signed decision D1: a destination that wants to rebuild
		// program state (reactions + elision) opts in expressly via this pattern.
		public MaterializeMirrorBuilder AsProgramMirror()
		{
			return new MaterializeMirrorBuilder(this);
		}

		internal MirrorSyncResult SyncInternal(bool includeCapa2)
		{
			long previousWatermark = watermark;
			Stopwatch stopwatch = Stopwatch.StartNew();

			// (a) Layer 1 — raw records from the current watermark.
			IReadOnlyList<MaterializationRecord> records = source.ReadRecordsAfter(watermark);

			MaterializationReactionsSnapshot? reactionsSnapshot = null;
			IReadOnlyList<MaterializationElisionMarker> elisionMarkers = Array.Empty<MaterializationElisionMarker>();

			long newHead = previousWatermark;
			foreach (var record in records)
			{
				if (record.EntryId > newHead) newHead = record.EntryId;
			}

			if (includeCapa2 && records.Count > 0)
			{
				// (c) Atomic AS-IS snapshot of the registry + checkpoints.
				reactionsSnapshot = source.ReadReactions();

				// (d) Elision markers in the range [previousWatermark + 1, newHead].
				// Only if there is a valid range (records.Count > 0 guarantees newHead > previousWatermark
				// but we force the defensive check).
				if (newHead > previousWatermark)
				{
					elisionMarkers = source.ReadElidedRange(previousWatermark + 1, newHead);
				}
			}

			// (b) Confirm — Max-monotonic. Only if we advanced. If records is empty,
			// there is nothing to confirm (silent no-op, natural recovery).
			bool watermarkAdvanced = false;
			if (newHead > previousWatermark)
			{
				watermarkAdvanced = source.ConfirmUntil(newHead);
				watermark = newHead;
			}

			stopwatch.Stop();
			LabInstrumentation.OnMaterializeSync?.Invoke(source.DestinationName, previousWatermark, watermark, stopwatch.ElapsedTicks);

			Action<string, long, long> recordCallback = LabInstrumentation.OnMaterializeRecordApplied;
			if (recordCallback != null && records.Count > 0)
			{
				string destinationName = source.DestinationName;
				foreach (var rec in records)
				{
					long approximateBytes = ApproximateRecordBytes(rec);
					recordCallback(destinationName, rec.EntryId, approximateBytes);
				}
			}

			return new MirrorSyncResult(
				records: records,
				reactionsSnapshot: reactionsSnapshot,
				elisionMarkers: elisionMarkers,
				previousWatermark: previousWatermark,
				newWatermark: watermark,
				watermarkAdvanced: watermarkAdvanced,
				includedCapa2: includeCapa2);
		}

		// Approximate bytes-on-wire for a MaterializationRecord. Used by
		// LabInstrumentation.OnMaterializeRecordApplied to feed sync_samples.csv.
		// Fixed overhead (24 bytes: 8 EntryId + 1 Kind + 8 OccurredAt ticks + 4
		// ActionId + small framing) plus UTF-16 char counts for the string fields.
		// Conservative — favors slight over-count on framing.
		private static long ApproximateRecordBytes(MaterializationRecord record)
		{
			long bytes = 24;
			if (record.Script != null) bytes += record.Script.Length * 2;
			if (record.Arguments != null) bytes += record.Arguments.Length * 2;
			if (record.DefineStatementText != null) bytes += record.DefineStatementText.Length * 2;
			if (record.ExposeData != null) bytes += record.ExposeData.Length * 2;
			return bytes;
		}
	}

	// Intermediary builder between AsProgramMirror() and Sync(). Exists so the
	// signed D1 pattern (`mirror.AsProgramMirror().Sync()`) has the `Sync()` verb
	// as a natural terminator, parallel to the simple `mirror.Sync()`. The
	// includeCapa2 flag is per-call, it does not persist in the mirror — consistent
	// with the DistillCommand pattern.
	public class MaterializeMirrorBuilder
	{
		private readonly MaterializeMirror mirror;

		internal MaterializeMirrorBuilder(MaterializeMirror mirror)
		{
			ArgumentNullException.ThrowIfNull(mirror);
			this.mirror = mirror;
		}

		public MirrorSyncResult Sync()
		{
			return mirror.SyncInternal(includeCapa2: true);
		}
	}

	// Result of a .Sync() cycle. Immutable. Public so the caller can inspect what
	// was received and decide what to apply locally. PreviousWatermark and
	// NewWatermark are both present to allow auditing of the advance.
	public readonly struct MirrorSyncResult
	{
		public IReadOnlyList<MaterializationRecord> Records { get; }
		// ReactionsSnapshot is null if IncludedCapa2 is false (the destination
		// requested Layer 1 only). If IncludedCapa2 is true but records is empty, it
		// also stays null (the call to (c) was not made, as an optimization).
		public MaterializationReactionsSnapshot? ReactionsSnapshot { get; }
		public IReadOnlyList<MaterializationElisionMarker> ElisionMarkers { get; }
		public long PreviousWatermark { get; }
		public long NewWatermark { get; }
		public bool WatermarkAdvanced { get; }
		public bool IncludedCapa2 { get; }

		internal MirrorSyncResult(
			IReadOnlyList<MaterializationRecord> records,
			MaterializationReactionsSnapshot? reactionsSnapshot,
			IReadOnlyList<MaterializationElisionMarker> elisionMarkers,
			long previousWatermark,
			long newWatermark,
			bool watermarkAdvanced,
			bool includedCapa2)
		{
			ArgumentNullException.ThrowIfNull(records);
			ArgumentNullException.ThrowIfNull(elisionMarkers);
			this.Records = records;
			this.ReactionsSnapshot = reactionsSnapshot;
			this.ElisionMarkers = elisionMarkers;
			this.PreviousWatermark = previousWatermark;
			this.NewWatermark = newWatermark;
			this.WatermarkAdvanced = watermarkAdvanced;
			this.IncludedCapa2 = includedCapa2;
		}
	}
}
