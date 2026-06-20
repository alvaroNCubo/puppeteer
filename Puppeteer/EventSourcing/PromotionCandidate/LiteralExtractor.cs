using Puppeteer.EventSourcing.Interpreter;
using System;
using System.Text;

namespace Puppeteer.EventSourcing.PromotionCandidate
{
	// B.3.2: result of extracting literals from a canonical V1 Script. The
	// three text artifacts are the inputs the V2 Action machinery already
	// understands:
	//
	//   ParametersDeclaration: e.g. "In,p0:int,In,p1:string,In,p2:int"
	//       — parseable by `new Parameters(parametersDeclaration)`.
	//   ActionBodyText: the canonical script with each literal token
	//       replaced by an @pN reference. Re-parseable as the body of a
	//       `define action <id> (<params>) as <body> end;` row.
	//   ArgumentsString: the comma-separated literal values in source
	//       order — exactly the format Parameters.LoadArguments expects.
	//       For the first invocation right after the promotion Define.
	//
	// LiteralCount is the number of generated parameters (= literals
	// substituted). A LiteralCount of 0 means the script has no literals
	// to extract; the promotion machinery (B.3.3+) may decide whether to
	// promote anyway or skip.
	internal sealed class LiteralExtractionResult
	{
		internal readonly string ParametersDeclaration;
		internal readonly string ActionBodyText;
		internal readonly string ArgumentsString;
		internal readonly int LiteralCount;

		internal LiteralExtractionResult(string parametersDeclaration, string actionBodyText, string argumentsString, int literalCount)
		{
			ArgumentNullException.ThrowIfNull(parametersDeclaration);
			ArgumentNullException.ThrowIfNull(actionBodyText);
			ArgumentNullException.ThrowIfNull(argumentsString);
			if (literalCount < 0) throw new LanguageException($"LiteralCount must be >= 0, got {literalCount}.");

			ParametersDeclaration = parametersDeclaration;
			ActionBodyText = actionBodyText;
			ArgumentsString = argumentsString;
			LiteralCount = literalCount;
		}
	}

	// B.3.2: walks the canonical text of a V1 Script using the existing
	// Lexer, identifies literal tokens (number, decimal, double, currency,
	// stringLit, boolTrue, boolFalse, date, time) in source order, and
	// produces the three text artifacts needed to materialize the
	// equivalent V2 Action. No AST traversal is required because the Lexer
	// already reports each literal's position and TokenType; the literal's
	// raw lexeme (source text between Start and End inclusive) doubles as
	// the argument value because the canonical form of every literal type
	// in Parameters.ArgumentsAsString matches the canonical form produced
	// by Program.ConvertToString — both share the language's surface
	// syntax for literals.
	internal static class LiteralExtractor
	{
		internal static LiteralExtractionResult Extract(string canonicalScript)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(canonicalScript);

			StringBuilder paramsDecl = new StringBuilder();
			StringBuilder args = new StringBuilder();
			StringBuilder body = new StringBuilder(canonicalScript.Length);

			Lexer lexer = new Lexer();
			lexer.Source = canonicalScript;

			int cursor = 0;            // next index in canonicalScript still to copy into `body`.
			int literalCount = 0;

			while (lexer.CurrentToken.Type != TokenType.eof)
			{
				Token tok = lexer.CurrentToken;
				string typeName = LiteralTypeForToken(tok.Type);

				if (typeName != null && tok.Start >= cursor)
				{
					// Copy any text preceding this literal verbatim — preserves
					// whitespace, comments, operators, identifiers, etc.
					if (tok.Start > cursor)
					{
						body.Append(canonicalScript, cursor, tok.Start - cursor);
					}

					// Capture the raw lexeme (including surrounding quotes for
					// stringLit) as the argument value, and emit @pN in its place.
					int length = tok.End - tok.Start + 1;
					string lexeme = canonicalScript.Substring(tok.Start, length);

					string paramName = "p" + literalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
					// Body uses the bare parameter name (no '@' prefix). The DSL
					// accepts '@name' as a stylistic alias for 'name' in the
					// source surface, but the Lexer preserves the '@' inside
					// the Id's lexeme — and Parameters.ContainsParameter is a
					// direct name match. Emitting '@p0' here would produce an
					// Id named "@p0" that does not match the declared Parameter
					// "p0", breaking SolveReferences's parameter linkage at
					// the first routed invocation. Using bare names sidesteps
					// the whole question.
					body.Append(paramName);

					if (paramsDecl.Length > 0) paramsDecl.Append(',');
					paramsDecl.Append("In,").Append(paramName).Append(':').Append(typeName);

					if (args.Length > 0) args.Append(',');
					args.Append(lexeme);

					cursor = tok.End + 1;
					literalCount++;
				}

				lexer.Accept();
			}

			// Tail of the canonical text (everything after the last literal).
			if (cursor < canonicalScript.Length)
			{
				body.Append(canonicalScript, cursor, canonicalScript.Length - cursor);
			}

			return new LiteralExtractionResult(paramsDecl.ToString(), body.ToString(), args.ToString(), literalCount);
		}

		// Mapping from literal-bearing TokenType to the type string accepted by
		// Parameters' declaration grammar (see Parameters.WriteSingleParameterType
		// for the canonical case of each name — note that decimal is "Decimal").
		private static string LiteralTypeForToken(TokenType t)
		{
			switch (t)
			{
				case TokenType.number:    return "int";
				case TokenType.@decimal:  return "Decimal";
				case TokenType.@double:   return "double";
				case TokenType.currency:  return "Decimal";
				case TokenType.stringLit: return "string";
				case TokenType.boolTrue:  return "bool";
				case TokenType.boolFalse: return "bool";
				case TokenType.date:      return "DateTime";
				case TokenType.time:      return "DateTime";
				default:                  return null;
			}
		}
	}
}
