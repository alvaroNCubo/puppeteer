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

        private static readonly Dictionary<string, Type> tiposPrimitivos = new Dictionary<string, Type>();
        private static NumberFormatInfo customFormat;
        private string source;

        // Upgrade names seen in the current Program during parsing.
        // Reset at the start of ParseProgram. Detects static duplicates in the same script.
        private readonly HashSet<string> upgradeNamesEnPrograma = new HashSet<string>(StringComparer.Ordinal);

        // Set to true if the parse of the current Program creates an OpEval or EvalStatement.
        // Reset at the start of ParseProgram; carried into Program.HasEval so that
        // ValidateStatically does not have to walk the AST looking for evals.
        private bool hasEvalEnPrograma;

		static Parser()
		{
			tiposPrimitivos["int"] = typeof(int);
			tiposPrimitivos["string"] = typeof(string);
			tiposPrimitivos["bool"] = typeof(bool);
			tiposPrimitivos["double"] = typeof(double);
			tiposPrimitivos["datetime"] = typeof(DateTime);
			tiposPrimitivos["decimal"] = typeof(decimal);
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
			Program result = ParseProgram(Array.Empty<int>(), isQuery, isCheck);
			return result;
		}

		internal Program Rehydrate()
		{
			bool rehydrateDontNeedOutput = true;
			bool rehydrateAlwaysIsCommand = false;
			Program result = ParseProgram(Array.Empty<int>(), isQuery: rehydrateAlwaysIsCommand, isCheck: rehydrateDontNeedOutput);
			return result;
		}

		internal Program ParseEval(int[] currLevel, bool isQuery, bool isCheck)
		{
			bool anteriorEsEval = symbolTable.InEvalMode;
			symbolTable.InEvalMode = true;
			Program result = ParseProgram(currLevel, isQuery, isCheck);
			symbolTable.InEvalMode = anteriorEsEval;
			return result;
		}

		internal void SetSource(string source)
		{
			this.lexer.Source = source;
			this.source = source;
		}

		private Program ParseProgram(int[] currLevel, bool isQuery, bool isCheck)
		{
            upgradeNamesEnPrograma.Clear();
            hasEvalEnPrograma = false;
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
			Program programaResultante = new Program(libraries, this.source, symbolTable, statements, currLevel, isQuery, isCheck);
			programaResultante.HasEval = hasEvalEnPrograma;
			// Lever 1 of the Now optimization: precompute (once per parse, outside the
			// hot path) whether the program references the SYSTEM Now parameter. Conservative with
			// HasEval: an Eval may synthesize the reference and it is not visible to the static scan.
			programaResultante.ReferencesNow = hasEvalEnPrograma || programaResultante.ScriptReferencesSystemNow();
			return programaResultante;
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
                case TokenType.FOR:
                    result = ParseForStatement(currLevel, ref blockNumber, isQuery, isQuery);
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
			Statement resultado;
			lexer.Accept();
			lexer.Accept(TokenType.lParen);
			AstExpression exp = ParseLogicalExpression(currLevel);
			lexer.Accept(TokenType.rParen);

            int newBlockNumber = 0;
			Statement comandosDelIF = ParseStatement(IncLevel(currLevel, ++blockNumber), isQuery, isCheck, ref newBlockNumber);

			if (lexer.CurrentToken.Type == TokenType.ELSE)
			{
				lexer.Accept();
				Statement elseBranchStatement = ParseStatement(IncLevel(currLevel, ++blockNumber), isQuery, isCheck, ref newBlockNumber);
				resultado = new IfStatement(symbolTable, exp, comandosDelIF, elseBranchStatement);
			}
			else
			{
				resultado = new IfStatement(symbolTable, exp, comandosDelIF);
			}
			return resultado;
		}

		private Statement ParseCheckStatement(int[] currLevel, bool isQuery, bool isCheck)
		{
			Statement resultado;
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
				resultado = new CheckStatement(exp, new Error(reason));
			}
			else if (value.Equals("INFORMATION".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				resultado = new CheckStatement(exp, new Information(reason));
			}
			else if (value.Equals("WARNING".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				resultado = new CheckStatement(exp, new Warning(reason));
			}
			else
			{
				throw new LanguageException($"Expected 'error', 'warning' or 'information' after 'check(...)' at line {Row()}, column {Column()}, but found '{value}'.", value.ToString(), Row(), Column());
			}
			lexer.Accept(TokenType.semicolon);

			return resultado;
		}

		private Statement ParseNotifyStatement(int[] currLevel, bool isQuery, bool isCheck)
		{
			Statement resultado;
			AstExpression reason;

			lexer.Accept(TokenType.notify);

			TokenType tokenType = lexer.CurrentToken.Type;

			if (tokenType != TokenType.id) throw new LanguageException($"Expected 'error', 'warning' or 'information' after 'notify' at line {Row()}, column {Column()}, but found token type '{tokenType}'.", lexer.CurrentLexeme().ToString(), Row(), Column());

			ReadOnlySpan<char> value = lexer.CurrentLexeme();
			if (value.Equals("ERROR".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				resultado = new CheckStatement(LiteralBoolean.LiteralFalse, new Error(reason));
			}
			else if (value.Equals("INFORMATION".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				resultado = new CheckStatement(LiteralBoolean.LiteralFalse, new Information(reason));
			}
			else if (value.Equals("WARNING".AsSpan(), StringComparison.OrdinalIgnoreCase))
			{
				lexer.Accept();
				reason = ParseExpression(currLevel);
				resultado = new CheckStatement(LiteralBoolean.LiteralFalse, new Warning(reason));
			}
			else
			{
				throw new LanguageException($"Expected 'error', 'warning' or 'information' after 'notify' at line {Row()}, column {Column()}, but found '{value}'.", value.ToString(), Row(), Column());
			}

			lexer.Accept(TokenType.semicolon);
			
			return resultado;
		}

		private Statement ParseForStatement(int[] currLevel, ref int blockNumber, bool isQuery, bool isCheck)
        {
            currLevel = IncLevel(currLevel, ++blockNumber);
            Statement resultado;
            lexer.Accept(TokenType.FOR);
            lexer.Accept(TokenType.lParen);
            Id id = (Id) ParseId(currLevel);

            Id idIndice = null;
            Id idElemento = null;
            bool soloIndice = false;

            if (lexer.CurrentToken.Type == TokenType.comma)
            {
                lexer.Accept(TokenType.comma);
                idIndice = id;
                if (lexer.CurrentToken.Type == TokenType.wildcard)
                {
                    lexer.Accept(TokenType.wildcard);
                    soloIndice = true;
                    idElemento = null;
                }
                else
                {
                    idElemento = (Id) ParseId(currLevel);
                }
            }

            // Accept both ':' and 'in' (syntactic sugar)
            if (lexer.CurrentToken.Type == TokenType.colon)
            {
                lexer.Accept(TokenType.colon);
            }
            else if (lexer.CurrentToken.Type == TokenType.IN)
            {
                lexer.Accept(TokenType.IN);
            }
            else
            {
                throw new LanguageException($"Expected ':' or 'in' in 'for' loop at line {Row()}, column {Column()}, but found '{lexer.CurrentToken.Type}'.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            AstExpression exp = ParseExpression(currLevel);
            lexer.Accept(TokenType.rParen);
            int newBlockNumber = 0;
            Statement forBodyStatement = ParseStatement(IncLevel(currLevel, ++blockNumber), isQuery, isCheck, ref newBlockNumber);

            if (idIndice != null)
            {
                resultado = new ForStatement(symbolTable, idIndice, idElemento, soloIndice, exp, forBodyStatement);
            }
            else
            {
                resultado = new ForStatement(symbolTable, id, exp, forBodyStatement);
            }
            return resultado;
        }

        private Statement ParseUpgradeStatement(int[] currLevel, bool isQuery, bool isCheck)
        {
            if (isQuery)
            {
                throw new LanguageException("'upgrade' is not valid in PerformQuery. It can only be used in PerformCmd because it persists actor state.");
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

            if (!upgradeNamesEnPrograma.Add(upgradeName))
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
        // significant because callsite arguments are positionally bound). Modifiers
        // (In/Out/InOut/Eval) are out of scope for Phase 1.
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
                    throw new LanguageException($"'define action' parameter expects an identifier (e.g. id:int or @id:int) but found token type '{lexer.CurrentToken.Type}' with lexeme '{lexer.CurrentLexeme()}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
                }
                sb.Append(lexer.CurrentLexeme());
                lexer.Accept(TokenType.id);

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
        // Tell statement — Plan 2 of the Tell primitive roadmap.
        // Grammar:
        //   tellStatement := TELL ( ackForm | targetForm ) SEMICOLON
        //   ackForm       := "ack" stringLit "from" id LPAREN exprList RPAREN
        //   targetForm    := id [LPAREN exprList RPAREN] action trailers
        //   action        := commandCall | sagaVerb commandCall
        //   sagaVerb      := "start" | "step" | "compensate" | "close"
        //   commandCall   := id LPAREN exprList RPAREN
        //   trailers      := (idTrailer | throughTrailer)*
        //   idTrailer     := "id" stringLit
        //   throughTrailer:= "through" stringLit
        //
        // Saga verbs require a target with id in parens (`tell <SagaActor>(<sagaId>)`).
        // 'ack', 'from', 'id', 'through' and the saga verbs are contextual keywords
        // (TokenType.id with matching lexeme), the same pattern as 'where' and 'list'.
        // ============================================================
        private Statement ParseTellStatement(int[] currLevel, bool isQuery, bool isCheck)
        {
            if (isQuery)
            {
                throw new LanguageException($"'tell' is not valid in PerformQuery. It can only be used in PerformCmd because it produces cross-actor causation that the journal must record (at line {Row()}, column {Column()}).");
            }

            lexer.Accept(TokenType.tell);

            // Form: tell ack 'tell-id' from Target(id)
            if (lexer.CurrentToken.Type == TokenType.id && LexemeEqualsIgnoreCase("ack"))
            {
                Statement ackResult = ParseTellAckBody();
                lexer.Accept(TokenType.semicolon);
                return ackResult;
            }

            // Form: tell <Target>[(<id>)] [sagaVerb] <Command>(<args>) [trailers]
            if (lexer.CurrentToken.Type != TokenType.id)
            {
                throw new LanguageException($"'tell' must be followed by an actor target identifier or 'ack', but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            string targetClass = lexer.CurrentLexeme().ToString();
            int targetClassRow = Row();
            int targetClassColumn = Column();
            lexer.Accept(TokenType.id);

            ValidateTellTargetClass(targetClass, targetClassRow, targetClassColumn);

            AstExpression targetId = null;
            if (lexer.CurrentToken.Type == TokenType.lParen)
            {
                lexer.Accept(TokenType.lParen);
                AstExpression[] targetIdArgs = ParseArguments(currLevel);
                if (targetIdArgs.Length != 1)
                {
                    throw new LanguageException($"'tell' target id must be a single expression in parens, but got {targetIdArgs.Length} expressions at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
                }
                targetId = targetIdArgs[0];
                lexer.Accept(TokenType.rParen);
            }

            // Detect optional saga verb. Saga always requires a target id.
            SagaVerb? sagaVerb = TryReadSagaVerb();

            if (sagaVerb.HasValue && targetId == null)
            {
                throw new LanguageException($"saga '{SagaVerbLexeme(sagaVerb.Value)}' requires a target id in parens — for example: tell {targetClass}(<sagaId>) {SagaVerbLexeme(sagaVerb.Value)} <Command>(...) (at line {Row()}, column {Column()}).", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            if (sagaVerb.HasValue)
            {
                lexer.Accept(TokenType.id); // consume the saga verb keyword
            }

            if (lexer.CurrentToken.Type != TokenType.id)
            {
                throw new LanguageException($"'tell' expects a command name after the target, but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            string commandName = lexer.CurrentLexeme().ToString();
            lexer.Accept(TokenType.id);

            lexer.Accept(TokenType.lParen);
            AstExpression[] commandArgs = ParseArguments(currLevel);
            lexer.Accept(TokenType.rParen);

            (string idLiteral, string throughLiteral) = ParseTellTrailers();

            lexer.Accept(TokenType.semicolon);

            if (sagaVerb.HasValue)
            {
                return new TellSagaStatement(symbolTable, targetClass, targetId, sagaVerb.Value, commandName, commandArgs, idLiteral, throughLiteral);
            }

            return new BasicTellStatement(symbolTable, targetClass, targetId, commandName, commandArgs, idLiteral, throughLiteral);
        }

        private Statement ParseTellAckBody()
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
                throw new LanguageException($"'tell ack ... from' must be followed by an actor target identifier, but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }

            string fromTargetClass = lexer.CurrentLexeme().ToString();
            int fromTargetRow = Row();
            int fromTargetColumn = Column();
            lexer.Accept(TokenType.id);

            ValidateTellTargetClass(fromTargetClass, fromTargetRow, fromTargetColumn);

            lexer.Accept(TokenType.lParen);
            AstExpression[] fromIdArgs = ParseArguments(new int[0]);
            if (fromIdArgs.Length != 1)
            {
                throw new LanguageException($"'tell ack' target id must be a single expression in parens, but got {fromIdArgs.Length} expressions at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            lexer.Accept(TokenType.rParen);

            return new TellAckStatement(symbolTable, ackId, fromTargetClass, fromIdArgs[0]);
        }

        private SagaVerb? TryReadSagaVerb()
        {
            if (lexer.CurrentToken.Type != TokenType.id) return null;
            if (LexemeEqualsIgnoreCase("start")) return SagaVerb.Start;
            if (LexemeEqualsIgnoreCase("step")) return SagaVerb.Step;
            if (LexemeEqualsIgnoreCase("compensate")) return SagaVerb.Compensate;
            if (LexemeEqualsIgnoreCase("close")) return SagaVerb.Close;
            return null;
        }

        private static string SagaVerbLexeme(SagaVerb verb)
        {
            return TellSagaStatement.VerbToken(verb);
        }

        private (string idLiteral, string throughLiteral) ParseTellTrailers()
        {
            string idLiteral = null;
            string throughLiteral = null;

            while (lexer.CurrentToken.Type == TokenType.id)
            {
                if (LexemeEqualsIgnoreCase("id"))
                {
                    if (idLiteral != null)
                    {
                        throw new LanguageException($"'tell' cannot have more than one 'id' trailer (at line {Row()}, column {Column()}).", lexer.CurrentLexeme().ToString(), Row(), Column());
                    }
                    lexer.Accept(TokenType.id); // consume 'id'
                    if (lexer.CurrentToken.Type != TokenType.stringLit)
                    {
                        throw new LanguageException($"'tell ... id' requires a string literal, but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
                    }
                    idLiteral = lexer.CurrentLexeme().ToString();
                    lexer.Accept(TokenType.stringLit);
                }
                else if (LexemeEqualsIgnoreCase("through"))
                {
                    if (throughLiteral != null)
                    {
                        throw new LanguageException($"'tell' cannot have more than one 'through' trailer (at line {Row()}, column {Column()}).", lexer.CurrentLexeme().ToString(), Row(), Column());
                    }
                    lexer.Accept(TokenType.id); // consume 'through'
                    if (lexer.CurrentToken.Type != TokenType.stringLit)
                    {
                        throw new LanguageException($"'tell ... through' requires a string literal, but found token type '{lexer.CurrentToken.Type}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
                    }
                    throughLiteral = lexer.CurrentLexeme().ToString();
                    lexer.Accept(TokenType.stringLit);
                }
                else
                {
                    break;
                }
            }

            return (idLiteral, throughLiteral);
        }

        private bool LexemeEqualsIgnoreCase(string keyword)
        {
            return lexer.CurrentLexeme().Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        // Plan 3: parse-time validation that the actor target class exists in the loaded
        // domain libraries. Applies to BasicTellStatement.TargetClass, TellSagaStatement.SagaActor
        // and TellAckStatement.FromTargetClass. Command signature validation is intentionally
        // out of scope — Plan 5 resolves arg types when wiring the transport, and validating
        // signatures here would require resolving expression types in parse-time without
        // execution context. Coherent with feedback_dsl_pattern_priorities (no speculation).
        private void ValidateTellTargetClass(string targetClass, int row, int column)
        {
            if (!libraries.Knows(targetClass))
            {
                throw new LanguageException($"'tell' target class '{targetClass}' is not known to the loaded domain libraries (at line {row}, column {column}). Either declare the class in a domain library, or pass the assembly explicitly when constructing the actor.", targetClass, row, column);
            }
        }

        private Statement ParseLineCommentStatement()
		{
			var comentario = lexer.CurrentLexeme();
			lexer.Accept(TokenType.lineComment);
			return new NullStatement(comentario.ToString());
		}

		private Statement ParseCreateOrCallStatement(int[] currLevel, bool isQuery, bool isCheck)
		{
			if (isQuery && currLevel.Length == 0)
			{
				throw new LanguageException($"Global variable declarations are not allowed in queries (at line {Row()}, column {Column()}).");
			}
			Statement resultado;
			AstExpression dot = ParseDotChain(currLevel);
			bool esUnComandoCreate = lexer.CurrentToken.Type == TokenType.assign;
			if (esUnComandoCreate)
			{
				resultado = ParseCreateStatement(dot, currLevel, isQuery, isCheck);
			}
			else
			{
				resultado = ParseCallStatement(dot, currLevel);
			}
			lexer.Accept(TokenType.semicolon);
			return resultado;
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
            hasEvalEnPrograma = true;
            return new EvalStatement(this.libraries, symbolTable, exp, currLevel, isQuery, isCheck);
        }

        private AstExpression ParseDotChain(int[] currLevel)
		{
			AstExpression resultado = ParseId(currLevel);
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
							if (resultado is Id id)
								resultado = new DottedId(libraries, symbolTable, id, method);
							else if (resultado is DotAccess dot)
								resultado = new ChainedDotAccess(dot, method);
							else if (resultado is NewInstance instance)
								resultado = new ChainedDotAccess(instance, method);
							else
								throw new LanguageException($"Cannot apply the dot operator ('.') to type '{resultado.GetType().Name}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
						}
						else
						{
							lexer.Accept(TokenType.lParen);
							var args = ParseArguments(currLevel);
							lexer.Accept(TokenType.rParen);

							if (resultado is Id id)
							{
								// Optional 'in' clause to disambiguate the namespace homonymy of a
								// static method call 'Clase.Metodo(args) in Namespace.Sub'. Same parser
								// as the Clase(args) in Namespace construction. Only allowed when the
								// receiver is an Id (a potential class); DottedId validates that it
								// actually resolves to a class and not to a variable.
								string staticNamespace = ParseOptionalInNamespace();
								resultado = new DottedId(libraries, symbolTable, id, method, args, staticNamespace);
							}
							else if (resultado is DotAccess dot)
								resultado = new ChainedDotAccess(dot, method, args);
							else if (resultado is NewInstance instance)
								resultado = new ChainedDotAccess(instance, method, args);
							else
								throw new LanguageException($"Cannot apply the dot operator ('.') to type '{resultado.GetType().Name}' at line {Row()}, column {Column()}.", lexer.CurrentLexeme().ToString(), Row(), Column());
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
					resultado = new SubscriptAstExpression(resultado, subscriptIndices.ToArray());
					break;
				case TokenType.lParen:
					lexer.Accept();
					var clazz = (Id) resultado;
					var arguments = ParseArguments(currLevel);
					lexer.Accept(TokenType.rParen);

					string nombreNamespace = ParseOptionalInNamespace();

					resultado = new NewInstance(libraries, symbolTable, clazz, arguments, nombreNamespace);
					break;
					default:
						return resultado;
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
			bool salir = false;
			bool siguienteCierraParentesis = lexer.CurrentToken.Type == TokenType.rParen;
			if (siguienteCierraParentesis)
			{
				salir = true;
			}

			List<AstExpression> arguments = new List<AstExpression>();
			while (!salir)
			{
				AstExpression argument = ParseLogicalExpression(currLevel);
				arguments.Add(argument);
				bool siguienteEsUnaComa = lexer.CurrentToken.Type == TokenType.comma;
				siguienteCierraParentesis = lexer.CurrentToken.Type == TokenType.rParen;
				if (siguienteCierraParentesis)
				{
					salir = true;
				}
				else if (siguienteEsUnaComa)
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
            bool salir = false;
            bool siguienteCierraLista = lexer.CurrentToken.Type == TokenType.end;
            if (siguienteCierraLista)
            {
                salir = true;
            }

            List<AstExpression> elementos = new List<AstExpression>();
            while (!salir)
            {
                AstExpression argument = ParseLogicalExpression(currLevel);
                elementos.Add(argument);
                bool siguienteEsUnaComa = lexer.CurrentToken.Type == TokenType.comma;
                siguienteCierraLista = lexer.CurrentToken.Type == TokenType.end;
                if (siguienteCierraLista)
                {
                    salir = true;
                }
                else if (siguienteEsUnaComa)
                {
                    lexer.Accept();
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
			AstExpression resultado = ParseRelationalExpression(currLevel);
			return resultado;
		}

        private AstExpression ParseDate()
		{
            DateTime date = ParseDateValidation(lexer);
			AstExpression resultado;
			if (lexer.CurrentToken.Type == TokenType.time)
			{
				resultado = ParseDateTime(ref date);
			}
            else
            {
				resultado = new LiteralDateTime(date);
			}
			return resultado;
		}

        private DateTime ParseDateValidation(Lexer lexer)
        {
            if (lexer.CurrentToken.Type != TokenType.date)
            {
                throw new LanguageException($"Expected a date literal at line {Row()}, column {Column()}, but found token '{lexer.CurrentToken.Type}'. Please verify the date format (MM/dd/yyyy).", lexer.CurrentLexeme().ToString(), Row(), Column());
            }
            DateTime resultado = DateTime.Parse(lexer.CurrentLexeme(), CultureInfo.InvariantCulture);
			lexer.Accept(TokenType.date);
            return resultado;
        }

        private AstExpression ParseDateTime(ref DateTime date)
		{
			DateTime dateTime = ParseDateTimeValidation(ref date, lexer);
			AstExpression resultado = new LiteralDateTime(dateTime);
			return resultado;
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

			DateTime resultado = DateTime.Parse(buffer.Slice(0, 11 + timeSpan.Length), CultureInfo.InvariantCulture);
			lexer.Accept(TokenType.time);
            return resultado;
        }


        private bool IsRelationalOperator()
		{
			TokenType type = lexer.CurrentToken.Type;

			return type == TokenType.equality || type == TokenType.inequality || type == TokenType.lessOrEqual || type == TokenType.greaterOrEqual || type == TokenType.lessThan || type == TokenType.greaterThan;
		}

		private AstExpression ParseLogicalExpression(int[] currLevel)
		{
			AstExpression resultado = ParseOrExpression(currLevel);

			if (lexer.CurrentToken.Type == TokenType.question)
			{
				lexer.Accept();
				AstExpression siVerdadero = ParseLogicalExpression(currLevel);
				lexer.Accept(TokenType.colon);
				AstExpression siFalso = ParseLogicalExpression(currLevel);
				resultado = new TernaryAstExpression(resultado, siVerdadero, siFalso);
			}

			return resultado;
		}

		private AstExpression ParseOrExpression(int[] currLevel)
		{
			AstExpression resultado = ParseAndExpression(currLevel);
			while (lexer.CurrentToken.Type == TokenType.logicalOr)
			{
				lexer.Accept();
				AstExpression derecho = ParseAndExpression(currLevel);
				resultado = new OpOr(resultado, derecho);
			}
			return resultado;
		}

		private AstExpression ParseAndExpression(int[] currLevel)
		{
			AstExpression resultado = ParseExpression(currLevel);
			while (lexer.CurrentToken.Type == TokenType.logicalAnd)
			{
				lexer.Accept();
				AstExpression derecho = ParseExpression(currLevel);
				resultado = new OpAnd(resultado, derecho);
			}
			return resultado;
		}

		private AstExpression ParseRelationalExpression(int[] currLevel)
		{
			AstExpression resultado = ParseAdditiveExpression(currLevel);

			bool siguienteOperadorRelacional = IsRelationalOperator();
			if (siguienteOperadorRelacional)
			{
				TokenType type = lexer.CurrentToken.Type;
				lexer.Accept();
				AstExpression segundoObjeto = ParseAdditiveExpression(currLevel);

				switch (type)
				{
					case TokenType.equality:
						resultado = new OpEqual(resultado, segundoObjeto);
						break;

					case TokenType.inequality:
						resultado = new OpNotEqual(resultado, segundoObjeto);
						break;

					case TokenType.lessThan:
						resultado = new OpLessThan(resultado, segundoObjeto);
						break;

					case TokenType.greaterThan:
						resultado = new OpGreaterThan(resultado, segundoObjeto);
						break;

					case TokenType.lessOrEqual:
						resultado = new OpLessOrEqual(resultado, segundoObjeto);
						break;

					case TokenType.greaterOrEqual:
						resultado = new OpGreaterOrEqual(resultado, segundoObjeto);
						break;
					default:
						break;
				}
			}
			return resultado;
		}

		private AstExpression ParseMultiplicativeExpression(int[] currLevel)
		{
			AstExpression resultado = ParseAtomicExpression(currLevel);
			while (lexer.CurrentToken.Type == TokenType.multiplication || lexer.CurrentToken.Type == TokenType.division)
			{
				TokenType type = lexer.CurrentToken.Type;
				lexer.Accept();
				AstExpression segundoObjeto = ParseAtomicExpression(currLevel);
				switch (type)
				{
					case TokenType.multiplication:
						resultado = new OpMultiply(resultado, segundoObjeto);
						break;
					case TokenType.division:
						resultado = new OpDivide(resultado, segundoObjeto);
						break;
				}
			}
			return resultado;
		}

		private AstExpression ParseAdditiveExpression(int[] currLevel)
		{
			AstExpression resultado = ParseMultiplicativeExpression(currLevel);
			while (lexer.CurrentToken.Type == TokenType.plus || lexer.CurrentToken.Type == TokenType.minus)
			{
				TokenType type = lexer.CurrentToken.Type;
				lexer.Accept();
				AstExpression segundoObjeto = ParseMultiplicativeExpression(currLevel);
				switch (type)
				{
					case TokenType.plus:
						resultado = new OpAdd(resultado, segundoObjeto);
						break;
					case TokenType.minus:
						resultado = new OpSubtract(resultado, segundoObjeto);
						break;
				}
			}
			return resultado;
		}

		private bool IsLiteral()
		{
			bool resultado = false;
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
					resultado = true;
					break;
			}
			return resultado;
		}

		private bool IsAtomicExpression()
        {
            bool resultado = false;
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
                    resultado = true;
                    break;
            }
            return resultado;
        }

        private AstExpression ParseAtomicExpression(int[] currLevel)
		{
            AstExpression resultado;
            TokenType type = lexer.CurrentToken.Type;
            switch (type)
            {
                case TokenType.id:
                    resultado = ParseDotChain(currLevel);
                    break;
                case TokenType.lParen:
                    lexer.Accept();
                    {
                        if (lexer.CurrentToken.Type == TokenType.id && lexer.CurrentLexeme().Equals("list".AsSpan(), StringComparison.OrdinalIgnoreCase))
                        {
                            resultado = ParseId(currLevel);
                        }
                        else
                        {
                            resultado = ParseLogicalExpression(currLevel);
                        }
                        if (resultado is Id typeToCast)
                        {
							if (String.Equals(typeToCast.Name, "list", StringComparison.OrdinalIgnoreCase))
							{
								lexer.Accept(TokenType.lessThan);
								Id listType = (Id)ParseId(currLevel);
								lexer.Accept(TokenType.greaterThan);
								lexer.Accept(TokenType.rParen);
								var expresionDerecha = ParseLogicalExpression(currLevel);
								resultado = new OpCast(this.libraries, typeToCast, expresionDerecha, listType);
							}
							else
							{
								lexer.Accept(TokenType.rParen);
								if (IsAtomicExpression())
								{
									var expresionDerecha = ParseLogicalExpression(currLevel);
									resultado = new OpCast(this.libraries, typeToCast, expresionDerecha);
								}
								else
								{
									resultado = new Parenthesis(resultado);
								}
							}
                        }
                        else
                        {
							resultado = new Parenthesis(resultado);
							lexer.Accept(TokenType.rParen);
						}
                    }
                    break;
                case TokenType.begin:
                    resultado = ParseList(currLevel);
                    break;
                case TokenType.logicalNot:
                    lexer.Accept();
                    resultado = ParseExpression(currLevel);
                    resultado = new OpNot(resultado);
                    break;
                case TokenType.minus:
                    lexer.Accept();
					if (lexer.CurrentToken.Type == TokenType.id)
						resultado = ParseAtomicExpression(currLevel);
					else if (IsLiteral())
						resultado = ParseLiteral();
					else
						resultado = ParseMultiplicativeExpression(currLevel);
					resultado = new OpNegate(resultado);
					break;
                case TokenType.plus:
                    lexer.Accept();
					if (lexer.CurrentToken.Type == TokenType.id)
						resultado = ParseAtomicExpression(currLevel);
					else if (IsLiteral())
						resultado = ParseLiteral();
					else
						resultado = ParseMultiplicativeExpression(currLevel);
					break;
				default:
                    resultado = ParseLiteral();
                    break;
            }
            return resultado;
        }

		private AstExpression ParseLiteral()
		{
			AstExpression resultado;
			TokenType literalType = lexer.CurrentToken.Type;
			switch (literalType)
			{
                case TokenType.number:
					resultado = ParseNumber();
					break;

                case TokenType.stringLit:
					resultado = ParseString();
					break;

                case TokenType.@decimal:
					resultado = ParseDecimal();
					break;

				case TokenType.@double:
					resultado = ParseDouble();
					break;

				case TokenType.nullToken:
					resultado = ParseNull();
					break;

                case TokenType.date:
					resultado = ParseDate();
					break;

                case TokenType.boolFalse:
                case TokenType.boolTrue:
					resultado = ParseBoolean();
					break;

				default:
					var problematicLexeme = lexer.CurrentLexeme();
					throw new LanguageException($"Expected a literal value, but found '{problematicLexeme}' at line {Row()}, column {Column()}.", problematicLexeme.ToString(), Row(), Column());
			}
			return resultado;
		}

		private AstExpression ParseBoolean()
		{
			AstExpression resultado = null;
			switch (lexer.CurrentToken.Type)
			{
				case TokenType.boolTrue:
					resultado = LiteralBoolean.LiteralTrue;
					break;
				case TokenType.boolFalse:
					resultado = LiteralBoolean.LiteralFalse;
					break;
			}
            lexer.Accept();
			return resultado;
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
			AstExpression resultado = new LiteralNull();
			lexer.Accept();
			return resultado;
		}

		private AstExpression ParseNumber()
		{
			AstExpression resultado = new LiteralNumber(int.Parse(lexer.CurrentLexeme()));
			lexer.Accept();
			return resultado;
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
