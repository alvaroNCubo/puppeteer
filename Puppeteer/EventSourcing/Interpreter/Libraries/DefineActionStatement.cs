using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// Phase 1 of the Action refactor (project_puppeteer_action_refactor_plan.md).
	// DefineActionStatement materialises the Statement form of an action definition
	// directly inside the journal:
	//
	//     define action <id> (p1:type, p2:type) as
	//         <body statements>
	//     end;
	//
	// The DSL accepts both `name:type` and `@name:type` at parse time (the Lexer drops
	// '@' as a legibility-only alias); the canonical text on
	// the journal never contains '@'. Decision (A) signed at the close of Phase 1 (2026-05-09).
	//
	// The shape lives in the journal as a parseable sentence so replay can rebuild
	// the actor's vocabulary by running statements in order — no lateral _ACTION
	// table, no ActionStore, no in-memory dict needed (Phases 4+5 dismantle those).
	//
	// The developer never authors this Statement: it is auto-emitted by the runtime
	// on first invocation of a parametric script (Phase 4). Phase 1 is parser-only —
	// Execute and ExecuteExpression both throw to make accidental runtime invocation
	// impossible to miss; the Statement exists so the journal sentence round-trips
	// through the same parser that will later emit it.
	internal sealed class DefineActionStatement : Statement
	{
		private readonly int actionId;
		private readonly string parametersText;
		private readonly Statement[] body;

		internal DefineActionStatement(int actionId, string parametersText, Statement[] body)
		{
			if (actionId < 0)
			{
				throw new LanguageException($"DefineActionStatement actionId '{actionId}' must be greater than or equal to zero.");
			}
			ArgumentNullException.ThrowIfNull(parametersText);
			ArgumentNullException.ThrowIfNull(body);

			this.actionId = actionId;
			this.parametersText = parametersText;
			this.body = body;
		}

		internal int ActionId => actionId;

		// Canonical parameter declaration text exactly as it should appear inside the
		// parens of `define action <id> (...)`. Format: `name:type` separated by `, `
		// (no '@' prefix — see class-level docs for option (A) sign-off rationale).
		// Phase 1 signed: NO normalization of parameter order — the order is
		// semantically significant because callsite arguments are positionally bound.
		// Two declarations with the same set in different orders are different actions
		// by design.
		internal string ParametersText => parametersText;

		internal IReadOnlyList<Statement> Body => body;

		internal override void Execute(ExecutionOutput output)
		{
			throw new LanguageException("DefineActionStatement.Execute is parser-only in Phase 1 of the Action refactor (see project_puppeteer_action_refactor_plan.md). Runtime wiring of auto-emit + cache population lands in Phase 4. Reaching this throw means a code path is invoking the Statement before the cutover — file an issue.");
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			throw new LanguageException("DefineActionStatement.ExecuteExpression is parser-only in Phase 1 of the Action refactor. Compiled-mode wiring lands in Phase 4 alongside the live auto-emit path.");
		}

		internal override void ValidateStatically()
		{
			foreach (Statement source in body)
			{
				source.ValidateStatically();
			}
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			foreach (Statement source in body)
			{
				source.PreparePatternMatching(patternAst, ref position);
			}
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			foreach (Statement source in body)
			{
				source.Visit(v);
			}
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));

			// Phase 4 of the Action refactor: render the body at tabs=0 (canonical) and
			// compose the full sentence via ComposeJournalText so the on-the-wire form
			// matches exactly what ActorHandler emits to the journal at runtime — and
			// what the parser reads back.
			StringBuilder bodySb = new StringBuilder();
			foreach (Statement source in body)
			{
				source.Write(bodySb, 0, databaseType);
			}
			resultado.Append(ComposeJournalText(actionId, parametersText, bodySb.ToString()));
		}

		// Phase 4 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// builds the canonical journal sentence
		//     define action <id> (<parametersText>) as
		//     <bodyText>end;
		// from its three components. Used by ActorHandler at runtime to compute the
		// `script` payload of a Define journal row, and by Statement.Write so render
		// and runtime emission stay in lockstep. The bodyText is the rendered
		// statements (typically `program.ConvertToString(IN_MEMORY)` of the body).
		internal static string ComposeJournalText(int actionId, string parametersText, string bodyText)
		{
			if (actionId < 0)
			{
				throw new LanguageException($"DefineActionStatement.ComposeJournalText: actionId '{actionId}' must be greater than or equal to zero.");
			}
			ArgumentNullException.ThrowIfNull(parametersText);
			ArgumentNullException.ThrowIfNull(bodyText);

			StringBuilder sb = new StringBuilder();
			sb.Append("define action ");
			sb.Append(actionId);
			sb.Append(" (");
			sb.Append(parametersText);
			sb.Append(") as\r");
			sb.Append(bodyText);
			sb.Append("end;");
			return sb.ToString();
		}
	}
}
