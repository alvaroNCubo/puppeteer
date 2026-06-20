using Puppeteer.EventSourcing.DB;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Fase 4 (firmado D1 2026-05-13). Cliente del
	// destination side que orquesta los 4 wire verbs del primary side. Patron
	// fluido firmado:
	//
	//   mirror.Sync();                       // Capa 1 — orquesta (a) + (b).
	//   mirror.AsProgramMirror().Sync();     // Capa 2 — orquesta (a) + (c) + (d) + (b).
	//
	// Sync agnostico de transport: el caller provee IMaterializeSource que abstrae
	// HTTP / in-process / loopback. MaterializeMirror solo coordina el ciclo y
	// actualiza el watermark local.
	//
	// IMPORTANTE — diseño firmado en Fase 4: MaterializeMirror NO aplica los
	// datos fetched a un storage local. Retorna MirrorSyncResult con todo lo
	// recibido — el caller decide que hacer con los records / reactions /
	// elision markers. Esto separa "fetch + confirm" (responsabilidad del
	// mirror cliente) de "apply locally" (responsabilidad del operador del
	// destination que sabe si tiene un journal local, replicacion async,
	// pure passive consumer, etc.). La aplicacion local es Hueco para Fase
	// futura cuando se firme el modelo destination-side completo.
	public class MaterializeMirror
	{
		private readonly IMaterializeSource source;
		private long watermark;

		// Watermark actual: el ultimo EntryId confirmado al primary via (b).
		// Inicia en startingFrom (default 0); avanza con cada Sync exitoso.
		public long Watermark => watermark;

		public IMaterializeSource Source => source;

		public MaterializeMirror(IMaterializeSource source, long startingFrom = 0)
		{
			ArgumentNullException.ThrowIfNull(source);
			if (startingFrom < 0) throw new LanguageException($"startingFrom {startingFrom} must be zero or greater.");
			this.source = source;
			this.watermark = startingFrom;
		}

		// Capa 1 sync: orquesta (a) ReadRecordsAfter + (b) ConfirmUntil. No
		// incluye reaction state ni elision markers — el caller solo recibe
		// los records raw. Util para destination que NO necesita re-ejecutar
		// reactions localmente (e.g. archive-only mirror).
		public MirrorSyncResult Sync()
		{
			return SyncInternal(includeCapa2: false);
		}

		// Activa Capa 2 para el siguiente .Sync() — agrega (c) + (d) al
		// orchestracion. Decision D1 firmada: el destination que quiere
		// reconstruir program state (reactions + elision) opt-in expresso
		// via este patron.
		public MaterializeMirrorBuilder AsProgramMirror()
		{
			return new MaterializeMirrorBuilder(this);
		}

		internal MirrorSyncResult SyncInternal(bool includeCapa2)
		{
			long previousWatermark = watermark;
			Stopwatch stopwatch = Stopwatch.StartNew();

			// (a) Capa 1 — records raw desde el watermark actual.
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
				// (c) Snapshot atomic AS-IS del registry + checkpoints.
				reactionsSnapshot = source.ReadReactions();

				// (d) Elision markers en el rango [previousWatermark + 1, newHead].
				// Solo si hay un rango valido (records.Count > 0 garantiza newHead > previousWatermark
				// pero forzamos el chequeo defensivo).
				if (newHead > previousWatermark)
				{
					elisionMarkers = source.ReadElidedRange(previousWatermark + 1, newHead);
				}
			}

			// (b) Confirm — Max-monotonic. Solo si avanzamos. Si records vacio,
			// no hay nada que confirmar (no-op silencioso, recovery natural).
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

	// Builder intermediario entre AsProgramMirror() y Sync(). Existe para que
	// el patron firmado D1 (`mirror.AsProgramMirror().Sync()`) tenga el verbo
	// `Sync()` como terminator natural, paralelo a `mirror.Sync()` simple. El
	// flag includeCapa2 es per-call, no persiste en el mirror — coherente con
	// patron de DistillCommand.
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

	// Resultado de un .Sync() ciclo. Inmutable. Public para que el caller
	// inspeccione lo recibido y decida que aplicar localmente. PreviousWatermark
	// y NewWatermark estan ambos presentes para permitir auditoria del avance.
	public readonly struct MirrorSyncResult
	{
		public IReadOnlyList<MaterializationRecord> Records { get; }
		// ReactionsSnapshot es null si IncludedCapa2 es false (el destination
		// pidio Capa 1 solo). Si IncludedCapa2 es true pero records vacio, queda
		// tambien null (no se hizo la llamada a (c) por optimizacion).
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
