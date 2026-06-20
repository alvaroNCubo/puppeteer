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

		// --- Timing por etapa del pipeline de rehidratacion ----------------------------
		// El pipeline corre 3 etapas concurrentes (parser -> resolver -> exec). El
		// wall-clock total ~= max(tiempo de cada etapa) + fill/drain, asi que la etapa
		// con mas tiempo acumulado de trabajo es el cuello de botella. Estos acumuladores
		// suman el tiempo (en ticks de Stopwatch) que cada etapa pasa DENTRO de su trabajo
		// por-entry (parse / SolveReferences / Perform), no el tiempo bloqueada en la cola.
		//
		// Gateado por StageTimingEnabled para no pagar Stopwatch.GetTimestamp() por entry
		// en produccion. El host de medicion lo enciende, corre la rehidratacion, y lee
		// ParseElapsedMs / ResolveElapsedMs / ExecuteElapsedMs.
		// Tambien se activa SIN tocar codigo via la variable de entorno
		// PUPPETEER_STAGE_TIMING=1: con eso ActorHandler.EventSourcingStorage imprime el
		// desglose por etapa de cada rehidratacion al stdout (ver el Console.WriteLine alla).
		// Pensado para que DevOps corra el respaldo y nos pase los numeros sin compilar nada.
		public static bool StageTimingEnabled =
			Environment.GetEnvironmentVariable("PUPPETEER_STAGE_TIMING") == "1";

		private static long _parseTicks;
		private static long _resolveTicks;
		private static long _executeTicks;

		internal static void AddParseTicks(long ticks) => Interlocked.Add(ref _parseTicks, ticks);
		internal static void AddResolveTicks(long ticks) => Interlocked.Add(ref _resolveTicks, ticks);
		internal static void AddExecuteTicks(long ticks) => Interlocked.Add(ref _executeTicks, ticks);

		private static double TicksToMs(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

		public static long ParseTicks => Interlocked.Read(ref _parseTicks);
		public static long ResolveTicks => Interlocked.Read(ref _resolveTicks);
		public static long ExecuteTicks => Interlocked.Read(ref _executeTicks);

		public static double ParseElapsedMs => TicksToMs(Interlocked.Read(ref _parseTicks));
		public static double ResolveElapsedMs => TicksToMs(Interlocked.Read(ref _resolveTicks));
		public static double ExecuteElapsedMs => TicksToMs(Interlocked.Read(ref _executeTicks));

		// --- Contadores del cache de resolucion de metodo (DotAccess.FindMethodCached) ------
		// Senal DETERMINISTICA (independiente de la carga de la maquina, a diferencia del
		// wall-clock por etapa): responde si el cache se usa de verdad. Si Uncacheable domina
		// (argTypes null/object por imprecision de tipos en V1), el cache nunca pega y solo
		// agrega overhead -> habria que clavear por tipos runtime de los valores ya evaluados.
		// Se incrementan solo con StageTimingEnabled para no pagar en produccion.
		private static long _methodCacheHits;
		private static long _methodCacheMisses;
		private static long _methodCacheUncacheable;

		public static long MethodCacheHits => Interlocked.Read(ref _methodCacheHits);
		public static long MethodCacheMisses => Interlocked.Read(ref _methodCacheMisses);
		public static long MethodCacheUncacheable => Interlocked.Read(ref _methodCacheUncacheable);

		internal static void IncrementMethodCacheHit() => Interlocked.Increment(ref _methodCacheHits);
		internal static void IncrementMethodCacheMiss() => Interlocked.Increment(ref _methodCacheMisses);
		internal static void IncrementMethodCacheUncacheable() => Interlocked.Increment(ref _methodCacheUncacheable);

		public static void ResetStageTicks()
		{
			Interlocked.Exchange(ref _parseTicks, 0);
			Interlocked.Exchange(ref _resolveTicks, 0);
			Interlocked.Exchange(ref _executeTicks, 0);
			Interlocked.Exchange(ref _methodCacheHits, 0);
			Interlocked.Exchange(ref _methodCacheMisses, 0);
			Interlocked.Exchange(ref _methodCacheUncacheable, 0);
		}
	}
}
