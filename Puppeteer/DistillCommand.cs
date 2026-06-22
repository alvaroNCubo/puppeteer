using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.DB;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Paper 5 / Materialize v2 — Phase 1 (decisions D1 #5/#6/#7/#9 signed
	// 2026-05-13). Fluent builder to invoke Distill with the Materialize-then-Distill
	// invariant: if there are registered destinations, Distill does not run until
	// each one confirms via ConfirmUntil that it received up to entryId. This
	// protects the primary actor's promise toward its destinations (do not physically
	// sweep records before confirmed delivery).
	//
	// Signed API (D1 #9):
	//   actor.Distill().Now();                      — without invariant (legacy if
	//                                                  there are no registered
	//                                                  destinations; see the
	//                                                  compromise below).
	//   actor.Distill().Until(N).Now();             — invariant: all registered
	//                                                  destinations must have
	//                                                  watermark >= N.
	//   actor.Distill().Forced().Now();             — escape hatch (D1 #7).
	//                                                  Conscious override,
	//                                                  auditable by grep.
	//   actor.Distill().Until(N).Forced().Now();    — combines; Forced wins.
	//
	// Pragmatic compromise vs strict D1 #7: D1 #7 signed "without .Forced() + with no
	// registered destinations it also fails". In the current implementation, with no
	// registered destinations Distill().Now() runs without restriction to preserve
	// backward-compat with tests/callers that do not use Materialize. This is
	// deliberately more lax than the decision; flagged for review if strict-by-default
	// is signed later.
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

		// Declares the required watermark. Inclusive (D1 #9): "Distill up to 100"
		// includes 100. Without this + with registered destinations, .Now() fails.
		public DistillCommand Until(long entryId)
		{
			if (entryId < 0) throw new LanguageException($"entryId {entryId} must be zero or greater.");
			this.untilEntryId = entryId;
			return this;
		}

		// Conscious escape hatch (D1 #7): "I accept loss of journal completeness
		// without confirmed backup". Explicit override, auditable by grep, visible
		// in code review. Forced wins over Until — if both are set, the destinations'
		// watermark is not validated.
		public DistillCommand Forced()
		{
			this.forced = true;
			return this;
		}

		// Mandatory terminator: triggers execution. Without this method the
		// builder configures but executes nothing. Explicit pattern to avoid
		// side effects in getters / implicit configurator.
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
				// EventSourcingStorage not configured yet. There is no registry
				// to validate — legacy behavior (compat with tests/callers that
				// do not use Materialize). Strict D1 #7 would fail here too;
				// deliberately postponed.
				return;
			}

			List<MaterializationCheckpointRow> rows = new List<MaterializationCheckpointRow>();
			storage.List(rows);

			if (rows.Count == 0)
			{
				// Pragmatic compromise vs D1 #7. With no registered destinations,
				// there is no Materialize contract to validate — runs legacy.
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
