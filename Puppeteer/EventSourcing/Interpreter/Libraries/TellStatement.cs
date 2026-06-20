using Puppeteer.EventSourcing.Follower;
using Puppeteer.Tell;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// Family of statements that materialise the cross-actor `tell` primitive in the
	// DSL. Plan 2 covered parsing + AST + render. Plan 3 added domain-library
	// validation in parse-time. Plan 5 (this plan) wires Execute() to the journal
	// and the transport: the rendered sentence reaches the journal automatically
	// via Statement.Write (program.ConvertToString → dairy.WriteScriptEntryAsync),
	// and the produced TellEnvelope is enqueued in symbolTable.PendingTells so that
	// ActorHandler.PerformCmdAsync can drain it post-commit, outside the write lock.
	internal abstract class TellStatement : Statement
	{
		private readonly SymbolTable symbolTable;

		// Optional id literal written by the developer. If null, Execute() assigns
		// a stable deterministic hash so replay does not re-emit.
		internal string IdLiteral { get; }

		// Optional 'through' transport hint written by the developer. The journal
		// omits it by default — only renders when present.
		internal string ThroughLiteral { get; }

		private protected TellStatement(SymbolTable symbolTable, string idLiteral, string throughLiteral)
		{
			ArgumentNullException.ThrowIfNull(symbolTable);
			this.symbolTable = symbolTable;
			IdLiteral = idLiteral;
			ThroughLiteral = throughLiteral;
		}

		private protected SymbolTable SymbolTable => symbolTable;

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			throw new NotImplementedException("Tell compiled-mode execution is not implemented yet. Force AlwaysInterpreted mode on actors that need to participate in tells until a later plan adds compiled-mode support.");
		}

		internal override void ValidateStatically()
		{
			// Domain-library validation runs in parse-time (Plan 3).
		}

		// Plan 7 of the Tell primitive roadmap: registration of tell sentences
		// in the patternAst is delegated to subclasses — they know which fields
		// to evaluate. The base class is a no-op so subclasses without
		// pattern-matchable shape (e.g. saga tells in Plan 8) can stay silent.
		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
		}

		// Subclass helpers ---------------------------------------------------

		private protected void EnsureTransportConfigured()
		{
			if (symbolTable.ActorHandler == null)
			{
				throw new LanguageException("Tell statement attempted to execute without an ActorHandler reachable from the SymbolTable. This is a framework wiring error — file an issue.");
			}

			// Shadow isolation (S1): a shadow produces zero external effect, so it
			// does not require a configured Transport. The tell still runs its match
			// and builds the envelope (journaled in the shadow's own storage), but
			// the envelope is dropped at the drain step (ActorHandler PerformCmd) and
			// never delivered to the real target actor.
			if (symbolTable.ActorHandler.IsShadow) return;

			if (symbolTable.ActorHandler.Transport == null)
			{
				throw new LanguageException("Tell statement attempted to execute on an actor without a configured Transport. Set actor.Handler.Transport before issuing 'tell' statements (or remove the statement from the script).");
			}
		}

		// Tell is a Reaction-Action-only statement. Cross-actor causation
		// is a consequence of an intra-actor event observed by a Reaction,
		// not a command/query primitive. Allowing `tell` from
		// PerformCommand / PerformQuery would let any caller dispatch
		// outbound traffic outside the observer pattern — breaking the
		// discipline that makes the journal a faithful catalog of what the
		// actor decided to say. Run inside a Reaction's .Causation.Continue(...)
		// body or remove the statement.
		private protected void EnsureInReactionAction()
		{
			if (!symbolTable.ActorHandler.InReactionAction)
			{
				throw new LanguageException("'tell' is only valid inside the .Causation.Continue(...) Action of a Reaction. It cannot be issued from a top-level PerformCommand, PerformCheckThenCommand, or PerformQuery — cross-actor dispatch must be observed by a Reaction over an intra-actor event. Move the tell into a Reaction's .Causation.Continue(...) body, or remove it from this script.");
			}
		}

		// Records the originating entry id for an envelope id so the ack-side
		// elision (Plan 6 (A)) can find this tell when the matching ack arrives.
		// Also marks the entry as single-tell-eligible when the program contains
		// exactly one TellStatement — the framework only emits MarkAsSkip on the
		// pair when both entries are single-statement (entry-coarse elision API).
		private protected void RegisterTellEntryForElision(string envelopeId)
		{
			if (Program == null) return; // No program context (e.g. unit tests rendering directly) → cannot wire elision.
			long entryId = Program.EntryId;
			if (entryId <= 0) return; // No entry id assigned (e.g. PerformQuery or ad-hoc execution) → no elision possible.
			SymbolTable.RegisterTellEnvelopeEntry(envelopeId, entryId);
			if (Program.HasSingleTellStatement)
			{
				SymbolTable.MarkSingleTellEntry(entryId);
			}
		}

		// Render the command-call text for the journal sentence + envelope.CommandText.
		// This IS allocating (StringBuilder + string). It is unavoidable: the receiving
		// side parses command-call text, and the journal sentence has to be a string
		// for the existing dairy.WriteScriptEntry pipeline. The allocation is made
		// once per outbound tell — never on the dedup-lookup hot path.
		private protected static string RenderCommandCall(string commandName, AstExpression[] commandArgs)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append(commandName);
			sb.Append("(");
			for (int i = 0; i < commandArgs.Length; i++)
			{
				if (i > 0) sb.Append(", ");
				commandArgs[i].write(sb, DatabaseType.IN_MEMORY);
			}
			sb.Append(")");
			return sb.ToString();
		}

		// Evaluate the target-id expression to a runtime value. The downstream code
		// branches on the returned object directly (string / int / long / etc.) so
		// no defensive ToString() is performed here — that would alloc on the hot
		// path for non-string ids. envelope.TargetId stays string-shaped because
		// the transport serialises it; we render to string only when constructing
		// the envelope for an outbound send (never on replay).
		private protected static object EvaluateTargetId(AstExpression targetId)
		{
			return targetId?.Execute();
		}

		// FNV-1a 64-bit. Public-domain, fast, deterministic across runs and
		// processes — exactly what dedup needs. We use it directly on
		// ReadOnlySpan<char> and on primitive value bytes to avoid the
		// per-execute string allocations the previous SHA-256 version did.
		private protected const long FNV_OFFSET_BASIS = unchecked((long)0xcbf29ce484222325UL);
		private protected const long FNV_PRIME = unchecked((long)0x100000001b3UL);

		// Deterministic 64-bit identity for an implicit tell — derived from the
		// target class, target id value, command name, and arg values. Returns the
		// same long for the same logical tell across runs, so SymbolTable
		// .IsImplicitTellApplied recognises replays and duplicates without ever
		// constructing a string id on the lookup path.
		private protected static long ComputeImplicitTellHash(string targetClass, object targetIdValue, string commandName, AstExpression[] commandArgs)
		{
			long h = FNV_OFFSET_BASIS;
			h = FoldString(h, targetClass);
			h = FoldSeparator(h);
			h = FoldValue(h, targetIdValue);
			h = FoldSeparator(h);
			h = FoldString(h, commandName);
			for (int i = 0; i < commandArgs.Length; i++)
			{
				h = FoldSeparator(h);
				h = FoldValue(h, commandArgs[i].Execute());
			}
			return h;
		}

		private protected static long FoldSeparator(long h)
		{
			return unchecked((h ^ 0x1FL) * FNV_PRIME);
		}

		private protected static long FoldString(long h, string s)
		{
			if (s == null) return unchecked((h ^ 0xFFL) * FNV_PRIME);
			ReadOnlySpan<char> span = s.AsSpan();
			for (int i = 0; i < span.Length; i++)
			{
				char c = span[i];
				h = unchecked((h ^ (byte)(c & 0xFF)) * FNV_PRIME);
				h = unchecked((h ^ (byte)(c >> 8)) * FNV_PRIME);
			}
			return h;
		}

		private protected static long FoldLong(long h, long v)
		{
			for (int shift = 0; shift < 64; shift += 8)
			{
				h = unchecked((h ^ (byte)((v >> shift) & 0xFF)) * FNV_PRIME);
			}
			return h;
		}

		// Mix any value reachable from the DSL (literals, evaluated expressions)
		// into the running FNV hash. The common cases (string, int, long, double,
		// bool, null) are zero-alloc. Boxed primitives unbox via pattern matching;
		// no ToString fallback runs on those paths. Truly exotic types fall through
		// to ToString() — that path allocates, but it is also vanishingly rare in
		// the kind of tells the framework expects.
		private protected static long FoldValue(long h, object value)
		{
			switch (value)
			{
				case null:
					return unchecked((h ^ 0x00L) * FNV_PRIME);
				case string s:
					return FoldString(h, s);
				case long l:
					return FoldLong(h, l);
				case int i:
					return FoldLong(h, i);
				case short sh:
					return FoldLong(h, sh);
				case byte b:
					return FoldLong(h, b);
				case bool boo:
					return FoldLong(h, boo ? 1L : 0L);
				case double d:
					return FoldLong(h, BitConverter.DoubleToInt64Bits(d));
				case decimal dec:
					int[] parts = decimal.GetBits(dec);
					long combined = ((long)parts[3] << 32) ^ ((long)parts[2] << 16) ^ ((long)parts[1] << 8) ^ parts[0];
					return FoldLong(h, combined);
				case DateTime dt:
					return FoldLong(h, dt.Ticks);
				default:
					return FoldString(h, value.ToString());
			}
		}

		// Format the implicit hash as the envelope.Id when the developer omitted
		// `id 'X'`. This is the single string allocation the implicit path takes,
		// and only when an envelope is actually constructed for outbound delivery
		// — replay never reaches here because Execute short-circuits on
		// RecoveringState before constructing the envelope.
		private protected static string FormatImplicitEnvelopeId(long hash)
		{
			return hash.ToString("x16");
		}

		// Helpers shared by subclasses for rendering trailing `id 'X'` and `through 'Y'`.
		private protected void WriteIdTrailer(StringBuilder resultado)
		{
			if (IdLiteral == null) return;
			resultado.Append("\r\tid '");
			resultado.Append(IdLiteral);
			resultado.Append("'");
		}

		private protected void WriteThroughTrailer(StringBuilder resultado)
		{
			if (ThroughLiteral == null) return;
			resultado.Append("\r\tthrough '");
			resultado.Append(ThroughLiteral);
			resultado.Append("'");
		}
	}

	// Form 1: `tell <Target>[(<targetId>)] <Command>(<args>) [id <id>] [through <s>]`.
	// When TargetId is null, this is a pure broadcast (e.g. `tell Reporting OrderConfirmed(...)`).
	// When TargetId is present, it is an addressed tell to a specific actor instance.
	internal sealed class BasicTellStatement : TellStatement
	{
		internal string TargetClass { get; }
		internal AstExpression TargetId { get; }
		internal string CommandName { get; }
		internal AstExpression[] CommandArgs { get; }

		internal BasicTellStatement(SymbolTable symbolTable, string targetClass, AstExpression targetId, string commandName, AstExpression[] commandArgs, string idLiteral, string throughLiteral)
			: base(symbolTable, idLiteral, throughLiteral)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(targetClass);
			ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
			ArgumentNullException.ThrowIfNull(commandArgs);

			TargetClass = targetClass;
			TargetId = targetId;
			CommandName = commandName;
			CommandArgs = commandArgs;
		}

		// Plan 7 of the Tell primitive roadmap: register this outbound tell as
		// a script-side ScriptTellStatement so the matcher (PatternMatcher) can
		// compare it against TellPatternNode entries in the Reaction's pattern.
		// Argument expressions are evaluated here — same contract as the matcher
		// uses for ScriptMethodCall (arguments captured as their resolved values).
		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			object targetIdValue = EvaluateTargetId(TargetId);
			object[] commandArgsValues = new object[CommandArgs.Length];
			for (int i = 0; i < CommandArgs.Length; i++)
			{
				commandArgsValues[i] = CommandArgs[i].Execute();
			}
			string envelopeId = IdLiteral
				?? FormatImplicitEnvelopeId(ComputeImplicitTellHash(TargetClass, targetIdValue, CommandName, CommandArgs));
			patternAst.RegisterTellStatement(TargetClass, targetIdValue, CommandName, commandArgsValues, envelopeId, position++);
		}

		internal override void Execute(ExecutionOutput output)
		{
			// Replay short-circuit (see SymbolTable.appliedImplicitTellHashes /
			// .appliedExplicitTellIds for rationale): mark the dedup entry so live
			// executes after recovery are no-ops, but do NOT enqueue an envelope —
			// the transport must not see ghost messages from rehydration.
			if (SymbolTable.RecoveringState)
			{
				if (IdLiteral != null)
				{
					SymbolTable.MarkExplicitTellApplied(IdLiteral);
					RegisterTellEntryForElision(IdLiteral);
				}
				else
				{
					object targetIdValueRecovery = EvaluateTargetId(TargetId);
					long hashRecovery = ComputeImplicitTellHash(TargetClass, targetIdValueRecovery, CommandName, CommandArgs);
					SymbolTable.MarkImplicitTellApplied(hashRecovery);
					RegisterTellEntryForElision(FormatImplicitEnvelopeId(hashRecovery));
				}
				return;
			}

			EnsureInReactionAction();
			EnsureTransportConfigured();

			// Explicit branch — developer wrote `id 'X'`. Reuse the string verbatim
			// for both the dedup key and the envelope id; no fabrication.
			if (IdLiteral != null)
			{
				if (SymbolTable.IsExplicitTellApplied(IdLiteral)) return;

				object targetIdValueExplicit = EvaluateTargetId(TargetId);
				string commandTextExplicit = RenderCommandCall(CommandName, CommandArgs);

				TellEnvelope envelopeExplicit = new TellEnvelope(
					Id: IdLiteral,
					TargetClass: TargetClass,
					TargetId: targetIdValueExplicit?.ToString(),
					CommandText: commandTextExplicit,
					Transport: ThroughLiteral,
					CausalEventId: null,
					ReactionName: null,
					Check: SymbolTable.CurrentCausationCheck);

				SymbolTable.MarkExplicitTellApplied(IdLiteral);
				SymbolTable.EnqueuePendingTell(envelopeExplicit);
				RegisterTellEntryForElision(IdLiteral);
				return;
			}

			// Implicit branch — no `id 'X'`. Dedup with FNV-1a long; envelope.Id is
			// the long formatted as 16-char hex (the single string alloc the
			// implicit path takes, and only when an envelope is actually built).
			object targetIdValue = EvaluateTargetId(TargetId);
			long hash = ComputeImplicitTellHash(TargetClass, targetIdValue, CommandName, CommandArgs);

			if (SymbolTable.IsImplicitTellApplied(hash)) return;

			string commandText = RenderCommandCall(CommandName, CommandArgs);
			string implicitEnvelopeId = FormatImplicitEnvelopeId(hash);
			TellEnvelope envelope = new TellEnvelope(
				Id: implicitEnvelopeId,
				TargetClass: TargetClass,
				TargetId: targetIdValue?.ToString(),
				CommandText: commandText,
				Transport: ThroughLiteral,
				CausalEventId: null,
				ReactionName: null);

			SymbolTable.MarkImplicitTellApplied(hash);
			SymbolTable.EnqueuePendingTell(envelope);
			RegisterTellEntryForElision(implicitEnvelopeId);
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));
			resultado.Append("tell ");
			resultado.Append(TargetClass);
			if (TargetId != null)
			{
				resultado.Append("(");
				TargetId.write(resultado, databaseType);
				resultado.Append(")");
			}
			resultado.Append("\r\t");
			resultado.Append(CommandName);
			resultado.Append("(");
			for (int i = 0; i < CommandArgs.Length; i++)
			{
				if (i > 0) resultado.Append(", ");
				CommandArgs[i].write(resultado, databaseType);
			}
			resultado.Append(")");
			WriteIdTrailer(resultado);
			WriteThroughTrailer(resultado);
			// Plan 9 of the Tell primitive roadmap: emit trailing semicolon so
			// the rendered sentence parses cleanly when the journal replays it
			// through the same parser that produced the AST in the first place.
			resultado.Append(";");
		}
	}

	// Form 2: `tell <SagaActor>(<sagaId>) start|step|compensate|close <Command>(<args>) [id <id>] [through <s>]`.
	// The saga verb is part of the program — auditors read the journal and immediately see
	// progression, compensation, or closure. Saga always carries a sagaId in parens.
	internal enum SagaVerb
	{
		Start,
		Step,
		Compensate,
		Close
	}

	internal sealed class TellSagaStatement : TellStatement
	{
		internal string SagaActor { get; }
		internal AstExpression SagaId { get; }
		internal SagaVerb Verb { get; }
		internal string CommandName { get; }
		internal AstExpression[] CommandArgs { get; }

		internal TellSagaStatement(SymbolTable symbolTable, string sagaActor, AstExpression sagaId, SagaVerb verb, string commandName, AstExpression[] commandArgs, string idLiteral, string throughLiteral)
			: base(symbolTable, idLiteral, throughLiteral)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(sagaActor);
			ArgumentNullException.ThrowIfNull(sagaId);
			ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
			ArgumentNullException.ThrowIfNull(commandArgs);

			SagaActor = sagaActor;
			SagaId = sagaId;
			Verb = verb;
			CommandName = commandName;
			CommandArgs = commandArgs;
		}

		internal static string VerbToken(SagaVerb verb)
		{
			return verb switch
			{
				SagaVerb.Start => "start",
				SagaVerb.Step => "step",
				SagaVerb.Compensate => "compensate",
				SagaVerb.Close => "close",
				_ => throw new LanguageException($"Unknown saga verb '{verb}'.")
			};
		}

		internal override void Execute(ExecutionOutput output)
		{
			object sagaIdValue = EvaluateTargetId(SagaId);
			string sagaIdString = sagaIdValue?.ToString();
			long currentEntryId = Program?.EntryId ?? 0;

			if (SymbolTable.RecoveringState)
			{
				// Replay path: rebuild dedup tables AND saga cursor state, but
				// do NOT enqueue envelopes (Plan 5 invariant) and tolerate
				// already-applied transitions (Plan 8 — replay is journal-truth,
				// invariant-violations on the storage are facts, not new errors).
				if (IdLiteral != null)
				{
					SymbolTable.MarkExplicitTellApplied(IdLiteral);
					RegisterTellEntryForElision(IdLiteral);
				}
				else
				{
					long hashRecovery = ComputeImplicitSagaHash(SagaActor, sagaIdValue, Verb, CommandName, CommandArgs);
					SymbolTable.MarkImplicitTellApplied(hashRecovery);
					RegisterTellEntryForElision(FormatImplicitEnvelopeId(hashRecovery));
				}

				if (sagaIdString != null)
				{
					SagaCursor cursorReplay = SymbolTable.GetOrCreateSagaCursor(SagaActor, sagaIdString);
					ApplySagaTransitionAtReplay(cursorReplay, currentEntryId);
					if (Verb == SagaVerb.Close) ReEmitTrajectoryElisionAtReplay(cursorReplay);
				}
				return;
			}

			EnsureInReactionAction();
			EnsureTransportConfigured();

			// Plan 8 live-path: enforce saga state-machine invariants BEFORE
			// running any envelope-emitting work. Illegal transitions (start
			// twice, step on a closed saga, etc.) raise LanguageException so the
			// developer sees the bug at execute time rather than as a silent
			// pile-up of inconsistent saga state.
			SagaCursor cursor = null;
			if (sagaIdString != null)
			{
				cursor = SymbolTable.GetOrCreateSagaCursor(SagaActor, sagaIdString);
				ApplySagaTransitionLive(cursor, currentEntryId);
			}

			string commandCall = RenderCommandCall(CommandName, CommandArgs);
			string commandText = $"{VerbToken(Verb)} {commandCall}";

			// Explicit branch.
			if (IdLiteral != null)
			{
				if (SymbolTable.IsExplicitTellApplied(IdLiteral))
				{
					// Already executed in a previous PerformCmd within this actor's
					// live session. The state machine has already been advanced at
					// the original call; do not advance it twice.
					return;
				}

				TellEnvelope envelopeExplicit = new TellEnvelope(
					Id: IdLiteral,
					TargetClass: SagaActor,
					TargetId: sagaIdString,
					CommandText: commandText,
					Transport: ThroughLiteral,
					CausalEventId: null,
					ReactionName: null,
					Check: SymbolTable.CurrentCausationCheck);

				SymbolTable.MarkExplicitTellApplied(IdLiteral);
				SymbolTable.EnqueuePendingTell(envelopeExplicit);
				RegisterTellEntryForElision(IdLiteral);

				if (Verb == SagaVerb.Close && cursor != null) EmitTrajectoryElisionLive(cursor);
				return;
			}

			// Implicit branch.
			long hash = ComputeImplicitSagaHash(SagaActor, sagaIdValue, Verb, CommandName, CommandArgs);

			if (SymbolTable.IsImplicitTellApplied(hash)) return;

			string implicitEnvelopeId = FormatImplicitEnvelopeId(hash);
			TellEnvelope envelope = new TellEnvelope(
				Id: implicitEnvelopeId,
				TargetClass: SagaActor,
				TargetId: sagaIdString,
				CommandText: commandText,
				Transport: ThroughLiteral,
				CausalEventId: null,
				ReactionName: null);

			SymbolTable.MarkImplicitTellApplied(hash);
			SymbolTable.EnqueuePendingTell(envelope);
			RegisterTellEntryForElision(implicitEnvelopeId);

			if (Verb == SagaVerb.Close && cursor != null) EmitTrajectoryElisionLive(cursor);
		}

		private void ApplySagaTransitionLive(SagaCursor cursor, long entryId)
		{
			switch (Verb)
			{
				case SagaVerb.Start: cursor.TransitionStart(entryId); break;
				case SagaVerb.Step: cursor.TransitionStep(entryId); break;
				case SagaVerb.Compensate: cursor.TransitionCompensate(entryId); break;
				case SagaVerb.Close: cursor.TransitionClose(entryId); break;
				default: throw new LanguageException($"Unknown saga verb '{Verb}'.");
			}
		}

		private void ApplySagaTransitionAtReplay(SagaCursor cursor, long entryId)
		{
			switch (Verb)
			{
				case SagaVerb.Start: cursor.ReplayStart(entryId); break;
				case SagaVerb.Step: cursor.ReplayStep(entryId); break;
				case SagaVerb.Compensate: cursor.ReplayCompensate(entryId); break;
				case SagaVerb.Close: cursor.ReplayClose(entryId); break;
				default: throw new LanguageException($"Unknown saga verb '{Verb}'.");
			}
		}

		private void EmitTrajectoryElisionLive(SagaCursor cursor)
		{
			if (SymbolTable.ActorHandler == null) return;
			SymbolTable.ActorHandler.EmitSagaTrajectoryElision(cursor.TrajectoryEntryIds.ToArray());
		}

		private void ReEmitTrajectoryElisionAtReplay(SagaCursor cursor)
		{
			if (SymbolTable.ActorHandler == null) return;
			SymbolTable.ActorHandler.EmitSagaTrajectoryElision(cursor.TrajectoryEntryIds.ToArray());
		}

		// Saga shares the same FNV-1a folding as basic tell, but mixes in the verb
		// so two structurally identical sentences with different verbs (e.g. step
		// vs compensate of the same payload) hash to distinct longs and therefore
		// dedupe independently — they ARE distinct effects on the saga.
		private static long ComputeImplicitSagaHash(string sagaActor, object sagaIdValue, SagaVerb verb, string commandName, AstExpression[] commandArgs)
		{
			long h = FNV_OFFSET_BASIS;
			h = FoldString(h, sagaActor);
			h = FoldSeparator(h);
			h = FoldValue(h, sagaIdValue);
			h = FoldSeparator(h);
			h = FoldLong(h, (long)verb);
			h = FoldSeparator(h);
			h = FoldString(h, commandName);
			for (int i = 0; i < commandArgs.Length; i++)
			{
				h = FoldSeparator(h);
				h = FoldValue(h, commandArgs[i].Execute());
			}
			return h;
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));
			resultado.Append("tell ");
			resultado.Append(SagaActor);
			resultado.Append("(");
			SagaId.write(resultado, databaseType);
			resultado.Append(") ");
			resultado.Append(VerbToken(Verb));
			resultado.Append(" ");
			resultado.Append(CommandName);
			resultado.Append("(");
			for (int i = 0; i < CommandArgs.Length; i++)
			{
				if (i > 0) resultado.Append(", ");
				CommandArgs[i].write(resultado, databaseType);
			}
			resultado.Append(")");
			WriteIdTrailer(resultado);
			WriteThroughTrailer(resultado);
			// Plan 9: trailing semicolon so journal replay re-parses cleanly.
			resultado.Append(";");
		}
	}

	// Form 3: `tell ack <ackId> from <Target>(<targetId>)`.
	// Recorded in the journal of the actor of origin (A) when the transport delivers an
	// acknowledgement coming from B. B is unaware of this primitive — it just emits a
	// regular event that the transport routes back to A's ack channel.
	//
	// Plan 5 leaves Execute as a no-op: ack ingestion comes from the transport via
	// RegisterAckHandler, not from a developer-authored DSL line. Plan 6 wires the
	// handler so the rendered ack sentence ends up in the journal automatically and
	// MarkAsSkip closes the tell+ack pair.
	internal sealed class TellAckStatement : TellStatement
	{
		internal string AckId { get; }
		internal string FromTargetClass { get; }
		internal AstExpression FromTargetId { get; }

		internal TellAckStatement(SymbolTable symbolTable, string ackId, string fromTargetClass, AstExpression fromTargetId)
			: base(symbolTable, idLiteral: null, throughLiteral: null)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(ackId);
			ArgumentException.ThrowIfNullOrWhiteSpace(fromTargetClass);
			ArgumentNullException.ThrowIfNull(fromTargetId);

			AckId = ackId;
			FromTargetClass = fromTargetClass;
			FromTargetId = fromTargetId;
		}

		// Plan 7 of the Tell primitive roadmap: register this ack as a
		// script-side ScriptTellAckStatement so the matcher can compare it
		// against TellAckPatternNode entries in the Reaction's pattern.
		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			object fromTargetIdValue = FromTargetId?.Execute();
			patternAst.RegisterTellAckStatement(AckId, FromTargetClass, fromTargetIdValue, position++);
		}

		internal override void Execute(ExecutionOutput output)
		{
			// Replay path: rebuild ack dedup state and re-emit pair elision if
			// the live HandleAckEnvelope call was interrupted between writing
			// the ack entry and emitting the elision marker. Idempotent.
			if (SymbolTable.RecoveringState)
			{
				SymbolTable.MarkTellEnvelopeIdAcked(AckId);

				if (Program == null) return;
				long ackEntryId = Program.EntryId;
				if (ackEntryId <= 0) return;

				if (SymbolTable.TryLookupTellEntryId(AckId, out long tellEntryId)
					&& SymbolTable.IsSingleTellEntry(tellEntryId)
					&& SymbolTable.ActorHandler != null)
				{
					SymbolTable.ActorHandler.TryEmitTellPairElision(tellEntryId, ackEntryId);
				}
				return;
			}

			// Live path: `tell ack` is never authored by user code. Acks
			// enter the journal exclusively through the transport's
			// RegisterAckHandler callback in ActorHandler.HandleAckEnvelope.
			// A live execution here means a script wrote `tell ack ...`
			// directly in a PerformCommand or a Reaction's
			// .Causation.Continue(...) body — both are contract violations.
			throw new LanguageException("'tell ack' is journaled by the ack handler of the transport, not by user code. It cannot be issued from a script (PerformCommand, PerformQuery, or a Reaction's .Causation.Continue(...) body). Remove the 'tell ack' statement from the script.");
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));
			resultado.Append("tell ack '");
			resultado.Append(AckId);
			resultado.Append("' from ");
			resultado.Append(FromTargetClass);
			resultado.Append("(");
			FromTargetId.write(resultado, databaseType);
			resultado.Append(")");
			// Plan 9: trailing semicolon so journal replay re-parses cleanly.
			resultado.Append(";");
		}
	}
}
