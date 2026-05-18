using Puppeteer.EventSourcing.DB;
using System.Collections.Generic;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Fase 4 (firmado D1 2026-05-13). Interfaz del
	// destination side al primary side. Abstrae los 4 wire verbs (a)/(b)/(c)/(d):
	//
	//   (a) ReadRecordsAfter — Capa 1 (records raw).
	//   (b) ConfirmUntil      — ack Max-monotonic al primary.
	//   (c) ReadReactions     — Capa 2a (registry + checkpoints snapshot).
	//   (d) ReadElidedRange   — Capa 2b (elision markers).
	//
	// El destination process en produccion implementa esto como HTTP client al
	// primary (fuera de scope I1). Tests in-process usan LocalMaterializeSource
	// para envolver un primary actor.Materialization sin transport real.
	//
	// MaterializeMirror orquesta los 4 verbs via esta interface — agnostico de
	// transport.
	public interface IMaterializeSource
	{
		// El destination symbolic name que este source representa (usado para
		// log/debug; el primary recibe este string en todas las llamadas).
		string DestinationName { get; }

		IReadOnlyList<MaterializationRecord> ReadRecordsAfter(long fromEntryId);

		MaterializationReactionsSnapshot ReadReactions();

		IReadOnlyList<MaterializationElisionMarker> ReadElidedRange(long fromEntryId, long toEntryId);

		bool ConfirmUntil(long entryId);
	}
}
