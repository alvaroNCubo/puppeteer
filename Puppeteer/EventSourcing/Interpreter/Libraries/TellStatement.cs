using Puppeteer.EventSourcing.Follower;
using Puppeteer.Tell;
using System;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// Family of statements that materialise the cross-actor `tell` primitive in the
	// DSL. A `tell` is a directed assertive speech act: the sender commits, in its
	// OWN vocabulary, to the truth of a proposition addressed to a hearer. It does
	// not invoke the receiver and does not name how the message travels.
	//
	// The rendered sentence reaches the journal automatically via Statement.Write
	// (program.ConvertToString → dairy WriteScriptEntry / Define+Invocation); the
	// produced TellEnvelope is enqueued in symbolTable.PendingTells so that
	// ActorHandler.PerformCmd can drain it post-commit, outside the write lock.
	internal abstract class TellStatement : Statement
	{
		private readonly SymbolTable symbolTable;

		private protected TellStatement(SymbolTable symbolTable)
		{
			ArgumentNullException.ThrowIfNull(symbolTable);
			this.symbolTable = symbolTable;
		}

		private protected SymbolTable SymbolTable => symbolTable;

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			// Unreachable by design: a `tell` has no compiled-mode lowering, so a
			// program that contains one is forced to interpreted execution in
			// Program.AdjustCompilationMode. The actor's other programs keep
			// compiling — only the Reaction Causation body that holds the tell (a
			// post-commit, non-hot path) interprets. Reaching here means that
			// invariant was bypassed.
			throw new LanguageException("Internal invariant violation: a 'tell' statement was reached under compiled execution. Programs that contain a tell must run interpreted (see Program.AdjustCompilationMode).");
		}

		internal override void ValidateStatically()
		{
		}

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
			// never delivered to the real receiver.
			if (symbolTable.ActorHandler.IsShadow) return;

			if (symbolTable.ActorHandler.Transport == null)
			{
				throw new LanguageException("Tell statement attempted to execute on an actor without a configured Transport. Set actor.Handler.Transport before issuing 'tell' statements (or remove the statement from the script).");
			}
		}

		// Tell is a Reaction-Action-only statement. Cross-actor causation is a
		// consequence of an intra-actor event observed by a Reaction, not a
		// command/query primitive. Allowing `tell` from PerformCommand / PerformQuery
		// would let any caller dispatch outbound traffic outside the observer
		// pattern — breaking the discipline that makes the journal a faithful catalog
		// of what the actor decided to say. Run inside a Reaction's
		// .Causation.Continue(...) body or remove the statement.
		private protected void EnsureInReactionAction()
		{
			if (!symbolTable.ActorHandler.InReactionAction)
			{
				throw new LanguageException("'tell' is only valid inside the .Causation.Continue(...) Action of a Reaction. It cannot be issued from a top-level PerformCommand, PerformCheckThenCommand, or PerformQuery — cross-actor dispatch must be observed by a Reaction over an intra-actor event. Move the tell into a Reaction's .Causation.Continue(...) body, or remove it from this script.");
			}
		}

		// Records the originating entry id for an envelope id so the ack-side elision
		// can find this tell when the matching ack arrives. Also marks the entry as
		// single-tell-eligible when the program contains exactly one TellStatement —
		// the framework only emits MarkAsSkip on the pair when both entries are
		// single-statement (entry-coarse elision API).
		private protected void RegisterTellEntryForElision(string envelopeId)
		{
			if (Program == null) return;
			long entryId = Program.EntryId;
			if (entryId <= 0) return;
			SymbolTable.RegisterTellEnvelopeEntry(envelopeId, entryId);
			if (Program.HasSingleTellStatement)
			{
				SymbolTable.MarkSingleTellEntry(entryId);
			}
		}

		// Capture the minimal facts a pending tell needs for post-rehydration fate
		// recovery. Called ONLY on the replay branch (RecoveringState) — the live
		// path keeps the full envelope in PendingTells, so it needs no recovery
		// record. The verdict the recovery pass later journals is LOGICAL (names the
		// addressee, never the transport), so only the addressee facts are kept.
		private protected void RegisterTellRecoveryInfo(string envelopeId, string addressee, object addresseeInstanceValue)
		{
			SymbolTable.RegisterTellRecoveryInfo(
				envelopeId,
				new TellRecoveryInfo(addressee, addresseeInstanceValue?.ToString()));
		}

		// Collect the ordered typed VALUES of the `with` payload into a Parameters
		// object — the data that travels to the receiver. Reactions never compute: a
		// captured `@token` arg is a parameter reference whose value was matched from
		// the journal and is already present (and already serialized) in the program's
		// live Parameters, so it is read BY NAME. A literal arg evaluates directly and
		// is typed from its runtime value. Values are serialized in order via
		// ArgumentsAsString; the parameter names/types never travel (the receiver
		// applies them positionally to the command it already holds). Returns null
		// when the message carries no payload.
		private protected Parameters CollectWithValues(AstExpression[] withArgs)
		{
			if (withArgs == null || withArgs.Length == 0) return null;

			Parameters source = Program?.Parameters;
			Parameters collected = null;
			for (int i = 0; i < withArgs.Length; i++)
			{
				AstExpression arg = withArgs[i];
				if (arg is Id id && source != null && source.ContainsParameter(id.Name))
				{
					Parameter p = source[id.Name];
					collected ??= new Parameters();
					collected[p.Name, p.ParameterType] = p.GetValue();
					continue;
				}

				object value = arg.Execute();
				collected ??= new Parameters();
				string name = "arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
				collected[name, value?.GetType() ?? typeof(object)] = value;
			}
			return collected;
		}

		// Resolve a `with` argument to its runtime VALUE without requiring the arg to
		// be bound as a symbol. A captured `@token` is read BY NAME from the live
		// Parameters (the same source CollectWithValues uses); a literal or constant
		// evaluates directly. This is what lets the content-hash fold the captured
		// values: Reactions never compute, so the captures are not bound for
		// Id.Execute() in the reaction-action scope, but their values are present in
		// Program.Parameters.
		private protected object ResolveArgValue(AstExpression arg)
		{
			if (arg is Id id)
			{
				Parameters source = Program?.Parameters;
				if (source != null && source.ContainsParameter(id.Name))
				{
					return source[id.Name].GetValue();
				}
			}
			return arg.Execute();
		}

		// Evaluate an expression (e.g. the addressee instance id) to a runtime value.
		// The downstream code branches on the returned object directly so no defensive
		// ToString() is performed here.
		private protected static object EvaluateExpr(AstExpression expr)
		{
			return expr?.Execute();
		}

		// FNV-1a 64-bit. Public-domain, fast, deterministic across runs and
		// processes — exactly what content-hash identity needs. Used directly on
		// ReadOnlySpan<char> and on primitive value bytes to avoid per-execute string
		// allocations.
		private protected const long FNV_OFFSET_BASIS = unchecked((long)0xcbf29ce484222325UL);
		private protected const long FNV_PRIME = unchecked((long)0x100000001b3UL);

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

		// Mix any value reachable from the DSL (literals, evaluated expressions) into
		// the running FNV hash. The common cases (string, int, long, double, bool,
		// null) are zero-alloc.
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

		// Format a content hash as the envelope.Id when the developer omitted
		// `once 'X'`. This is the single string allocation the content-hash path
		// takes, and only when an envelope is actually constructed for outbound
		// delivery — replay never reaches here because Execute short-circuits on
		// RecoveringState before constructing the envelope.
		private protected static string FormatContentHash(long hash)
		{
			return hash.ToString("x16");
		}
	}

	// The assertive `tell`:
	//   tell <Message> [with <v1, v2, ...>] to <Addressee>[('<instanceId>')] [once <idExpr>];
	//
	// <Message> is a fact the sender lived, named in ITS OWN vocabulary (never the
	// receiver's verb). The `with` values are the payload and deduce the typed
	// signature of the message-action (reusing V2 Action journaling). `to <Addressee>`
	// is the hearer (a logical role); the optional ('<instanceId>') is a specific
	// instance the sender can name. Per-utterance identity defaults to the content
	// hash of (message, addressee, instance, ordered values); the optional `once`
	// EXPRESSION pins it to an author-chosen key for an idempotent utterance.
	//
	// The `once` clause is PARAMETRIC, mirroring the matcher's `once <param>`: the
	// body is compiled once at DefineReaction but the expression is evaluated PER
	// event, so a single Action firing on purchases A1, A2, A3 issues three tells
	// with identities A1, A2, A3 — never one Action per purchase. A bare `@parameter`
	// (once @order) captures the meaningful per-event value; a string literal
	// (once 'welcome-42') pins a constant idempotent key exactly as before; and a
	// string-valued expression (once 'reward-' + @order) composes the two.
	internal sealed class AssertiveTellStatement : TellStatement
	{
		internal string MessageName { get; }
		internal AstExpression[] WithArgs { get; }
		internal string Addressee { get; }
		internal AstExpression AddresseeInstanceId { get; }
		// Optional `once <expr>`. When present, the per-utterance identity IS the value
		// the expression evaluates to (content does NOT enter the identity), so N
		// utterances under the same resolved key collapse to one commitment. A literal
		// is just a constant expression (the pre-expression `once '<literal>'` shape).
		// Null → content-hash identity.
		internal AstExpression OnceExpression { get; }

		internal AssertiveTellStatement(SymbolTable symbolTable, string messageName, AstExpression[] withArgs, string addressee, AstExpression addresseeInstanceId, AstExpression onceExpression)
			: base(symbolTable)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(messageName);
			ArgumentNullException.ThrowIfNull(withArgs);
			ArgumentException.ThrowIfNullOrWhiteSpace(addressee);

			MessageName = messageName;
			WithArgs = withArgs;
			Addressee = addressee;
			AddresseeInstanceId = addresseeInstanceId;
			OnceExpression = onceExpression;
		}

		// The `once` expression's identifiers must be reachable by reference
		// resolution so a captured `@parameter` inside it binds to the per-event value
		// (the same treatment the reaction action gives its parameters). Only the
		// `once` subtree is exposed — the `with` args and addressee instance keep their
		// established behavior of being read BY NAME from Program.Parameters, never
		// bound for Id.Execute() (see ResolveArgValue). Forwarding just this new node
		// lets `once 'reward-' + @order` evaluate while leaving all existing paths
		// untouched. The base Visit performs the self type-match against the visitor.
		internal override void Visit(ASTVisitor v)
		{
			base.Visit(v);
			OnceExpression?.Visit(v);
		}

		// Resolve the `once` expression to the per-utterance identity string. Mirrors
		// the `with` capture idiom (ResolveArgValue): a bare captured `@parameter` is
		// read by name from the live Parameters, and any other expression (a literal or
		// a composed string) is evaluated directly. The result is rendered with the
		// invariant culture so a numeric/date-typed identity is byte-stable across
		// runs, replays, and data centers — the same determinism the content hash gives.
		private string ResolveOnceIdentity()
		{
			object value = ResolveArgValue(OnceExpression);
			if (value == null)
			{
				throw new LanguageException("'tell ... once <expr>' evaluated to null. The once identity must resolve to a non-empty value — a captured @parameter (e.g. once @order), a literal (once 'welcome-42'), or a string-valued expression (e.g. once 'reward-' + @order).");
			}
			string id = value as string
				?? (value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value.ToString());
			if (string.IsNullOrEmpty(id))
			{
				throw new LanguageException("'tell ... once <expr>' evaluated to an empty identity. The once identity must be a non-empty value.");
			}
			return id;
		}

		// Register this outbound tell as a script-side ScriptTellStatement so the
		// matcher (PatternMatcher) can compare it against TellPatternNode entries in
		// the Reaction's pattern. Argument expressions are resolved to their values
		// here — same contract the matcher uses for ScriptMethodCall.
		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			object instanceValue = EvaluateExpr(AddresseeInstanceId);
			object[] withValues = new object[WithArgs.Length];
			for (int i = 0; i < WithArgs.Length; i++)
			{
				withValues[i] = ResolveArgValue(WithArgs[i]);
			}
			string envelopeId = OnceExpression != null
				? ResolveOnceIdentity()
				: FormatContentHash(ComputeContentHash(MessageName, Addressee, instanceValue, WithArgs));
			patternAst.RegisterTellStatement(MessageName, Addressee, instanceValue, withValues, envelopeId, position++);
		}

		// Build the outbound envelope for this utterance. Used by both the live branch
		// (enqueued to PendingTells for post-commit delivery) and the replay branch
		// (retained for a possible red-black takeover re-delivery, never enqueued). The
		// `with` VALUES resolve from Program.Parameters, which replay has already loaded
		// with the journaled invocation arguments — so a replay-built envelope carries
		// the same payload the live one did.
		private TellEnvelope BuildEnvelope(string envelopeId, object instanceValue)
		{
			return new TellEnvelope(
				Id: envelopeId,
				MessageName: MessageName,
				Addressee: Addressee,
				AddresseeInstanceId: instanceValue?.ToString(),
				// Explicit causal back-reference: the entry that triggered the Reaction
				// whose body emitted this tell, and that Reaction's name. Set by
				// ExecuteCausation around this PerformCmd; null when the tell is not
				// emitted from a Causation body. This is the durable causal identity of
				// the utterance — the anchor of tell-native observability.
				CausalEventId: SymbolTable.CurrentCausationCausalEventId,
				ReactionName: SymbolTable.CurrentCausationReactionName,
				Check: SymbolTable.CurrentCausationCheck,
				Values: CollectWithValues(WithArgs));
		}

		internal override void Execute(ExecutionOutput output)
		{
			// Replay short-circuit: mark the dedup entry so live executes after recovery
			// are no-ops, and retain the full envelope for a possible red-black takeover
			// re-delivery — but do NOT enqueue it to PendingTells: the transport must not
			// see ghost messages from rehydration. The retained envelope is delivered only
			// if a takeover (UnlockAndRunAlive, primary) finds the tell still pending.
			if (SymbolTable.RecoveringState)
			{
				object instanceValueReplay = EvaluateExpr(AddresseeInstanceId);
				if (OnceExpression != null)
				{
					string onceIdReplay = ResolveOnceIdentity();
					SymbolTable.MarkExplicitTellApplied(onceIdReplay);
					RegisterTellEntryForElision(onceIdReplay);
					RegisterTellRecoveryInfo(onceIdReplay, Addressee, instanceValueReplay);
					SymbolTable.RegisterReissueEnvelope(onceIdReplay, BuildEnvelope(onceIdReplay, instanceValueReplay));
				}
				else
				{
					long hashReplay = ComputeContentHash(MessageName, Addressee, instanceValueReplay, WithArgs);
					string envelopeIdReplay = FormatContentHash(hashReplay);
					SymbolTable.MarkImplicitTellApplied(hashReplay);
					RegisterTellEntryForElision(envelopeIdReplay);
					RegisterTellRecoveryInfo(envelopeIdReplay, Addressee, instanceValueReplay);
					SymbolTable.RegisterReissueEnvelope(envelopeIdReplay, BuildEnvelope(envelopeIdReplay, instanceValueReplay));
				}
				return;
			}

			EnsureInReactionAction();
			EnsureTransportConfigured();

			object instanceValue = EvaluateExpr(AddresseeInstanceId);

			// Explicit branch — developer wrote `once <expr>`. Identity IS the value the
			// expression evaluates to for THIS event (a captured @parameter varies per
			// event; a literal is constant).
			if (OnceExpression != null)
			{
				string onceId = ResolveOnceIdentity();
				if (SymbolTable.IsExplicitTellApplied(onceId)) return;

				TellEnvelope envelopeOnce = BuildEnvelope(onceId, instanceValue);

				SymbolTable.MarkExplicitTellApplied(onceId);
				SymbolTable.EnqueuePendingTell(envelopeOnce);
				RegisterTellEntryForElision(onceId);
				return;
			}

			// Default branch — content-hash identity over the values.
			long hash = ComputeContentHash(MessageName, Addressee, instanceValue, WithArgs);
			if (SymbolTable.IsImplicitTellApplied(hash)) return;

			string envelopeId = FormatContentHash(hash);
			TellEnvelope envelope = BuildEnvelope(envelopeId, instanceValue);

			SymbolTable.MarkImplicitTellApplied(hash);
			SymbolTable.EnqueuePendingTell(envelope);
			RegisterTellEntryForElision(envelopeId);
		}

		// Deterministic 64-bit identity for a content-hashed tell — derived from the
		// message name, addressee, addressee instance, and the ordered `with` values.
		// Returns the same long for the same logical utterance across runs, so distinct
		// events hash distinctly and a true re-utterance hashes identically.
		private long ComputeContentHash(string messageName, string addressee, object instanceValue, AstExpression[] withArgs)
		{
			long h = FNV_OFFSET_BASIS;
			h = FoldString(h, messageName);
			h = FoldSeparator(h);
			h = FoldString(h, addressee);
			h = FoldSeparator(h);
			h = FoldValue(h, instanceValue);
			for (int i = 0; i < withArgs.Length; i++)
			{
				h = FoldSeparator(h);
				h = FoldValue(h, ResolveArgValue(withArgs[i]));
			}
			return h;
		}

		internal override void Write(StringBuilder result, int tabs, DatabaseType databaseType)
		{
			if (WasFiltered) return;
			if (tabs > 0) result.Append(GenerateTabs(tabs));
			result.Append("tell ");
			result.Append(MessageName);
			if (WithArgs.Length > 0)
			{
				result.Append(" with ");
				for (int i = 0; i < WithArgs.Length; i++)
				{
					if (i > 0) result.Append(", ");
					WithArgs[i].write(result, databaseType);
				}
			}
			result.Append(" to ");
			result.Append(Addressee);
			if (AddresseeInstanceId != null)
			{
				result.Append("(");
				AddresseeInstanceId.write(result, databaseType);
				result.Append(")");
			}
			if (OnceExpression != null)
			{
				result.Append(" once ");
				OnceExpression.write(result, databaseType);
			}
			result.Append(";");
		}
	}

	// Framework-emitted journal sentence (never user-authored):
	//   tell ack '<id>' from <Addressee>[('<instanceId>')];
	// Recorded in A's journal when the transport delivers an acknowledgement from the
	// addressee. `<id>` is the utterance's content identity; `from <Addressee>` names
	// the logical hearer that acknowledged — both things A can truthfully say.
	internal sealed class TellAckStatement : TellStatement
	{
		internal string AckId { get; }
		internal string FromAddressee { get; }
		internal AstExpression FromAddresseeInstanceId { get; }

		internal TellAckStatement(SymbolTable symbolTable, string ackId, string fromAddressee, AstExpression fromAddresseeInstanceId)
			: base(symbolTable)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(ackId);
			ArgumentException.ThrowIfNullOrWhiteSpace(fromAddressee);

			AckId = ackId;
			FromAddressee = fromAddressee;
			FromAddresseeInstanceId = fromAddresseeInstanceId;
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			object fromInstanceValue = EvaluateExpr(FromAddresseeInstanceId);
			patternAst.RegisterTellAckStatement(AckId, FromAddressee, fromInstanceValue, position++);
		}

		internal override void Execute(ExecutionOutput output)
		{
			// Replay path: rebuild ack dedup state and re-emit pair elision if the live
			// HandleAckEnvelope call was interrupted between writing the ack entry and
			// emitting the elision marker. Idempotent.
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

			// Live path: `tell ack` is never authored by user code. Acks enter the
			// journal exclusively through the transport's RegisterAckHandler callback
			// in ActorHandler.HandleAckEnvelope.
			throw new LanguageException("'tell ack' is journaled by the ack handler of the transport, not by user code. It cannot be issued from a script (PerformCommand, PerformQuery, or a Reaction's .Causation.Continue(...) body). Remove the 'tell ack' statement from the script.");
		}

		internal override void Write(StringBuilder result, int tabs, DatabaseType databaseType)
		{
			if (WasFiltered) return;
			if (tabs > 0) result.Append(GenerateTabs(tabs));
			result.Append("tell ack '");
			result.Append(AckId);
			result.Append("' from ");
			result.Append(FromAddressee);
			if (FromAddresseeInstanceId != null)
			{
				result.Append("(");
				FromAddresseeInstanceId.write(result, databaseType);
				result.Append(")");
			}
			result.Append(";");
		}
	}

	// Framework-emitted journal sentence (never user-authored):
	//   tell '<id>' unacknowledged by <Addressee>;
	// The terminal NON-acknowledgement verdict, the failure-side counterpart of
	// `tell ack`: together they make the journal self-sufficient about the FATE of
	// every issued tell. It is LOGICAL — A asserts the absence of an acknowledgement
	// from the addressee, a thing A can truthfully say. Which transport testified
	// (and why: dead-letter vs exhausted retries) is provenance for telemetry, never
	// the journal. Recorded by the transport's failure handler / the recovery pass.
	internal sealed class TellUnacknowledgedStatement : TellStatement
	{
		internal string EnvelopeIdLiteral { get; }
		internal string Addressee { get; }

		internal TellUnacknowledgedStatement(SymbolTable symbolTable, string envelopeId, string addressee)
			: base(symbolTable)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
			ArgumentException.ThrowIfNullOrWhiteSpace(addressee);

			EnvelopeIdLiteral = envelopeId;
			Addressee = addressee;
		}

		internal override void Execute(ExecutionOutput output)
		{
			// Replay path: rebuild the TERMINAL not-acknowledged dedup state so a later
			// recovery pass does not re-query the transport for an envelope whose fate
			// the journal already records. Idempotent.
			if (SymbolTable.RecoveringState)
			{
				SymbolTable.MarkTellEnvelopeIdNotDelivered(EnvelopeIdLiteral);
				return;
			}

			// Live path: never authored by user code — non-acknowledgement verdicts
			// enter the journal exclusively through the transport's failure handler
			// (ActorHandler.HandleTellFailure) or the post-rehydration recovery pass.
			throw new LanguageException("'tell ... unacknowledged by' is journaled by the transport's failure handler / recovery pass, not by user code. It cannot be issued from a script (PerformCommand, PerformQuery, or a Reaction's .Causation.Continue(...) body). Remove the statement from the script.");
		}

		internal override void Write(StringBuilder result, int tabs, DatabaseType databaseType)
		{
			if (WasFiltered) return;
			if (tabs > 0) result.Append(GenerateTabs(tabs));
			result.Append("tell '");
			result.Append(EnvelopeIdLiteral);
			result.Append("' unacknowledged by ");
			result.Append(Addressee);
			result.Append(";");
		}
	}
}
