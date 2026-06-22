using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Follower
{
	// Three planes the Reaction's Action can address. The plane describes
	// what the verb touches, not the role of the verb:
	//
	//   Program   — derive a signal from the actor's program (read-only).
	//               Optionally guarded by a check; the guard is a property
	//               of the Action via CheckScript, not a separate plane.
	//   Causation — extend the program flow into another actor (cross-actor
	//               tell). Runs under ActorHandler.InReactionAction so the
	//               TellStatement runtime gate is satisfied.
	//   Metadata  — alter the journal's structure (mark observed entries as
	//               elided, or declare them materialized to a destination).
	//               No state change, no causation — just journal-level
	//               bookkeeping. The MetadataKind sub-distinction lives on
	//               the Reaction so the plane stays single.
	//
	// `None` means the Reaction was defined without a terminator (build-
	// time error path).
	internal enum ReactionActionType
	{
		None,
		Program,
		Causation,
		Metadata,
		// Journal-outbox emit: a write-mode emit that RECORDS the outgoing message
		// (destination + payload + deterministic idempotency key) into the diary's
		// outbox table, committed atomically with the reaction cursor advance. A
		// relay delivers it at-least-once. Distinct from the read-only Program
		// plane — it touches the journal, so it is never the isQuery PerformEmit
		// path. See notes/reactions-outbox-emit.md.
		Outbox
	}

	// Sub-distinction inside the Metadata plane: which journal-level
	// bookkeeping verb the developer chose. Lives on the Reaction (not the
	// ReactionAction) because the destination string for Materialize is
	// also Reaction-scoped.
	internal enum MetadataKind
	{
		None,
		Elide,
		Materialize
	}

	internal class ReactionAction
	{
		internal ReactionActionType ActionType { get; set; }
		internal MetadataKind MetadataKind { get; set; }
		internal string Script { get; set; }
		internal string CheckScript { get; set; }
		internal List<long> EventIdsToSkip { get; set; }

		// F4 Elide(seek:/seeks:): names of the Seeks whose entryIds are elided. null =
		// the full match chain (the default behavior of Elide()). When this is set,
		// CollectEventIdsFromChain only collects the ids of the nodes whose Seek
		// (Engine.PatternDescription) is in this set.
		internal string[] ElideTargetSeeks { get; set; }

		// True when ActionType == Program and a `when:` check script is
		// configured. The executor runs CheckScript via PerformChk before
		// running Script via PerformEmit; if the check fails, the emit is
		// skipped. Equivalent to the old EmitWithCheck branch, now folded
		// into Program with a flag.
		internal bool HasCheck => ActionType == ReactionActionType.Program && !string.IsNullOrEmpty(CheckScript);

		internal ReactionAction()
		{
			ActionType = ReactionActionType.None;
			MetadataKind = MetadataKind.None;
			EventIdsToSkip = new List<long>();
		}

		internal void Reset()
		{
			ActionType = ReactionActionType.None;
			MetadataKind = MetadataKind.None;
			Script = null;
			CheckScript = null;
			ElideTargetSeeks = null;
			EventIdsToSkip.Clear();
		}
	}
}
