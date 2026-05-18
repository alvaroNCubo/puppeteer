using System;
using System.Threading;

namespace Puppeteer
{
	public static class LabInstrumentation
	{
		public static Action<long> OnCompileElapsedTicks;
		public static Action<long> OnEvalCompileElapsedTicks;

		// Paper 5 Lab 1 — red-black handoff measurements.
		public static Action<long> OnRedBlackHandoffElapsedTicks;
		public static Action<long> OnReplayEventCounted;
		public static Action<string> OnHandoverStarted;
		public static Action<string> OnHandoverCompleted;

		// Paper 5 Lab 3 — DC B symmetric Reactions parity.
		// Fires from Reaction.ExecuteAction when an Emit / EmitWithCheck
		// terminator dispatches. Args:
		//   triggeringEntryId — the entryId of the event that closed the
		//                        match (leafNode.EntryId in MatchTree).
		//   actionOrScript    — the rendered emit script (PerformEmit input).
		//   payloadHash8      — SHA-256 of the matched parameters, truncated
		//                        to 8 bytes for O(1) parity comparison.
		public static Action<long, string, byte[]> OnReactionEmit;

		// Paper 5 Lab 3 — fires from Reaction.ExecuteAction's case MarkAsSkip
		// after MarkEventsAsElided flushes the matched event-id batch. Args:
		//   elidedEventIds — the snapshot of EventIdsToSkip just flushed.
		//   reactionId     — the persistent ReactionId on the host.
		public static Action<long[], int> OnReactionElide;

		// Paper 5 Lab 3 — fires from Reaction.ExecuteAction's case Tell after
		// the wrapped PerformCmd dispatches the tell envelope. Args:
		//   triggeringEntryId — leafNode.EntryId of the match.
		//   targetActor       — destination actor name (rendered into the Tell
		//                        body; "" if not extractable here).
		//   payloadHash8      — SHA-256 of the matched parameters, truncated
		//                        to 8 bytes.
		public static Action<long, string, byte[]> OnReactionTell;

		// Paper 5 Lab 4 — passive consumer / Materialize v2 native.
		// Wall-clock of the full MaterializeMirror.SyncInternal cycle:
		// (a) ReadRecordsAfter + [c+d if Capa 2] + (b) ConfirmUntil.
		// Args: (destination, fromEntryId, toEntryId, elapsedTicks).
		public static Action<string, long, long, long> OnMaterializeSync;

		// Paper 5 Lab 4 — catch-up window measurement.
		// Wall-clock of ActorHandler.CatchUpFromJournal after a simulated
		// retention gap. Args: (fromEntryId, toEntryId, elapsedTicks).
		public static Action<long, long, long> OnMaterializeCatchUp;

		// Paper 5 Lab 4 — per-record dispatch after MirrorSyncResult is
		// bundled. The harness uses this to count records applied per
		// destination and approximate the bytes transferred for the
		// sync_samples.csv dataset.
		// Args: (destination, entryId, approximateBytes).
		public static Action<string, long, long> OnMaterializeRecordApplied;

		private static long _parsersRentHits;
		private static long _parsersRentMisses;
		private static long _parametersRentHits;
		private static long _parametersRentMisses;
		private static long _replayEventsCounted;

		public static long ParsersRentHits => Interlocked.Read(ref _parsersRentHits);
		public static long ParsersRentMisses => Interlocked.Read(ref _parsersRentMisses);
		public static long ParametersRentHits => Interlocked.Read(ref _parametersRentHits);
		public static long ParametersRentMisses => Interlocked.Read(ref _parametersRentMisses);
		public static long ReplayEventsCounted => Interlocked.Read(ref _replayEventsCounted);

		internal static void IncrementParsersRentHit() => Interlocked.Increment(ref _parsersRentHits);
		internal static void IncrementParsersRentMiss() => Interlocked.Increment(ref _parsersRentMisses);
		internal static void IncrementParametersRentHit() => Interlocked.Increment(ref _parametersRentHits);
		internal static void IncrementParametersRentMiss() => Interlocked.Increment(ref _parametersRentMisses);

		public static void ResetPoolCounters()
		{
			Interlocked.Exchange(ref _parsersRentHits, 0);
			Interlocked.Exchange(ref _parsersRentMisses, 0);
			Interlocked.Exchange(ref _parametersRentHits, 0);
			Interlocked.Exchange(ref _parametersRentMisses, 0);
		}

		public static void ResetRedBlackCounters()
		{
			Interlocked.Exchange(ref _replayEventsCounted, 0);
		}

		public static void IncrementReplayEventsCounted() => Interlocked.Increment(ref _replayEventsCounted);
	}
}
