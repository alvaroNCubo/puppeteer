using System;
using System.Collections.Generic;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// Phase 1 of the Action refactor. Canonicalises the body of a DefineActionStatement
	// into the deterministic string form that the journal stores and replay re-parses.
	//
	// Contract (firmado 2026-05-09 al iniciar Fase 1):
	//   - Render via Statement.Write with DatabaseType.IN_MEMORY — same machinery the
	//     existing Statement renderers already use to emit themselves canonically.
	//   - Same logical body with different incoming whitespace / line comments produces
	//     the same canonical string (whitespace and `//` line comments are absorbed by
	//     parsing — the AST is the truth, not the source text).
	//   - Different statements (or same statements in different order) produce different
	//     canonical strings.
	//   - NO parameter normalization here — parameters are part of the DefineActionStatement
	//     header, not the body. The header's parametersText is canonicalised at parse-time
	//     directly by Parser.ParseDefineActionParameterList.
	//
	// Phase 4 will use this utility on the live path (compute body signature when first
	// invocation triggers auto-emit) and on the re-declaration check (detect cuerpo
	// mismatch between an earlier Define entry and a fresher script body).
	internal static class ActionBodyCanonicalizer
	{
		internal static string Canonicalize(IReadOnlyList<Statement> body)
		{
			ArgumentNullException.ThrowIfNull(body);

			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < body.Count; i++)
			{
				body[i].Write(sb, 0, DatabaseType.IN_MEMORY);
			}
			return sb.ToString();
		}

		internal static string Canonicalize(DefineActionStatement defineAction)
		{
			ArgumentNullException.ThrowIfNull(defineAction);
			return Canonicalize(defineAction.Body);
		}
	}
}
