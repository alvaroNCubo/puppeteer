using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter
{
	internal class Parser
	{
        private Statement lastValidStatement;

        private readonly SymbolTable symbolTable;
        private readonly Lexer lexer;

        private readonly DomainLibraries libraries;

        private static readonly Dictionary<string, Type> primitiveTypes = new Dictionary<string, Type>();
        private static NumberFormatInfo customFormat;
        private string source;

        // Upgrade names seen in the current Program during parsing.
        // Reset at the start of ParseProgram. Detects static duplicates in the same script.
        private readonly HashSet<string> upgradeNamesInProgram = new HashSet<string>(StringComparer.Ordinal);

        // Set to true if the parse of the current Program creates an OpEval or EvalStatement.
        // Reset at the start of ParseProgram; carried into Program.HasEval so that
        // ValidateStatically does not have to walk the AST looking for evals.
        private bool hasEvalInProgram;

        // True while re-parsing a journaled script during rehydration (Parser.Rehydrate).
        // Set at the start of ParseProgram. Journal replay OVERLOADS isCheck to mean "don't
        // need output" and re-parses ordinary COMMANDS, so the read-only-context guards
        // (which reject the journal-writing constructs 'expose' and 'upgrade' in a check)
        // must NOT fire on replay — a replayed command may legitimately contain them.
        private bool parsingRehydration;

		static Parser()
		{
			primitiveTypes["int"] = typeof(int);
			primitiveTypes["string"] = typeof(string);
			primitiveTypes["bool"] = typeof(bool);
			primitiveTypes["double"] = typeof(double);
			primitiveTypes["datetime"] = typeof(DateTime);
			primitiveTypes["decimal"] = typeof(decimal);
			CultureInfo original = CultureInfo.GetCultureInfo("en-US");
			customFormat = (NumberFormatInfo)original.NumberFormat.Clone();
			customFormat.NumberDecimalSeparator = ".";
		}

        internal Parser(DomainLibraries libraries, SymbolTable symbolTable)
		{
			this.libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
			this.symbolTable = symbolTable ?? throw new ArgumentNullException(nameof(symbolTable));
            lexer = new Lexer();
			this.source = "";
        }

		internal Program Parse(bool isQuery, bool isCheck)
		{
			Program result = ParseProgram(Array.Empty<int>(), isQuery, isCheck, isRehydration: false);
			if (isCheck)
			{
				// A check script that cannot reject the command is an always-pass
				// footgun: the framework would treat it as "passed" and run the command
				// unconditionally. A check rejects only via a Check(...) whose reason is a
				// blocking severity (Error/Warning) — an Information/Message (including
				// `Notify Information`) is advisory and never conditions the command. So a
				// check with no Check(...) at all, or only Information reasons, is rejected
				// here at parse time. This guards every check-parse entry point (PerformChk
				// and phase 1 of PerformCheckThenCmd, including the reaction `when:` guard).
				// It lives in Parse (not ParseProgram) on purpose: Rehydrate() overloads the
				// isCheck flag to mean "don't need output" and re-parses ordinary COMMANDS
				// via ParseProgram directly — those legitimately have no Check() and must
				// not be rejected on journal replay.
				RequireAtLeastOneRejectingCheck(result);
			}
			return result;
		}

		private static void RequireAtLeastOneRejectingCheck(Program program)
		{
			foreach (CheckStatement check in program.Collect<CheckStatement>())
			{
				if (check.CanReject)
				{
					return; // at least one Check(...) can condition the command
				}
			}
			throw new LanguageException("A check script must contain at least one Check(...) whose reason is an Error or Warning; an Information message does not condition the command.");
		}

		internal Program Rehydrate()
		{
			bool rehydrateDontNeedOutput = true;
			bool rehydrateAlwaysIsCommand = false;
			Program result = ParseProgram(Array.Empty<int>(), isQuery: rehydrateAlwaysIsCommand, isCheck: rehydrateDontNeedOutput, isRehydration: true);
			return result;
		}

		internal Program ParseEval(int[] currLevel, bool isQuery, bool isCheck)
		{
			bool previousIsEval = symbolTable.InEvalMode;
			symbolTable.InEvalMode = true;
			// An Eval body re-parsed at runtime does not carry the rehydration mark; when it
			// runs during replay the RecoveringState signal covers it (see IsReadOnlyUserContext).
			Program result = ParseProgram(currLevel, isQuery, isCheck, isRehydration: false);
			symbolTable.InEvalMode = previousIsEval;
			return result;
		}

		internal void SetSource(string source)
		{
			this.lexer.Source = source;
			this.source = source;
		}

		private Program ParseProgram(int[] currLevel, bool isQuery, bool isCheck, bool isRehydration)
		{
            upgradeNamesInProgram.Clear();
            hasEvalInProgram = false;
            parsingRehydration = isRehydration;
            List<Statement> statements = new List<Statement>();
            while (lexer.CurrentToken.Type != TokenType.eof)
			{
                switch(lexer.CurrentToken.Type)
                { 
                    case TokenType.eol:
					    ParseWhitespace();
					    break;
                    default:
					    int blockNumber = 0;
					    while (lexer.CurrentToken.Type != TokenType.eof && lexer.CurrentToken.Type != TokenType.eol)
					    {
						    statements.Add(ParseStatement(currLevel, isQuery, isCheck, ref blockNumber));
					    }
					    if (lexer.CurrentToken.Type == TokenType.eol)
					    {
						    lexer.Accept(TokenType.eol);
					    }
                        break;
                }
            }
			lexer.Accept(TokenType.eof);
			Program resultingProgram = new Program(libraries, this.source, symbolTable, statements, currLevel, isQuery, isCheck, isRehydration);
			resultingProgram.HasEval = hasEvalInProgram;
			// Lever 1 of the Now optimization: precompute (once per parse, outside the
			// hot path) whether the program references the SYSTEM Now parameter. Conservative with
			// HasEval: an Eval may synthesize the reference and it is not visible to the static scan.
			resultingProgram.ReferencesNow = hasEvalInProgram || resultingProgram.ScriptReferencesSystemNow();
			return resultingProgram;
		}

		private void ParseWhitespace()
		{
			lexer.Accept(TokenType.eol);
		}

		private void ParseLineComments()
		{
			var _ = lexer.CurrentLexeme();
			lexer.Accept(TokenType.lineComment);
		}

        private int[] IncLevel(int[] level, int lastValue)
        {
            int len = level.Length;
            int[] result = new int[len + 1];
            Array.Copy(level, result, len);
            result[len] = lastValue;
            return result;
        }

		private Statement ParseStatement(int[] currLevel, bool isQuery, bool isCheck, ref int blockNumber)
		{
			Statement result = null;
			TokenType type = lexer.CurrentToken.Type;
			switch (type)
			{
				case TokenType.print:
					result = ParsePrintStatement(currLevel, isCheck);
					break;
				case TokenType.expose:
					result = ParseExposeStatement(currLevel, isQuery, isCheck);
					break;
				case TokenType.IF:
					result = ParseIfStatement(currLevel, ref blockNumber, isQuery, isCheck);
					break;
                case TokenType.FOREACH:
                    result = ParseForEachStatement(currLevel, ref blockNumber, isQuery, isQuery);
                    break;
                case TokenType.upgrade:
                    result = ParseUpgradeStatement(currLevel, isQuery, isCheck);
                    break;
                case TokenType.tell:
                    result = ParseTellStatement(currLevel, isQuery, isCheck);
                    break;
                case TokenType.define:
                    result = ParseDefineActionStatement(currLevel, isQuery, isCheck);
                    break;
                case TokenType.begin:
					result = ParseBlock(IncLevel(currLevel, ++blockNumber), isQuery, isCheck);
					break;
                case TokenType.id:
					result = ParseCreateOrCallStatement(currLevel, isQuery, isCheck);
					break;
                case TokenType.EVAL:
                    result = ParseEvalStatement(currLevel, isQuery, isCheck);
                    break;
                case TokenType.lineComment:
					result = ParseLineCommentStatement();
					break;
				case TokenType.check:
					result = ParseCheckStatement(currLevel, isQuery, isCheck);
					break;
				case TokenType.notify:
					result = ParseNotifyStatement(currLevel, isQuery, isCheck);
					break;
                default:
					var problematicLexeme = lexer.CurrentLexeme();
					throw new LanguageException($"Unexpected token '{problematicLexeme}' at line {Row()}, column {Column()}: expected the start of a statement.", problematicLexeme.ToString(), Row(), Column());
			}
			lastValidStatement = result;
			return result;
		}

		private Type ParseTypeName()
		{
			ReadOnlySpan<char> typeName = lexer.CurrentLexeme();
			Type type = null;

			if (typeName.Equals("int".AsSpan(), StringComparison.OrdinalIgnoreCase))
				type = typeof(int);
			else if (typeName.Equals("string".AsSpan(), StringComparison.OrdinalIgnoreCase))
				type = typeof(string);
			else if (typeName.Equals("bool".AsSpan(), StringComparison.OrdinalIgnoreCase))
				type = typeof(bool);
			else if (typeName.Equals("double".AsSpan(), StringComparison.OrdinalIgnoreCase))
				type = typeof(double);
			else if (typeName.Equals("datetime".AsSpan(), StringComparison.OrdinalIgnoreCase))
				type = typeof(DateTime);
			else if (typeName.Equals("decimal".AsSpan(), StringComparison.OrdinalIgnoreCase))
				type = typeof(decimal);

			// An @parameter typed as a domain enum is journaled by the type NAME
			// (Parameters.CanonicalTypeName emits type.Name); replay re-parses that
			// header `define action (state:StateEnum) as ...` and resolves the name via
			// the actor's DomainLibraries (which already index enums by name). The value
			// travels by member name in the arguments blob (Parameters.ArgumentsValue
			// uses Enum.Parse), readable and symbolic ('FL', not its ordinal).
			if (type == null && libraries.TryGetType(typeName.ToString(), out Type domainType) && domainType.IsEnum)
			{
				type = domainType;
			}

			if (type == null)
			{
				throw new LanguageException($"Invalid type in procedure parameters: '{typeName}' at line {Row()}, column {Column()}. Valid primitive types: int, string, bool, double, datetime, decimal (or a known domain enum).", typeName.ToString(), Row(), Column());
			}
			lexer.Accept();

			// Collection (array) suffix `<elem>[]`. A collection @parameter renders its
			// type as `<elem>[]` on the journal (Parameters.CanonicalTypeName via
			// UserParametersAsCanonicalText). Replay re-parses that `define action`
			// header through this parser, so the main DSL parser must consume the `[]`
			// just like the internal Parameters parser already does (Parameters.IsArray).
			// Without this, ParseDefineActionParameterList stops after the base type and
			// the trailing lBracket aborts with "Expected token type 'comma'".
			if (lexer.CurrentToken.Type == TokenType.lBracket)
			{
				lexer.Accept(TokenType.lBracket);
				lexer.Accept(TokenType.rBracket);
				type = type.MakeArrayType();
			}
			return type;
		}

		private Statement ParseIfStatement(int[] currLevel, ref int blockNumber, bool isQuery, bool isCheck)
		{
			Statement result;
			lexer.Accept();
			lexer.Accept(TokenType.lParen);
			AstExpression exp = ParseLogicalExpression(currLevel);
			lexer.Accept(TokenType.rParen);

            int newBlockNumber = 0;
			Statement ifCommands = ParseStatement(IncLevel(currLevel, ++blockNumber), isQuery, isCheck, ref newBlockNumber);

			if (lexer.CurrentToken.Type == TokenType.ELSE)
			{
				lexer.Accept();
				Statement elseBranchStatement = ParseStatement(IncLevel(currLevel, ++blockNumber), isQuery, isCheck, ref newBlockNumber);
				result = new IfStatement(symbolTable, exp, ifCommands, elseBranchStatement);
			}
			else
			{
				result = new IfStatement(symbolTable, exp, ifCommands);
			}
			return result;
		}

		private Statement ParseCheckStatement(int[] currLevel, bool isQuery, bool isCheck)
		{
			Statement result;
			AstExpression reason;
			
			lexer.Accept(TokenType.check);
			lexer.Accept(TokenType.lParen);
			AstExpression exp = ParseLogicalExpression(currLevel);

			lexer.Accept(TokenType.rParen);

			TokenType tokenType = lexer.CurrentToken.Type;

			if (tokenType != TokenType.id) throw new LanguageException($"Expected 'error', 'warning' or 'information' after 'check(...)' at line {Row()}, column {Column()}, but found token type '{tokenType}'.", lexer.CurrentLexeme().ToString(), Row(), Column());

			ReadOnlySpan<char> value = lexer.CurrentLexeme();
			if (value.Equals("ERROR".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				result = new CheckStatement(exp, new Error(reason));
			}
			else if (value.Equals("INFORMATION".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				result = new CheckStatement(exp, new Information(reason));
			}
			else if (value.Equals("WARNING".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				result = new CheckStatement(exp, new Warning(reason));
			}
			else
			{
				throw new LanguageException($"Expected 'error', 'warning' or 'information' after 'check(...)' at line {Row()}, column {Column()}, but found '{value}'.", value.ToString(), Row(), Column());
			}
			lexer.Accept(TokenType.semicolon);

			return result;
		}

		private Statement ParseNotifyStatement(int[] currLevel, bool isQuery, bool isCheck)
		{
			Statement result;
			AstExpression reason;

			lexer.Accept(TokenType.notify);

			TokenType tokenType = lexer.CurrentToken.Type;

			if (tokenType != TokenType.id) throw new LanguageException($"Expected 'error', 'warning' or 'information' after 'notify' at line {Row()}, column {Column()}, but found token type '{tokenType}'.", lexer.CurrentLexeme().ToString(), Row(), Column());

			ReadOnlySpan<char> value = lexer.CurrentLexeme();
			if (value.Equals("ERROR".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				result = new CheckStatement(LiteralBoolean.LiteralFalse, new Error(reason));
			}
			else if (value.Equals("INFORMATION".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				result = new CheckStatement(LiteralBoolean.LiteralFalse, new Information(reason));
			}
			else if (value.Equals("WARNING".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				result = new CheckStatement(LiteralBoolean.LiteralFalse, new Warning(reason));
			}
			else
			{
				throw new LanguageException($"Expected 'error', 'warning' or 'information' after 'notify' at line {Row()}, column {Column()}, but found '{value}'.", value.ToString(), Row(), Column());
			}

			lexer.Accept(TokenType.semicolon);
			
			return result;
		}

		private Statement ParseForEachStatement(int[] currLevel, ref int blockNumber, bool isQuery, bool isCheck)
        {
            currLevel = IncLevel(currLevel, ++blockNumber);
            Statement result;
            lexer.Accept(TokenType.FOREACH);
            lexer.Accept(TokenType.lParen);
            Id id = (Id) ParseId(currLevel);

            Id indexId = null;
            Id idElemento = null;
            bool indexOnly = false;

            if (lexer.CurrentToken.Type == TokenType.comma)
            {
                lexer.Accept(TokenType.comma);
                indexId = id;
                if (lexer.CurrentToken.Type == TokenType.wildcard)
                {
                    lexer.Accept(TokenType.wildcard);
                    indexOnly = true;
                    idElemento = null;
                }
                else
                {
                    idElemento = (Id) ParseId(currLevel);
                }
            }

            // Accept both 'in' (canonical) and ':' (deprecated alias)
            if (lexer.CurrentToken.Type == TokenType.IN)
            {
                lexer.Accept(TokenType.IN);
            }
            else if (lexer.CurrentToken.Type == TokenType.colon)
            {
                lexer.Accept(TokenType.colon);
            }
            else
            {
                throw new LanguageException($"Expected 'in' or ':' in 'foreach' loop at line {Row()}, column {Column()}, but found '{lexer.CurrentToken.Type}'.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            AstExpression exp = ParseExpression(currLevel);
            lexer.Accept(TokenType.rParen);
            int newBlockNumber = 0;
            Statement foreachBody = ParseStatement(IncLevel(currLevel, ++blockNumber), isQuery, isCheck, ref newBlockNumber);

            if (indexId != null)
            {
                result = new ForEachStatement(symbolTable, indexId, idElemento, indexOnly, exp, foreachBody);
            }
            else
            {
                result = new ForEachStatement(symbolTable, id, exp, foreachBody);
            }
            return result;
        }

        private Statement ParseUpgradeStatement(int[] currLevel, bool isQuery, bool isCheck)
        {
            if (isQuery)
            {
                throw new LanguageException("'upgrade' is not valid in PerformQuery. It can only be used in PerformCmd because it persists actor state.");
            }

            // 'upgrade' seeds/migrates the actor by creating journaled globals, so it
            // writes the journal. A check is read-only (like a query): it takes no write
            // lock and persists nothing. Reject 'upgrade' here for the same reason
            // 'expose' and 'tell' are rejected in read-only contexts -- it is a
            // command-only construct. Skip during rehydration, which re-parses journaled
            // COMMANDS (that legitimately carry 'upgrade') with isCheck overloaded.
            if (isCheck && !parsingRehydration)
            {
                throw new LanguageException("'upgrade' is not valid inside a check. It can only be used in PerformCmd because it persists actor state (it creates journaled globals).");
            }

            lexer.Accept(TokenType.upgrade);
            lexer.Accept(TokenType.lParen);

            if (lexer.CurrentToken.Type != TokenType.stringLit)
            {
                throw new LanguageException($"'upgrade' requires a string literal as its name; variables and expressions are not allowed. Example: upgrade('seed') {{ ... }}; (at line {Row()}, column {Column()}).", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            string upgradeName = lexer.CurrentLexeme().ToString();
            lexer.Accept(TokenType.stringLit);
            lexer.Accept(TokenType.rParen);

            if (string.IsNullOrWhiteSpace(upgradeName))
            {
                throw new LanguageException($"The 'upgrade' name cannot be empty (at line {Row()}, column {Column()}).");
            }

            if (!upgradeNamesInProgram.Add(upgradeName))
            {
                throw new LanguageException($"'upgrade' name '{upgradeName}' appears twice in the same script. Each 'upgrade' must have a unique name within the Program.");
            }

            if (lexer.CurrentToken.Type != TokenType.begin)
            {
                throw new LanguageException($"'upgrade' requires a mandatory '{{ ... }}' block (at line {Row()}, column {Column()}, found '{lexer.CurrentToken.Type}').", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            // KEY: parse the body at currLevel (NOT IncLevel) — 'upgrade' is scope-transparent.
            // Variables declared inside the body stay at the level where the 'upgrade' appears,
            // not at the block level. This means upgrade('seed') { x = ... } creates x as global
            // if the 'upgrade' appears at the top level of the Program.
            Statement body = ParseBlock(currLevel, isQuery, isCheck);

            return new UpgradeStatement(symbolTable, upgradeName, body);
        }

        // ============================================================
        // Define-action statement — Phase 1 of the Action refactor
        // (project_puppeteer_action_refactor_plan.md).
        //
        // Grammar:
        //   defineActionStatement := "define" "action" number LPAREN paramList RPAREN
        //                            "as" body "end" SEMICOLON
        //   paramList             := empty | param (COMMA param)*
        //   param                 := id COLON typeName            // id may be `name` or `@name`;
        //                                                          // canonical text drops the '@'
        //   body                  := statement*
        //
        // 'action' and 'end' are contextual keywords (TokenType.id with matching lexeme),
        // same pattern as the saga verbs in the Tell roadmap. Only 'define' is a formal
        // TokenType because it is statement-level. Parameter modifiers (In/Out/InOut/Eval)
        // are deliberately out of scope for Phase 1 — auto-emit lands in Phase 4 and the
        // first invocation that needs a non-default modifier will pin the syntax then.
        //
        // The Statement is parser-only: Execute and ExecuteExpression both throw. Phase 1
        // exists so the journal sentence round-trips through the parser; Phase 4 wires
        // the runtime emission and cache population.
        // ============================================================
        private Statement ParseDefineActionStatement(int[] currLevel, bool isQuery, bool isCheck)
        {
            int defineRow = Row();
            int defineColumn = Column();
            lexer.Accept(TokenType.define);

            // 'action' contextual keyword.
            if (!(lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("action")))
            {
                throw new LanguageException($"'define' must be followed by 'action' but found '{lexer.CurrentLexeme()}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            lexer.Accept(TokenType.id);

            if (lexer.CurrentToken.Type != TokenType.number)
            {
                throw new LanguageException($"'define action' requires a numeric action id but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            string actionIdLexeme = lexer.CurrentLexeme().ToString();
            if (!int.TryParse(actionIdLexeme, out int actionId))
            {
                throw new LanguageException($"'define action' id '{actionIdLexeme}' is not a valid integer at line {Row()}, column {Column()}.", actionIdLexeme, Row(), Column());
            }
            lexer.Accept(TokenType.number);

            lexer.Accept(TokenType.lParen);
            string parametersText = ParseDefineActionParameterList();
            lexer.Accept(TokenType.rParen);

            lexer.Accept(TokenType.@as);

            List<Statement> body = new List<Statement>();
            int blockNumber = 0;
            while (true)
            {
                if (lexer.CurrentToken.Type == TokenType.eof)
                {
                    throw new LanguageException($"'define action' starting at line {defineRow}, column {defineColumn} is not terminated. Expected 'end;' before end of input.", "eof", Row(), Column());
                }
                if (lexer.CurrentToken.Type == TokenType.eol)
                {
                    lexer.Accept(TokenType.eol);
                    continue;
                }
                if (lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("end"))
                {
                    break;
                }
                body.Add(ParseStatement(currLevel, isQuery, isCheck, ref blockNumber));
            }

            // Consume 'end' (contextual keyword) and the trailing semicolon.
            lexer.Accept(TokenType.id);
            lexer.Accept(TokenType.semicolon);

            return new DefineActionStatement(actionId, parametersText, body.ToArray());
        }

        // Parses a parameter list of the form `name1:type1, name2:type2` (possibly empty).
        // The canonical text uses `name:type` separated by `, ` (comma + single space).
        //
        // The DSL accepts both `@name:type` and `name:type` at the input — the Lexer treats
        // '@' as an alias-prefix that it silently drops on the way to the token stream
        // (by design: "@ at the beginning of Id's name is just an alias of
        // the same Id without @. It is for Parameter's legibility"). The canonical text
        // produced here therefore never contains '@' regardless of the input form, and
        // round-trip through the parser is a fixed point. Decision (A) signed at the close
        // of Phase 1 (2026-05-09) — option (B), modifying the Lexer to preserve '@', was
        // ruled out as out-of-scope for Phase 1.
        //
        // NO parameter-order normalization (signed at the start of Phase 1: order is semantically
        // significant because callsite arguments are positionally bound).
        //
        // Each entry is `[out|inout ]name:type`. An optional lowercase `out`/`inout` keyword
        // carries the parameter modifier; with no keyword the parameter is In (the default,
        // and the shape of every pre-existing journal). `out`/`inout` lex as plain
        // identifiers (they are not reserved tokens), so a modified entry surfaces as two
        // consecutive id tokens before the ':'. Disambiguation without lookahead: after
        // accepting the first id, if the next token is ':' the id was the parameter NAME
        // (In); otherwise it was the modifier keyword and the following id is the name. A
        // parameter legitimately NAMED `out`/`inout` is therefore still parsed correctly —
        // its ':' comes immediately after it. The modifier is re-emitted canonically so the
        // ParametersText round-trips (and CanonicalDeclarationsToParametersString can lift
        // it into the Parameters ctor grammar). `in`/`eval` are NOT accepted as keywords
        // here (they are reserved tokens and are never emitted): In and Eval are prefix-less.
        private string ParseDefineActionParameterList()
        {
            StringBuilder sb = new StringBuilder();
            bool first = true;
            while (lexer.CurrentToken.Type != TokenType.rParen)
            {
                if (!first)
                {
                    lexer.Accept(TokenType.comma);
                    sb.Append(", ");
                }
                first = false;

                if (lexer.CurrentToken.Type != TokenType.id)
                {
                    throw new LanguageException($"'define action' parameter expects an identifier (e.g. id:int, @id:int, out id:int) but found token type '{lexer.CurrentToken.Type}' with lexeme '{lexer.CurrentLexeme()}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
                }
                string firstLexeme = lexer.CurrentLexeme().ToString();
                lexer.Accept(TokenType.id);

                // If the ':' does not follow immediately, the first id was a modifier keyword
                // and the real name is the next id.
                if (lexer.CurrentToken.Type != TokenType.colon)
                {
                    if (string.Equals(firstLexeme, Parameters.OutModifierKeyword, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(firstLexeme, Parameters.InOutModifierKeyword, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(firstLexeme.ToLowerInvariant());
                        sb.Append(' ');
                    }
                    else
                    {
                        throw new LanguageException($"'define action' parameter modifier '{firstLexeme}' is not valid (expected 'out' or 'inout') at line {Row()}, column {Column()}.", firstLexeme, Row(), Column());
                    }

                    if (lexer.CurrentToken.Type != TokenType.id)
                    {
                        throw new LanguageException($"'define action' parameter expects a name after modifier '{firstLexeme}' but found token type '{lexer.CurrentToken.Type}' with lexeme '{lexer.CurrentLexeme()}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
                    }
                    sb.Append(lexer.CurrentLexeme());
                    lexer.Accept(TokenType.id);
                }
                else
                {
                    sb.Append(firstLexeme);
                }

                lexer.Accept(TokenType.colon);
                sb.Append(':');

                Type type = ParseTypeName();
                sb.Append(CanonicalTypeName(type));
            }
            return sb.ToString();
        }

        // Phase 1 canonical render of primitive types inside `define action` parameter
        // lists. Lower-case to match the DSL's textual surface (the Lexer matches type
        // names case-insensitively but the canonical journal sentence picks one casing
        // so two equivalent declarations don't diverge).
        private static string CanonicalTypeName(Type type)
        {
            // Collection (array) types render as `<elem>[]`, matching the journal text
            // produced by Parameters.CanonicalTypeName so a Define header round-trips
            // through the parser as a fixed point.
            if (type.IsArray)
            {
                return CanonicalTypeName(type.GetElementType()) + "[]";
            }
            if (type == typeof(int)) return "int";
            if (type == typeof(string)) return "string";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(double)) return "double";
            if (type == typeof(DateTime)) return "datetime";
            if (type == typeof(decimal)) return "decimal";
            // Domain enum: rendered by its type name (resolved when re-parsing via
            // DomainLibraries). Fixed-point round-trip with Parameters.CanonicalTypeName.
            if (type.IsEnum) return type.Name;
            throw new LanguageException($"Type '{type.Name}' is not a valid primitive in 'define action' parameter lists.");
        }

        // ============================================================
        // Tell statement — a directed assertive speech act.
        // Grammar:
        //   tellStatement := TELL ( ackForm | unackForm | assertiveForm ) SEMICOLON
        //   ackForm       := "ack" stringLit "from" id [LPAREN expr RPAREN]
        //   unackForm     := stringLit "unacknowledged" "by" id
        //   assertiveForm := id [ "with" exprList ] "to" id [LPAREN expr RPAREN] [ "once" expr ]
        //
        // <Message> (the assertive's id) is the SENDER's own vocabulary, validated as
        // a message name — NOT as a method on the addressee. There is no coupling to
        // the receiver's API: the message is defined on first use (its typed signature
        // deduced from the `with` payload). <Addressee> is a logical role resolved by
        // the runtime binding table; the DSL never names the transport. 'ack', 'from',
        // 'with', 'to', 'once', 'unacknowledged', 'by' are contextual keywords
        // (TokenType.id with matching lexeme), the same pattern as 'where' and 'list'.
        // The ack and unacknowledged forms are framework-emitted (rejected if
        // user-authored at runtime); they begin with "ack" / a stringLit so they
        // disambiguate from the assertive form (which begins with the message id).
        // ============================================================
        private Statement ParseTellStatement(int[] currLevel, bool isQuery, bool isCheck)
        {
            if (isQuery)
            {
                throw new LanguageException($"'tell' is not valid in PerformQuery. It can only be used in PerformCmd because it produces cross-actor causation that the journal must record (at line {Row()}, column {Column()}).");
            }

            lexer.Accept(TokenType.tell);

            // Form: tell ack '<id>' from <Addressee>[(<instanceId>)]
            if (lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("ack"))
            {
                Statement ackResult = ParseTellAckBody(currLevel);
                lexer.Accept(TokenType.semicolon);
                return ackResult;
            }

            // Form: tell '<id>' unacknowledged by <Addressee>
            if (lexer.CurrentToken.Type == TokenType.stringLit)
            {
                Statement unackResult = ParseTellUnacknowledgedBody();
                lexer.Accept(TokenType.semicolon);
                return unackResult;
            }

            // Form: tell <Message> [with <args>] to <Addressee>[(<id>)] [once <idExpr>]
            if (lexer.CurrentToken.Type != TokenType.id)
            {
                throw new LanguageException($"'tell' must be followed by a message name, 'ack', or a quoted envelope id, but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            string messageName = lexer.CurrentLexeme().ToString();
            lexer.Accept(TokenType.id);

            AstExpression[] withArgs = Array.Empty<AstExpression>();
            if (lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("with"))
            {
                lexer.Accept(TokenType.id); // consume 'with'
                withArgs = ParseWithArguments(currLevel);
            }

            if (!(lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("to")))
            {
                throw new LanguageException($"'tell <Message>' must be followed by 'to <Addressee>' (optionally after a 'with' payload), but found '{lexer.CurrentLexeme()}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            lexer.Accept(TokenType.id); // consume 'to'

            if (lexer.CurrentToken.Type != TokenType.id)
            {
                throw new LanguageException($"'tell ... to' must be followed by an addressee role identifier, but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            string addressee = lexer.CurrentLexeme().ToString();
            lexer.Accept(TokenType.id);

            AstExpression addresseeInstanceId = null;
            if (lexer.CurrentToken.Type == TokenType.lParen)
            {
                lexer.Accept(TokenType.lParen);
                AstExpression[] instanceArgs = ParseArguments(currLevel);
                if (instanceArgs.Length != 1)
                {
                    throw new LanguageException($"'tell ... to <Addressee>(...)' instance id must be a single expression in parens, but got {instanceArgs.Length} expressions at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
                }
                addresseeInstanceId = instanceArgs[0];
                lexer.Accept(TokenType.rParen);
            }

            // `once` takes an EXPRESSION, not just a string literal. The identity is
            // resolved per event at execute-time, so a captured `@parameter`
            // (once @order) yields a meaningful per-utterance id, a literal
            // (once 'welcome-42') keeps the constant-key behavior, and a string-valued
            // expression (once 'reward-' + @order) composes them. This is the issuing
            // counterpart of the matcher's `once <param>` (PatternParser) and reuses the
            // same expression machinery as the `with` payload.
            AstExpression onceExpression = null;
            if (lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("once"))
            {
                lexer.Accept(TokenType.id); // consume 'once'
                onceExpression = ParseLogicalExpression(currLevel);
            }

            lexer.Accept(TokenType.semicolon);

            return new AssertiveTellStatement(symbolTable, messageName, withArgs, addressee, addresseeInstanceId, onceExpression);
        }

        // Parse the comma-separated `with` payload, terminated by the 'to' keyword.
        private AstExpression[] ParseWithArguments(int[] currLevel)
        {
            List<AstExpression> args = new List<AstExpression>();
            while (true)
            {
                AstExpression arg = ParseLogicalExpression(currLevel);
                args.Add(arg);
                if (lexer.CurrentToken.Type == TokenType.comma)
                {
                    lexer.Accept(TokenType.comma);
                    continue;
                }
                break;
            }
            return args.ToArray();
        }

        private Statement ParseTellAckBody(int[] currLevel)
        {
            // Already at the lexeme "ack" (TokenType.id).
            lexer.Accept(TokenType.id); // consume 'ack'

            if (lexer.CurrentToken.Type != TokenType.stringLit)
            {
                throw new LanguageException($"'tell ack' requires a string literal as the ack id, but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            string ackId = lexer.CurrentLexeme().ToString();
            lexer.Accept(TokenType.stringLit);

            if (!(lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("from")))
            {
                throw new LanguageException($"'tell ack' requires 'from' after the ack id, but found '{lexer.CurrentLexeme()}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            lexer.Accept(TokenType.id); // consume 'from'

            if (lexer.CurrentToken.Type != TokenType.id)
            {
                throw new LanguageException($"'tell ack ... from' must be followed by an addressee role identifier, but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            string fromAddressee = lexer.CurrentLexeme().ToString();
            lexer.Accept(TokenType.id);

            AstExpression fromInstanceId = null;
            if (lexer.CurrentToken.Type == TokenType.lParen)
            {
                lexer.Accept(TokenType.lParen);
                AstExpression[] fromIdArgs = ParseArguments(currLevel);
                if (fromIdArgs.Length != 1)
                {
                    throw new LanguageException($"'tell ack ... from <Addressee>(...)' instance id must be a single expression in parens, but got {fromIdArgs.Length} expressions at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
                }
                fromInstanceId = fromIdArgs[0];
                lexer.Accept(TokenType.rParen);
            }

            return new TellAckStatement(symbolTable, ackId, fromAddressee, fromInstanceId);
        }

        // Parse `'<envelopeId>' unacknowledged by <Addressee>`. Entered with the
        // current token positioned at the envelope-id string literal. The contextual
        // keywords 'unacknowledged' and 'by' lex as TokenType.id.
        private Statement ParseTellUnacknowledgedBody()
        {
            // Already at the envelope id string literal.
            string envelopeId = lexer.CurrentLexeme().ToString();
            lexer.Accept(TokenType.stringLit);

            if (!(lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("unacknowledged")))
            {
                throw new LanguageException($"'tell '<id>'' must be followed by 'unacknowledged by', but found '{lexer.CurrentLexeme()}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            lexer.Accept(TokenType.id); // consume 'unacknowledged'

            if (!(lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("by")))
            {
                throw new LanguageException($"'tell '<id>' unacknowledged' must be followed by 'by', but found '{lexer.CurrentLexeme()}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            lexer.Accept(TokenType.id); // consume 'by'

            if (lexer.CurrentToken.Type != TokenType.id)
            {
                throw new LanguageException($"'tell '<id>' unacknowledged by' must be followed by an addressee role identifier, but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            string addressee = lexer.CurrentLexeme().ToString();
            lexer.Accept(TokenType.id);

            return new TellUnacknowledgedStatement(symbolTable, envelopeId, addressee);
        }

        private bool LexemeEqualsIgnoreCase(string keyword)
        {
            return lexer.CurrentLexeme().Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private Statement ParseLineCommentStatement()
		{
			var comentario = lexer.CurrentLexeme();
			lexer.Accept(TokenType.lineComment);
			return new NullStatement(comentario.ToString());
		}

		private Statement ParseCreateOrCallStatement(int[] currLevel, bool isQuery, bool isCheck)
		{
			// NOTE: the "no global variable declarations in queries" rule is NOT enforced
			// here. At parse time the '@' alias is already dropped by the Lexer and the
			// caller's parameter set is unknown, so a top-level `id = value;` is
			// indistinguishable between declaring a new global (forbidden in a query) and
			// assigning to a pre-declared @Out parameter (allowed). The rule is enforced
			// in Program.SolveReferences, where each LValue's scope (Global vs Parameter)
			// is resolved against the actual parameter set. See RejectGlobalDeclarationInQuery.
			Statement result;
			AstExpression dot = ParseDotChain(currLevel);
			bool isCreateCommand = lexer.CurrentToken.Type == TokenType.assign;
			if (isCreateCommand)
			{
				result = ParseCreateStatement(dot, currLevel, isQuery, isCheck);
			}
			else
			{
				result = ParseCallStatement(dot, currLevel);
			}
			lexer.Accept(TokenType.semicolon);
			return result;
		}

		private Statement ParseBlock(int[] currLevel, bool isQuery, bool isCheck)
		{
			lexer.Accept(TokenType.begin);
			List<Statement> blockStatements = new List<Statement>();
            int blockNumber = 0;
			while (lexer.CurrentToken.Type != TokenType.end && lexer.CurrentToken.Type != TokenType.eof)
			{
					blockStatements.Add(ParseStatement(currLevel, isQuery, isCheck, ref blockNumber));
			}
			lexer.Accept(TokenType.end);
			if (lexer.CurrentToken.Type == TokenType.semicolon)
			{
				lexer.Accept();
			}
            Statement[] statements = blockStatements.ToArray();
			return new BlockStatement(symbolTable, statements);
		}

		private Statement ParseCallStatement(AstExpression dot, int[] currLevel)
		{
			return new CallStatement(symbolTable, dot);
		}

		private Statement ParseCreateStatement(AstExpression lValue, int[] currLevel, bool isQuery, bool isCheck)
		{
			lexer.Accept(TokenType.assign);
			AstExpression rValue = ParseLogicalExpression(currLevel);
            return new NewInstanceStatement(symbolTable, lValue, rValue);
        }

		private Statement ParsePrintStatement(int[] currLevel, bool isCheck)
		{
			if (isCheck && currLevel.Length == 0)
			{
				throw new LanguageException($"'print' is not allowed inside 'check' statements (at line {Row()}, column {Column()}).");
			}

			lexer.Accept();

			PrintStatementIndividual firstPrint = null;
			List<PrintStatementIndividual> prints = null;

			while (true)
			{
				AstExpression exp = ParseExpression(currLevel);

				if (lexer.CurrentToken.Type == TokenType.@as)
				{
					lexer.Accept(TokenType.@as);
				}

				ReadOnlySpan<char> alias;
				if (lexer.CurrentToken.Type == TokenType.id)
				{
					alias = lexer.CurrentLexeme();
					lexer.Accept(TokenType.id);
				}
				else if (lexer.CurrentToken.Type == TokenType.stringLit)
				{
					alias = lexer.CurrentLexeme();
					lexer.Accept(TokenType.stringLit);
				}
				else
				{
					throw new LanguageException($"Expected an alias for the 'print' expression '{exp.ToString()}' at line {Row()}, column {Column()}, but found '{lexer.CurrentToken.Type}'.", lexer.CurrentLexeme().ToString(), Row(), Column());
				}

				var print = new PrintStatementIndividual(exp, alias.ToString());

				if (firstPrint == null)
				{
					firstPrint = print;
				}
				else
				{
					if (prints == null) prints = new List<PrintStatementIndividual>() { firstPrint };
					prints.Add(print);
				}

				if (lexer.CurrentToken.Type == TokenType.comma)
				{
					lexer.Accept(TokenType.comma);
					continue;
				}
				else
				{
					break;
				}
			}

			lexer.Accept(TokenType.semicolon);

			if (prints == null)
			{
				return firstPrint;
			}
			else
			{
				return new PrintStatement(prints);
			}
		}

		private Statement ParseExposeStatement(int[] currLevel, bool isQuery, bool isCheck)
		{
			if (isQuery) throw new LanguageException($"'expose' is not allowed inside queries (at line {Row()}, column {Column()}). 'expose' persists data and only makes sense in commands.");

			if (isCheck && currLevel.Length == 0)
			{
				throw new LanguageException($"'expose' is not allowed inside 'check' statements (at line {Row()}, column {Column()}).");
			}

			lexer.Accept();

			ExposeStatementIndividual firstExpose = null;
			List<ExposeStatementIndividual> exposes = null;
			HashSet<string> aliasesUsados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			while (true)
			{
				AstExpression exp = ParseExpression(currLevel);

				if (lexer.CurrentToken.Type == TokenType.@as)
				{
					lexer.Accept(TokenType.@as);
				}

				ReadOnlySpan<char> alias;
				if (lexer.CurrentToken.Type == TokenType.id)
				{
					alias = lexer.CurrentLexeme();

					if (alias.Length > 0 && alias[0] == '@')
					{
						throw new LanguageException($"The alias '{alias.ToString()}' in 'expose' cannot start with '@' (at line {Row()}, column {Column()}). 'expose' aliases must be simple identifiers.", alias.ToString(), Row(), Column());
					}

					string aliasStr = alias.ToString();
					if (aliasesUsados.Contains(aliasStr))
					{
						throw new LanguageException($"The alias '{aliasStr}' is duplicated in the 'expose' statement (at line {Row()}, column {Column()}). Each alias must be unique.", aliasStr, Row(), Column());
					}
					aliasesUsados.Add(aliasStr);

					lexer.Accept(TokenType.id);
				}
				else if (lexer.CurrentToken.Type == TokenType.stringLit)
				{
					alias = lexer.CurrentLexeme();

					if (alias.Length > 0 && alias[0] == '@')
					{
						throw new LanguageException($"The alias '{alias.ToString()}' in 'expose' cannot start with '@' (at line {Row()}, column {Column()}). 'expose' aliases must be simple identifiers.", alias.ToString(), Row(), Column());
					}

					string aliasStr = alias.ToString();
					if (aliasesUsados.Contains(aliasStr))
					{
						throw new LanguageException($"The alias '{aliasStr}' is duplicated in the 'expose' statement (at line {Row()}, column {Column()}). Each alias must be unique.", aliasStr, Row(), Column());
					}
					aliasesUsados.Add(aliasStr);

					lexer.Accept(TokenType.stringLit);
				}
				else
				{
					throw new LanguageException($"Expected an alias for the 'expose' expression '{exp.ToString()}' at line {Row()}, column {Column()}, but found '{lexer.CurrentToken.Type}'.", lexer.CurrentLexeme().ToString(), Row(), Column());
				}

				var expose = new ExposeStatementIndividual(exp, alias.ToString());

				if (firstExpose == null)
				{
					firstExpose = expose;
				}
				else
				{
					if (exposes == null) exposes = new List<ExposeStatementIndividual>() { firstExpose };
					exposes.Add(expose);
				}

				if (lexer.CurrentToken.Type == TokenType.comma)
				{
					lexer.Accept(TokenType.comma);
					continue;
				}
				else
				{
					break;
				}
			}

			lexer.Accept(TokenType.semicolon);

			if (exposes == null)
			{
				return firstExpose;
			}
			else
			{
				return new ExposeStatement(exposes);
			}
		}

		private Statement ParseEvalStatement(int[] currLevel, bool isQuery, bool isCheck)
        {
            lexer.Accept(TokenType.EVAL);
            lexer.Accept(TokenType.lParen);
            AstExpression exp = ParseExpression(currLevel);
            lexer.Accept(TokenType.rParen);
            lexer.Accept(TokenType.semicolon);
            hasEvalInProgram = true;
            return new EvalStatement(this.libraries, symbolTable, exp, currLevel, isQuery, isCheck);
        }

        private AstExpression ParseDotChain(int[] currLevel)
		{
			AstExpression result = ParseId(currLevel);
			TokenType type = lexer.CurrentToken.Type;
			while (true)
			{
				switch (type)
				{
					case TokenType.dot:
						lexer.Accept();
						string method = lexer.CurrentLexeme().ToString();
						lexer.Accept(TokenType.id);

						if (lexer.CurrentToken.Type != TokenType.lParen)
						{
							if (result is Id id)
								result = new DottedId(libraries, symbolTable, id, method);
							else if (result is DotAccess dot)
								result = new ChainedDotAccess(dot, method);
							else if (result is NewInstance instance)
								result = new ChainedDotAccess(instance, method);
							else
								throw new LanguageException($"Cannot apply the dot operator ('.') to type '{result.GetType().Name}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
						}
						else
						{
							lexer.Accept(TokenType.lParen);
							var args = ParseArguments(currLevel);
							lexer.Accept(TokenType.rParen);

							if (result is Id id)
							{
								// Optional 'in' clause to disambiguate the namespace homonymy of a
								// static method call 'Clase.Metodo(args) in Namespace.Sub'. Same parser
								// as the Clase(args) in Namespace construction. Only allowed when the
								// receiver is an Id (a potential class); DottedId validates that it
								// actually resolves to a class and not to a variable.
								string staticNamespace = ParseOptionalInNamespace();
								result = new DottedId(libraries, symbolTable, id, method, args, staticNamespace);
							}
							else if (result is DotAccess dot)
								result = new ChainedDotAccess(dot, method, args);
							else if (result is NewInstance instance)
								result = new ChainedDotAccess(instance, method, args);
							else
								throw new LanguageException($"Cannot apply the dot operator ('.') to type '{result.GetType().Name}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
						}
						break;
				case TokenType.lBracket:
					lexer.Accept();
					List<AstExpression> subscriptIndices = new List<AstExpression>();
					subscriptIndices.Add(ParseLogicalExpression(currLevel));
					while (lexer.CurrentToken.Type == TokenType.comma)
					{
						lexer.Accept();
						subscriptIndices.Add(ParseLogicalExpression(currLevel));
					}
					lexer.Accept(TokenType.rBracket);
					result = new SubscriptAstExpression(result, subscriptIndices.ToArray());
					break;
				case TokenType.lParen:
					lexer.Accept();
					var clazz = (Id) result;
					var arguments = ParseArguments(currLevel);
					lexer.Accept(TokenType.rParen);

					string namespaceName = ParseOptionalInNamespace();

					result = new NewInstance(libraries, symbolTable, clazz, arguments, namespaceName);
					break;
					default:
						return result;
				}
				type = lexer.CurrentToken.Type;
			}
		}

		// Parses the optional 'in Namespace.Sub' clause that follows a 'Clase(args) in Ns'
		// construction or a static call 'Clase.Metodo(args) in Ns'. Returns the
		// full namespace, or null if there is no 'in' clause. Centralized so both uses
		// share exactly the same namespace grammar.
		private string ParseOptionalInNamespace()
		{
			if (lexer.CurrentToken.Type != TokenType.IN)
			{
				return null;
			}

			lexer.Accept(TokenType.IN);
			if (lexer.CurrentToken.Type != TokenType.id)
				throw new LanguageException($"Expected a namespace identifier after 'in' at line {Row()}, column {Column()}, but found '{lexer.CurrentToken.Type}'.", lexer.CurrentLexeme().ToString(), Row(), Column());

			StringBuilder namespaceBuilder = new StringBuilder();
			namespaceBuilder.Append(lexer.CurrentLexeme().ToString());
			lexer.Accept();

			while (lexer.CurrentToken.Type == TokenType.dot)
			{
				namespaceBuilder.Append('.');
				lexer.Accept();
				if (lexer.CurrentToken.Type != TokenType.id)
					throw new LanguageException($"Expected an identifier after the dot in the namespace at line {Row()}, column {Column()}, but found '{lexer.CurrentToken.Type}'.", lexer.CurrentLexeme().ToString(), Row(), Column());
				namespaceBuilder.Append(lexer.CurrentLexeme().ToString());
				lexer.Accept();
			}

			return namespaceBuilder.ToString();
		}

		private AstExpression[] ParseArguments(int[] currLevel)
		{
			bool shouldExit = false;
			bool nextClosesParen = lexer.CurrentToken.Type == TokenType.rParen;
			if (nextClosesParen)
			{
				shouldExit = true;
			}

			List<AstExpression> arguments = new List<AstExpression>();
			while (!shouldExit)
			{
				AstExpression argument = ParseLogicalExpression(currLevel);
				arguments.Add(argument);
				bool nextIsComma = lexer.CurrentToken.Type == TokenType.comma;
				nextClosesParen = lexer.CurrentToken.Type == TokenType.rParen;
				if (nextClosesParen)
				{
					shouldExit = true;
				}
				else if (nextIsComma)
				{
					lexer.Accept();
				}
				else
				{
					var problematicLexeme = lexer.CurrentLexeme();
					throw new LanguageException($"Expected an argument or a closing parenthesis ')', but found '{problematicLexeme}' at line {Row()}, column {Column()}.", problematicLexeme.ToString(), Row(), Column());
				}
			}
            AstExpression[] argumentsArr = arguments.ToArray();
			return argumentsArr;
		}

        private AstExpression ParseList(int[] currLevel)
        {
            lexer.Accept(TokenType.begin);

            // Empty collection literal {}.
            if (lexer.CurrentToken.Type == TokenType.end)
            {
                lexer.Accept(TokenType.end);
                return new LiteralList(new AstExpression[0]);
            }

            AstExpression first = ParseLogicalExpression(currLevel);

            // Range literal {start..end}: an inclusive, ascending integer sequence.
            // The '..' after the first element selects the range form over the
            // collection form. See docs/rfc/foreach-range-literal.md.
            if (lexer.CurrentToken.Type == TokenType.range)
            {
                lexer.Accept(TokenType.range);
                AstExpression rangeEnd = ParseLogicalExpression(currLevel);
                lexer.Accept(TokenType.end);
                return new LiteralRange(first, rangeEnd);
            }

            // Collection literal {a, b, c}.
            List<AstExpression> elementos = new List<AstExpression> { first };
            bool shouldExit = lexer.CurrentToken.Type == TokenType.end;
            while (!shouldExit)
            {
                bool nextIsComma = lexer.CurrentToken.Type == TokenType.comma;
                bool nextClosesList = lexer.CurrentToken.Type == TokenType.end;
                if (nextClosesList)
                {
                    shouldExit = true;
                }
                else if (nextIsComma)
                {
                    lexer.Accept();
                    elementos.Add(ParseLogicalExpression(currLevel));
                }
                else
                {
                    var problematicLexeme = lexer.CurrentLexeme();
                    throw new LanguageException($"Expected an argument or a closing brace '}}', but found '{problematicLexeme}' at line {Row()}, column {Column()}.", problematicLexeme.ToString(), Row(), Column());
                }
            }
            lexer.Accept(TokenType.end);
            AstExpression[] elementosArr = elementos.ToArray();
            return new LiteralList(elementosArr);
        }

        private AstExpression ParseId(int[] currLevel)
        {
            string id = lexer.CurrentLexeme().ToString();
            lexer.Accept(TokenType.id);
            return new Id(symbolTable, id, currLevel);
        }

		private AstExpression ParseExpression(int[] currLevel)
		{
			AstExpression result = ParseRelationalExpression(currLevel);
			return result;
		}

        private AstExpression ParseDate()
		{
            DateTime date = ParseDateValidation(lexer);
			AstExpression result;
			if (lexer.CurrentToken.Type == TokenType.time)
			{
				result = ParseDateTime(ref date);
			}
            else
            {
				result = new LiteralDateTime(date);
			}
			return result;
		}

        private DateTime ParseDateValidation(Lexer lexer)
        {
            if (lexer.CurrentToken.Type != TokenType.date)
            {
                throw new LanguageException($"Expected a date literal at line {Row()}, column {Column()}, but found token '{lexer.CurrentToken.Type}'. Please verify the date format (MM/dd/yyyy).", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            DateTime result = DateTime.Parse(lexer.CurrentLexeme(), CultureInfo.InvariantCulture);
			lexer.Accept(TokenType.date);
            return result;
        }

        private AstExpression ParseDateTime(ref DateTime date)
		{
			DateTime dateTime = ParseDateTimeValidation(ref date, lexer);
			AstExpression result = new LiteralDateTime(dateTime);
			return result;
		}

        private DateTime ParseDateTimeValidation(ref DateTime date, Lexer lexer)
        {
            bool hasTime = lexer.CurrentToken.Type == TokenType.time;
            if (!hasTime)
            {
                throw new LanguageException($"Expected a time literal at line {lexer.Row()}, column {lexer.Column()}, but found token '{lexer.CurrentToken.Type}'. Please verify the time format (HH:mm:ss).", lexer.CurrentLexeme().ToString(), lexer.Row(), lexer.Column());
            }

			ReadOnlySpan<char> timeSpan = lexer.CurrentLexeme();
			Span<char> buffer = stackalloc char[19]; // "MM/dd/yyyy HH:mm:ss" = 19 chars
			date.TryFormat(buffer.Slice(0, 10), out _, "MM/dd/yyyy", CultureInfo.InvariantCulture);
			buffer[10] = ' ';
			timeSpan.CopyTo(buffer.Slice(11));

			DateTime result = DateTime.Parse(buffer.Slice(0, 11 + timeSpan.Length), CultureInfo.InvariantCulture);
			lexer.Accept(TokenType.time);
            return result;
        }


        private bool IsRelationalOperator()
		{
			TokenType type = lexer.CurrentToken.Type;

			return type == TokenType.equality || type == TokenType.inequality || type == TokenType.lessOrEqual || type == TokenType.greaterOrEqual || type == TokenType.lessThan || type == TokenType.greaterThan;
		}

		private AstExpression ParseLogicalExpression(int[] currLevel)
		{
			AstExpression result = ParseOrExpression(currLevel);

			if (lexer.CurrentToken.Type == TokenType.question)
			{
				lexer.Accept();
				AstExpression ifTrue = ParseLogicalExpression(currLevel);
				lexer.Accept(TokenType.colon);
				AstExpression ifFalse = ParseLogicalExpression(currLevel);
				result = new TernaryAstExpression(result, ifTrue, ifFalse);
			}

			return result;
		}

		private AstExpression ParseOrExpression(int[] currLevel)
		{
			AstExpression result = ParseAndExpression(currLevel);
			while (lexer.CurrentToken.Type == TokenType.logicalOr)
			{
				lexer.Accept();
				AstExpression right = ParseAndExpression(currLevel);
				result = new OpOr(result, right);
			}
			return result;
		}

		private AstExpression ParseAndExpression(int[] currLevel)
		{
			AstExpression result = ParseExpression(currLevel);
			while (lexer.CurrentToken.Type == TokenType.logicalAnd)
			{
				lexer.Accept();
				AstExpression right = ParseExpression(currLevel);
				result = new OpAnd(result, right);
			}
			return result;
		}

		private AstExpression ParseRelationalExpression(int[] currLevel)
		{
			AstExpression result = ParseAdditiveExpression(currLevel);

			bool nextRelationalOperator = IsRelationalOperator();
			if (nextRelationalOperator)
			{
				TokenType type = lexer.CurrentToken.Type;
				lexer.Accept();
				AstExpression secondObject = ParseAdditiveExpression(currLevel);

				switch (type)
				{
					case TokenType.equality:
						result = new OpEqual(result, secondObject);
						break;

					case TokenType.inequality:
						result = new OpNotEqual(result, secondObject);
						break;

					case TokenType.lessThan:
						result = new OpLessThan(result, secondObject);
						break;

					case TokenType.greaterThan:
						result = new OpGreaterThan(result, secondObject);
						break;

					case TokenType.lessOrEqual:
						result = new OpLessOrEqual(result, secondObject);
						break;

					case TokenType.greaterOrEqual:
						result = new OpGreaterOrEqual(result, secondObject);
						break;
					default:
						break;
				}
			}
			return result;
		}

		private AstExpression ParseMultiplicativeExpression(int[] currLevel)
		{
			AstExpression result = ParseAtomicExpression(currLevel);
			while (lexer.CurrentToken.Type == TokenType.multiplication || lexer.CurrentToken.Type == TokenType.division)
			{
				TokenType type = lexer.CurrentToken.Type;
				lexer.Accept();
				AstExpression secondObject = ParseAtomicExpression(currLevel);
				switch (type)
				{
					case TokenType.multiplication:
						result = new OpMultiply(result, secondObject);
						break;
					case TokenType.division:
						result = new OpDivide(result, secondObject);
						break;
				}
			}
			return result;
		}

		private AstExpression ParseAdditiveExpression(int[] currLevel)
		{
			AstExpression result = ParseMultiplicativeExpression(currLevel);
			while (lexer.CurrentToken.Type == TokenType.plus || lexer.CurrentToken.Type == TokenType.minus)
			{
				TokenType type = lexer.CurrentToken.Type;
				lexer.Accept();
				AstExpression secondObject = ParseMultiplicativeExpression(currLevel);
				switch (type)
				{
					case TokenType.plus:
						result = new OpAdd(result, secondObject);
						break;
					case TokenType.minus:
						result = new OpSubtract(result, secondObject);
						break;
				}
			}
			return result;
		}

		private bool IsLiteral()
		{
			bool result = false;
			TokenType literalType = lexer.CurrentToken.Type;
			switch (literalType)
			{
				case TokenType.number:
				case TokenType.stringLit:
				case TokenType.@decimal:
				case TokenType.@double:
				case TokenType.nullToken:
				case TokenType.date:
				case TokenType.boolFalse:
				case TokenType.boolTrue:
					result = true;
					break;
			}
			return result;
		}

		private bool IsAtomicExpression()
        {
            bool result = false;
            TokenType type = lexer.CurrentToken.Type;
            switch (type)
            {
                case TokenType.id:
                case TokenType.lParen:
                case TokenType.begin:
                case TokenType.minus:
                case TokenType.plus:
                case TokenType.logicalNot:
                case TokenType.number:
                case TokenType.stringLit:
                case TokenType.@decimal:
                case TokenType.@double:
				case TokenType.nullToken:
                case TokenType.date:
                case TokenType.boolFalse:
                case TokenType.boolTrue:
                    result = true;
                    break;
            }
            return result;
        }

        private AstExpression ParseAtomicExpression(int[] currLevel)
		{
            AstExpression result;
            TokenType type = lexer.CurrentToken.Type;
            switch (type)
            {
                case TokenType.id:
                    result = ParseDotChain(currLevel);
                    break;
                case TokenType.lParen:
                    lexer.Accept();
                    {
                        if (lexer.CurrentToken.Type == TokenType.id && lexer.CurrentLexeme().Equals("list".AsSpan(), StringComparison.OrdinalIgnoreCase))
                        {
                            result = ParseId(currLevel);
                        }
                        else
                        {
                            result = ParseLogicalExpression(currLevel);
                        }
                        if (result is Id typeToCast)
                        {
							if (String.Equals(typeToCast.Name, "list", StringComparison.OrdinalIgnoreCase))
							{
								lexer.Accept(TokenType.lessThan);
								Id listType = (Id)ParseId(currLevel);
								lexer.Accept(TokenType.greaterThan);
								lexer.Accept(TokenType.rParen);
								var rightExpression = ParseLogicalExpression(currLevel);
								result = new OpCast(this.libraries, typeToCast, rightExpression, listType);
							}
							else
							{
								lexer.Accept(TokenType.rParen);
								if (IsAtomicExpression())
								{
									var rightExpression = ParseLogicalExpression(currLevel);
									result = new OpCast(this.libraries, typeToCast, rightExpression);
								}
								else
								{
									result = new Parenthesis(result);
								}
							}
                        }
                        else
                        {
							result = new Parenthesis(result);
							lexer.Accept(TokenType.rParen);
						}
                    }
                    break;
                case TokenType.begin:
                    result = ParseList(currLevel);
                    break;
                case TokenType.logicalNot:
                    lexer.Accept();
                    result = ParseExpression(currLevel);
                    result = new OpNot(result);
                    break;
                case TokenType.minus:
                    lexer.Accept();
					if (lexer.CurrentToken.Type == TokenType.id)
						result = ParseAtomicExpression(currLevel);
					else if (IsLiteral())
						result = ParseLiteral();
					else
						result = ParseMultiplicativeExpression(currLevel);
					result = new OpNegate(result);
					break;
                case TokenType.plus:
                    lexer.Accept();
					if (lexer.CurrentToken.Type == TokenType.id)
						result = ParseAtomicExpression(currLevel);
					else if (IsLiteral())
						result = ParseLiteral();
					else
						result = ParseMultiplicativeExpression(currLevel);
					break;
				default:
                    result = ParseLiteral();
                    break;
            }
            return result;
        }

		private AstExpression ParseLiteral()
		{
			AstExpression result;
			TokenType literalType = lexer.CurrentToken.Type;
			switch (literalType)
			{
                case TokenType.number:
					result = ParseNumber();
					break;

                case TokenType.stringLit:
					result = ParseString();
					break;

                case TokenType.@decimal:
					result = ParseDecimal();
					break;

				case TokenType.@double:
					result = ParseDouble();
					break;

				case TokenType.nullToken:
					result = ParseNull();
					break;

                case TokenType.date:
					result = ParseDate();
					break;

                case TokenType.boolFalse:
                case TokenType.boolTrue:
					result = ParseBoolean();
					break;

				default:
					var problematicLexeme = lexer.CurrentLexeme();
					throw new LanguageException($"Expected a literal value, but found '{problematicLexeme}' at line {Row()}, column {Column()}.", problematicLexeme.ToString(), Row(), Column());
			}
			return result;
		}

		private AstExpression ParseBoolean()
		{
			AstExpression result = null;
			switch (lexer.CurrentToken.Type)
			{
				case TokenType.boolTrue:
					result = LiteralBoolean.LiteralTrue;
					break;
				case TokenType.boolFalse:
					result = LiteralBoolean.LiteralFalse;
					break;
			}
            lexer.Accept();
			return result;
		}

		private AstExpression ParseString()
		{
			ReadOnlySpan<char> raw = lexer.CurrentLexeme();

			if (raw.Length >= 2 && (raw[0] == '\''))
				raw = raw.Slice(1, raw.Length - 2);

			var sb = new System.Text.StringBuilder(raw.Length);
			for (int i = 0; i < raw.Length; i++)
			{
				if (raw[i] == '\\' && i + 1 < raw.Length)
				{
					char next = raw[i + 1];
					switch (next)
					{
						case '\\': sb.Append('\\'); i++; break;
						case '\'': sb.Append('\''); i++; break;
						default:
							sb.Append('\\');
							sb.Append(next);
							i++;
							break;
					}
				}
				else
				{
					sb.Append(raw[i]);
				}
			}
			string literal = sb.ToString();
			lexer.Accept();
			return literal == "" ? LiteralString.EMPTY : new LiteralString(literal);
		}

		private AstExpression ParseDouble()
		{
			double value = double.Parse(lexer.CurrentLexeme(), customFormat);
			AstExpression doubleLiteral = new LiteralDouble(value);
			lexer.Accept();
			return doubleLiteral;
		}

		private AstExpression ParseDecimal()
		{
			decimal value = decimal.Parse(lexer.CurrentLexeme(), customFormat);
			AstExpression decimalLiteral = new LiteralDecimal(value);
			lexer.Accept();
			return decimalLiteral;
		}

		private AstExpression ParseNull()
		{
			AstExpression result = new LiteralNull();
			lexer.Accept();
			return result;
		}

		private AstExpression ParseNumber()
		{
			AstExpression result = new LiteralNumber(int.Parse(lexer.CurrentLexeme()));
			lexer.Accept();
			return result;
		}

        internal string CurrentStatementText()
		{
            if (lastValidStatement == null)
                return "Start of file: no previous statement.";
            return lastValidStatement.ToString();
		}

        internal int Row()
		{
			return lexer.Row();
		}

        internal int Column()
		{
			return lexer.Column();
		}
	}
}
