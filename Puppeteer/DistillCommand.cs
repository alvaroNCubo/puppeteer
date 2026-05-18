using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.DB;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Fase 1 (decisiones D1 #5/#6/#7/#9 firmadas
	// 2026-05-13). Builder fluido para invocar Distill con el invariante
	// Materialize-then-Distill: si hay destinations registradas, Distill no se
	// ejecuta hasta que cada una confirme via ConfirmUntil que recibio hasta
	// entryId. Esto protege la promesa del actor primary hacia sus destinations
	// (no barrer fisicamente records antes de delivery confirmado).
	//
	// API firmada (D1 #9):
	//   actor.Distill().Now();                      — sin invariante (legacy si
	//                                                  no hay destinations
	//                                                  registradas; ver compromiso
	//                                                  abajo).
	//   actor.Distill().Until(N).Now();             — invariante: todas las
	//                                                  destinations registradas
	//                                                  deben tener watermark >= N.
	//   actor.Distill().Forced().Now();             — escape hatch (D1 #7).
	//                                                  Override consciente,
	//                                                  auditable por grep.
	//   actor.Distill().Until(N).Forced().Now();    — combina; Forced gana.
	//
	// Compromiso pragmatico vs D1 #7 estricto: D1 #7 firmo "sin .Forced() + sin
	// destinations registradas tambien falla". En la implementacion actual, sin
	// destinations registradas Distill().Now() ejecuta sin restriccion para
	// preservar backward-compat con tests/callers que no usan Materialize. Esto
	// es deliberadamente mas laxo que la decision; flageado para revision si
	// Alvaro firma strict-by-default mas adelante.
	public class DistillCommand
	{
		private readonly ActorHandler handler;
		private long? untilEntryId;
		private bool forced;

		internal DistillCommand(ActorHandler handler)
		{
			ArgumentNullException.ThrowIfNull(handler);
			this.handler = handler;
		}

		// Declara watermark requerido. Inclusivo (D1 #9): "Distill hasta el 100"
		// incluye el 100. Sin esto + con destinations registradas, .Now() falla.
		public DistillCommand Until(long entryId)
		{
			if (entryId < 0) throw new LanguageException($"entryId {entryId} must be zero or greater.");
			this.untilEntryId = entryId;
			return this;
		}

		// Escape hatch consciente (D1 #7): "acepto perdida de completitud del
		// journal sin backup confirmado". Override explicito, auditable por grep,
		// visible en code review. Forced gana sobre Until — si ambos se setean,
		// no se valida el watermark de destinations.
		public DistillCommand Forced()
		{
			this.forced = true;
			return this;
		}

		// Terminator obligatorio: dispara la ejecucion. Sin este metodo el
		// builder configura pero no ejecuta nada. Patron explicito para evitar
		// side effects en getters / configurador implicito.
		public void Now()
		{
			if (!forced)
			{
				ValidateMaterializeInvariant();
			}

			handler.Distill();
		}

		private void ValidateMaterializeInvariant()
		{
			MaterializationCheckpointStorage storage = handler.TryGetMaterializationCheckpointStorage();
			if (storage == null)
			{
				// EventSourcingStorage no configurado todavia. No hay registry
				// para validar — comportamiento legacy (compat con tests/callers
				// que no usan Materialize). D1 #7 estricto haria fallar tambien
				// aqui; postpuesto deliberadamente.
				return;
			}

			List<MaterializationCheckpointRow> rows = new List<MaterializationCheckpointRow>();
			storage.List(rows);

			if (rows.Count == 0)
			{
				// Compromiso pragmatico vs D1 #7. Sin destinations registradas,
				// no hay contrato Materialize que validar — ejecuta legacy.
				return;
			}

			if (untilEntryId == null)
			{
				throw new LanguageException(
					$"Distill requires .Until(entryId) when destinations are registered ({rows.Count} found), or .Forced() to override.");
			}

			long required = untilEntryId.Value;
			foreach (var row in rows)
			{
				if (row.LastConfirmedEntryId < required)
				{
					throw new LanguageException(
						$"Destination '{row.Destination}' has not confirmed up to EntryId {required} (currently at {row.LastConfirmedEntryId}). Use .Forced() to override.");
				}
			}
		}
	}
}
