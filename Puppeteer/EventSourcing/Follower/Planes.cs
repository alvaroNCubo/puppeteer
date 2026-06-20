using System;

namespace Puppeteer.EventSourcing.Follower
{
	// The three planes a Reaction's Action can touch. Each plane exposes
	// the leaf verb that completes the Reaction definition. The plane
	// describes what the verb addresses (the system surface) — it is not
	// a configuration of the builder, so it is a property on the surface
	// type, not a method.
	//
	// Program   — `.Program.Emit(script[, when: check])` — read-only
	//             execution against the actor's libraries (PerformEmit,
	//             optionally guarded by PerformChk).
	// Causation — `.Causation.Continue(script)` — the Action body extends
	//             the program flow into another actor (the script contains
	//             a `tell`). Runs under InReactionAction so the runtime
	//             gate accepts the tell statement.
	// Metadata  — `.Metadata.Elide()` — mark the matched entries as elided
	//             in the journal. `.Metadata.Materialize(destination)` —
	//             declare the matched entries materialized to the given
	//             destination (Paper 5 claim 4). Both verbs are journal-
	//             level bookkeeping with no state change or causation.
	//
	// Each Reaction has exactly one Action. Calling a leaf verb after one
	// has already been set throws — the build-time check lives in the
	// internal Reaction.SetProgramAction / SetCausationAction /
	// SetMetadataAction helpers.

	public sealed class ProgramPlane
	{
		private readonly Reaction reaction;

		internal ProgramPlane(Reaction reaction)
		{
			ArgumentNullException.ThrowIfNull(reaction);
			this.reaction = reaction;
		}

		// Emit a script for read-only execution against the actor's
		// libraries. The script does not journal a new entry and does not
		// dispatch envelopes. Use this for projections, exposure, derived
		// signals — anything that reads from the actor's program without
		// changing it.
		public void Emit(string script)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(script);
			reaction.SetProgramAction(script, when: null);
		}

		// Same as Emit(script), but only runs the script when the `when`
		// check returns OK. Replaces the old `Using(check, cmd).Emit()`
		// two-script terminator.
		public void Emit(string script, string when)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(script);
			ArgumentException.ThrowIfNullOrWhiteSpace(when);
			reaction.SetProgramAction(script, when);
		}
	}

	public sealed class CausationPlane
	{
		private readonly Reaction reaction;

		internal CausationPlane(Reaction reaction)
		{
			ArgumentNullException.ThrowIfNull(reaction);
			this.reaction = reaction;
		}

		// Continue the program flow into another actor. The script must
		// contain a `tell` statement (the framework's TellStatement
		// runtime gate ensures this is the only legal place a tell can
		// fire). The Action body journals as a script entry on this
		// actor's log; the envelope reaches the configured Transport.
		public void Continue(string script)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(script);
			reaction.SetCausationAction(script);
		}

		// Variante con check: el tell del script entrega al RECEPTOR un
		// CheckThenCommand — el check (predicado DSL) se evalua contra el estado
		// del receptor, no aqui (el origen siempre cumpliria). Asi un fan-out
		// replicado es idempotente: el nodo que ya creo el efecto absorbe el
		// echo. El check viaja en TellEnvelope.Check. Firma pensada para
		// `Continue(check: "...", "tell ...")`.
		public void Continue(string check, string script)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(check);
			ArgumentException.ThrowIfNullOrWhiteSpace(script);
			reaction.SetCausationAction(script, check);
		}
	}

	public sealed class MetadataPlane
	{
		private readonly Reaction reaction;

		internal MetadataPlane(Reaction reaction)
		{
			ArgumentNullException.ThrowIfNull(reaction);
			this.reaction = reaction;
		}

		// Mark the entries that complete this Reaction's pattern as
		// elided in the journal. Replaces the old `MarkAsSkip()` verb.
		// The framework already used the term `MarkEventsAsElided` in
		// EventElisionStorage; the surface verb now matches.
		public void Elide()
		{
			reaction.SetMetadataAction(MetadataKind.Elide, destination: null);
		}

		// F4: elisión selectiva. Elide(seek: "X") elide solo los entryIds del Seek "X"
		// del match (p.ej. la compra ancla, sin sus confirms). Elide(seeks: "A","B")
		// elide varios. Sin argumentos, Elide() elide la cadena completa del match.
		public void Elide(string seek)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(seek);
			reaction.SetMetadataAction(MetadataKind.Elide, destination: null, elideSeeks: new[] { seek });
		}

		public void Elide(params string[] seeks)
		{
			ArgumentNullException.ThrowIfNull(seeks);
			if (seeks.Length == 0) throw new LanguageException("Elide(seeks:) requiere al menos un Seek; usa Elide() para la cadena completa.");
			foreach (string s in seeks) ArgumentException.ThrowIfNullOrWhiteSpace(s);
			reaction.SetMetadataAction(MetadataKind.Elide, destination: null, elideSeeks: seeks);
		}

		// Declare that the entries completing this Reaction's pattern
		// materialize to the given destination. The runtime calls
		// EventMaterializationStorage.MarkEventsAsMaterialized; a
		// delivery worker external to the actor consumes the resulting
		// (DiaryId, ReactionId, Destination) rows. Paper 5 claim 4: the
		// program declares which events materialize to which destinations;
		// the operation falls out of the substrate.
		public void Materialize(string destination)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(destination);
			reaction.SetMetadataAction(MetadataKind.Materialize, destination);
		}
	}
}
