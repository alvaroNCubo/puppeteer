using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Puppeteer.EventSourcing.Follower
{
	internal class PatternParser
	{
		private readonly Lexer lexer;
		private static readonly NumberFormatInfo customFormat;

		static PatternParser()
		{
			CultureInfo original = CultureInfo.GetCultureInfo("en-US");
			customFormat = (NumberFormatInfo)original.NumberFormat.Clone();
			customFormat.NumberDecimalSeparator = ".";
		}

		internal PatternParser()
		{
			lexer = new Lexer();
		}

		internal PatternListNode Parse(string pattern)
		{
			ArgumentNullException.ThrowIfNull(pattern);

			lexer.Source = pattern;
			return ParsePatternList();
		}

		// <pattern-list> ::= <expression> [<where-clause>]* (<separator> <expression> [<where-clause>]*)*
		// <separator> ::= ' ' | '...' | '\n'
		private PatternListNode ParsePatternList()
		{
			List<ExpressionNode> expressions = new List<ExpressionNode>();

			// Consume optional leading '...' (syntactic sugar).
			while (lexer.CurrentToken.Type == TokenType.ellipsis)
			{
				lexer.Accept(TokenType.ellipsis);
			}

			expressions.Add(ParseExpressionWithOptionalGuards());

			// Consume multiple expressions separated by spaces, '...', or newlines.
			while (lexer.CurrentToken.Type != TokenType.eof)
			{
				// Consume optional separators: '...' is syntactic sugar equivalent to a space.
				if (lexer.CurrentToken.Type == TokenType.ellipsis)
				{
					lexer.Accept(TokenType.ellipsis);
					// If '...' is followed by EOF, stop.
					if (lexer.CurrentToken.Type == TokenType.eof)
					{
						break;
					}
				}

				// Whitespace and newlines are handled automatically by the lexer.
				expressions.Add(ParseExpressionWithOptionalGuards());
			}

			lexer.Accept(TokenType.eof);
			return new PatternListNode(expressions);
		}

		// Parses an expression with optional guards and optional OR alternatives.
		private ExpressionNode ParseExpressionWithOptionalGuards()
		{
			ExpressionNode firstExpression = ParseExpressionWithGuards();

			// Check for OR alternatives (|).
			if (lexer.CurrentToken.Type == TokenType.logicalOr)
			{
				List<AlternativeBranch> branches = new List<AlternativeBranch>();

				// First branch.
				string firstLabel = TryParseAsLabel();
				branches.Add(new AlternativeBranch(firstExpression, firstLabel));

				// Additional branches.
				while (lexer.CurrentToken.Type == TokenType.logicalOr)
				{
					lexer.Accept(TokenType.logicalOr);
					ExpressionNode branchExpr = ParseExpressionWithGuards();
					string label = TryParseAsLabel();
					branches.Add(new AlternativeBranch(branchExpr, label));
				}

				return new AlternativeExpressionNode(branches);
			}

			return firstExpression;
		}

		// Parses an expression and, optionally, one or more 'where' clauses.
		private ExpressionNode ParseExpressionWithGuards()
		{
			ExpressionNode expression = ParseExpression();

			// Check for 'where' clauses.
			List<GuardClause> guards = null;
			while (lexer.CurrentToken.Type == TokenType.id && lexer.CurrentLexeme().SequenceEqual("where".AsSpan()))
			{
				if (guards == null) guards = new List<GuardClause>();
				guards.Add(ParseGuardClause());
			}

			if (guards != null)
			{
				return new GuardedExpressionNode(expression, guards);
			}

			return expression;
		}

		// Try to parse 'as label' after an OR expression.
		private string TryParseAsLabel()
		{
			if (lexer.CurrentToken.Type == TokenType.@as)
			{
				lexer.Accept(TokenType.@as);

				if (lexer.CurrentToken.Type == TokenType.stringLit)
				{
					string label = lexer.CurrentLexeme().ToString();
					lexer.Accept(TokenType.stringLit);
					return label;
				}

				throw new LanguageException($"Expected a string literal after 'as', but found '{lexer.CurrentLexeme()}'.");
			}

			return null;
		}

		// <where-clause> ::= 'where' <guard-expression>
		// <guard-expression> ::= '$variable' <comparison-op> <literal>
		//                      | 'not' 'contains' <string-literal>
		//                      | 'contains' <string-literal>
		private GuardClause ParseGuardClause()
		{
			// Consume 'where' (it is a contextual identifier).
			lexer.Accept(TokenType.id); // where

			// Check whether it is 'not contains' or 'contains'.
			if (lexer.CurrentToken.Type == TokenType.id)
			{
				ReadOnlySpan<char> currentLexeme = lexer.CurrentLexeme();

				if (currentLexeme.SequenceEqual("not".AsSpan()))
				{
					lexer.Accept(TokenType.id); // not

					if (lexer.CurrentToken.Type == TokenType.id && lexer.CurrentLexeme().SequenceEqual("contains".AsSpan()))
					{
						lexer.Accept(TokenType.id); // contains
						LiteralParameterNode literal = ParseLiteral();
						return new GuardClause(null, GuardOperator.NotContains, literal.Value, literal.LiteralType);
					}

					throw new LanguageException($"Expected 'contains' after 'not' in 'where' clause, but found '{lexer.CurrentLexeme()}'.");
				}

				if (currentLexeme.SequenceEqual("contains".AsSpan()))
				{
					lexer.Accept(TokenType.id); // contains
					LiteralParameterNode literal = ParseLiteral();
					return new GuardClause(null, GuardOperator.Contains, literal.Value, literal.LiteralType);
				}
			}

			// Must be: $variable op literal
			if (lexer.CurrentToken.Type != TokenType.variable)
			{
				throw new LanguageException($"Expected '$variable', 'not' or 'contains' after 'where', but found '{lexer.CurrentLexeme()}'.");
			}

			string variableName = lexer.CurrentLexeme().ToString();
			lexer.Accept(TokenType.variable);

			// Parse the comparison operator.
			GuardOperator op;
			switch (lexer.CurrentToken.Type)
			{
				case TokenType.equality:
					op = GuardOperator.Equal;
					lexer.Accept(TokenType.equality);
					break;
				case TokenType.inequality:
					op = GuardOperator.NotEqual;
					lexer.Accept(TokenType.inequality);
					break;
				case TokenType.greaterThan:
					op = GuardOperator.GreaterThan;
					lexer.Accept(TokenType.greaterThan);
					break;
				case TokenType.lessThan:
					op = GuardOperator.LessThan;
					lexer.Accept(TokenType.lessThan);
					break;
				case TokenType.greaterOrEqual:
					op = GuardOperator.GreaterOrEqual;
					lexer.Accept(TokenType.greaterOrEqual);
					break;
				case TokenType.lessOrEqual:
					op = GuardOperator.LessOrEqual;
					lexer.Accept(TokenType.lessOrEqual);
					break;
				default:
					throw new LanguageException($"Expected a comparison operator (==, !=, >, <, >=, <=) after '{variableName}', but found '{lexer.CurrentLexeme()}'.");
			}

			// Parse the literal value.
			LiteralParameterNode literalValue = ParseLiteral();

			return new GuardClause(variableName, op, literalValue.Value, literalValue.LiteralType);
		}

		// <expression> ::= <assignment> | <instance-access> | <type-access> | <constructor-call> | <tell-pattern>
		private ExpressionNode ParseExpression()
		{
			// Detect 'expose' patterns: expose expression alias;
			if (lexer.CurrentToken.Type == TokenType.expose)
			{
				return ParseExpose();
			}
			// Plan 7 (b) of the Tell primitive roadmap: tell-shaped patterns
			// parsed before bracket / id / variable forms. The `tell` keyword
			// (TokenType.tell) was added in Plan 1; the parser only recognises
			// it here, in the pattern grammar entry point.
			else if (lexer.CurrentToken.Type == TokenType.tell)
			{
				return ParseTellPattern();
			}
			// Detect assignments that start with $variable or _
			else if (lexer.CurrentToken.Type == TokenType.variable || lexer.CurrentToken.Type == TokenType.wildcard)
			{
				return ParseAssignment();
			}
			else if (lexer.CurrentToken.Type == TokenType.lBracket)
			{
				// Logic for [instance:Type], [Type], etc.
				return ParseBracketExpression();
			}
			else if (lexer.CurrentToken.Type == TokenType.id)
			{
				// Could be an assignment (id = ...) or a constructor (id(...)).
				string identifier = ParseIdentifier();

				if (lexer.CurrentToken.Type == TokenType.assign)
				{
					// Free-pattern assignment.
					return ParseAssignmentAfterIdentifier(identifier);
				}
				else if (lexer.CurrentToken.Type == TokenType.lParen)
				{
					// Constructor call.
					List<ParameterNode> parameters = ParseMethodCall();
					return new ConstructorCallNode(identifier, parameters);
				}
				else
				{
					throw new LanguageException($"Expected '=' or '(' after identifier '{identifier}', but found '{lexer.CurrentLexeme()}'.");
				}
			}
			else
			{
				throw new LanguageException($"Expected '[', identifier, variable or wildcard at the start of the expression, but found '{lexer.CurrentLexeme()}'.");
			}
		}

		private ExpressionNode ParseBracketExpression()
		{
			lexer.Accept(TokenType.lBracket);

			string firstId = ParseIdentifier();

			if (lexer.CurrentToken.Type == TokenType.colon)
			{
				// [instance:Type]
				lexer.Accept(TokenType.colon);
				string typeName = ParseIdentifier();
				lexer.Accept(TokenType.rBracket);

				MemberAccessNode memberAccess = null;
				if (lexer.CurrentToken.Type == TokenType.dot)
				{
					memberAccess = ParseMemberAccess();
				}

				return new InstanceAccessNode(firstId, typeName, memberAccess);
			}
			else if (lexer.CurrentToken.Type == TokenType.rBracket)
			{
				// [Type] form.
				lexer.Accept(TokenType.rBracket);

				if (lexer.CurrentToken.Type == TokenType.lParen)
				{
					// [Type](...) - constructor.
					List<ParameterNode> parameters = ParseMethodCall();
					return new ConstructorCallNode(firstId, parameters);
				}
				else if (lexer.CurrentToken.Type == TokenType.dot)
				{
					// [Type].Member - type access.
					MemberAccessNode memberAccess = ParseMemberAccess();
					return new TypeAccessNode(firstId, memberAccess);
				}
				else
				{
					// [Type] alone.
					return new TypeAccessNode(firstId, null);
				}
			}
			else
			{
				throw new LanguageException($"Expected ':' or ']' after the identifier in pattern, but found '{lexer.CurrentLexeme()}'.");
			}
		}

		// Plan 7 (b) of the Tell primitive roadmap: parse tell-shaped patterns.
		// Two forms:
		//
		//   tell <TargetClass>(<targetParam>) <CommandName>(<commandParams>) [id <idParam>]
		//   tell ack <ackIdParam> [from <FromTargetClass>(<fromTargetParam>)]
		//
		// Saga verbs (start/step/compensate/close) and the through trailer are
		// intentionally NOT recognised by Plan 7 (b) — saga matchers come with
		// Plan 8 of the roadmap; through is speculative until a real use case
		// motivates it (see feedback_dsl_pattern_priorities).
		private ExpressionNode ParseTellPattern()
		{
			lexer.Accept(TokenType.tell);

			// `tell ack ...` form: contextual `ack` keyword, recognised by lexeme.
			if (lexer.CurrentToken.Type == TokenType.id
				&& lexer.CurrentLexeme().SequenceEqual("ack".AsSpan()))
			{
				lexer.Accept(TokenType.id); // consume 'ack'
				ParameterNode ackIdParameter = ParseParameter();

				string fromTargetClass = null;
				ParameterNode fromTargetParameter = null;
				if (lexer.CurrentToken.Type == TokenType.id
					&& lexer.CurrentLexeme().SequenceEqual("from".AsSpan()))
				{
					lexer.Accept(TokenType.id); // consume 'from'
					if (lexer.CurrentToken.Type != TokenType.id)
					{
						throw new LanguageException($"Expected a target class identifier after 'from' in 'tell ack ... from <Class>(<param>)', but found '{lexer.CurrentLexeme()}'.");
					}
					fromTargetClass = ParseIdentifier();
					lexer.Accept(TokenType.lParen);
					fromTargetParameter = ParseParameter();
					lexer.Accept(TokenType.rParen);
				}

				return new TellAckPatternNode(ackIdParameter, fromTargetClass, fromTargetParameter);
			}

			// Outbound `tell <Target>(<targetParam>) <Cmd>(<commandParams>) [id <idParam>]` form.
			if (lexer.CurrentToken.Type != TokenType.id)
			{
				throw new LanguageException($"Expected a target class identifier or 'ack' after 'tell' in pattern, but found '{lexer.CurrentLexeme()}'.");
			}
			string targetClass = ParseIdentifier();
			lexer.Accept(TokenType.lParen);
			ParameterNode targetParameter = ParseParameter();
			lexer.Accept(TokenType.rParen);

			if (lexer.CurrentToken.Type != TokenType.id)
			{
				throw new LanguageException($"Expected a command name identifier after the target in 'tell <Target>(<param>) <Cmd>(...)', but found '{lexer.CurrentLexeme()}'.");
			}
			string commandName = ParseIdentifier();
			lexer.Accept(TokenType.lParen);
			List<ParameterNode> commandParameters = new List<ParameterNode>();
			if (lexer.CurrentToken.Type != TokenType.rParen)
			{
				commandParameters = ParseParameterList();
			}
			lexer.Accept(TokenType.rParen);

			ParameterNode idParameter = null;
			if (lexer.CurrentToken.Type == TokenType.id
				&& lexer.CurrentLexeme().SequenceEqual("id".AsSpan()))
			{
				lexer.Accept(TokenType.id); // consume 'id'
				idParameter = ParseParameter();
			}

			return new TellPatternNode(targetClass, targetParameter, commandName, commandParameters, idParameter);
		}

		// <assignment> ::= (<variable> | <wildcard> | <identifier>) [':' <type>] '=' <assignment-value> ';'
		private AssignmentNode ParseAssignment()
		{
			string variableName;

			if (lexer.CurrentToken.Type == TokenType.wildcard)
			{
				variableName = "_";
				lexer.Accept(TokenType.wildcard);
			}
			else if (lexer.CurrentToken.Type == TokenType.variable)
			{
				variableName = lexer.CurrentLexeme().ToString();
				lexer.Accept(TokenType.variable);
			}
			else
			{
				// This handles the free-pattern case that was already consumed
				// and is called from ParseExpression.
				throw new InvalidOperationException($"{nameof(ParseAssignment)} must not be called directly with an id token.");
			}

			return ParseAssignmentAfterIdentifier(variableName);
		}

		private AssignmentNode ParseAssignmentAfterIdentifier(string variableName)
		{
			// Optional: parse a type: $variable:Type or anInstance:Type.
			if (lexer.CurrentToken.Type == TokenType.colon)
			{
				lexer.Accept(TokenType.colon);
				Type paramType = ParseType();
				variableName = variableName + ":" + paramType.Name;
			}

			lexer.Accept(TokenType.assign);

			ExpressionNode value = ParseAssignmentValue();

			lexer.Accept(TokenType.semicolon);

			return new AssignmentNode(variableName, value);
		}

		// ParseAssignmentValue: parses the right-hand side of an assignment.
		// Can be: a partial pattern (... pattern ...) or a simple expression.
		private ExpressionNode ParseAssignmentValue()
		{
			bool hasLeadingWildcard = false;
			List<ExpressionNode> patterns = new List<ExpressionNode>();

			// Check if it starts with '...'.
			if (lexer.CurrentToken.Type == TokenType.ellipsis)
			{
				hasLeadingWildcard = true;
				lexer.Accept(TokenType.ellipsis);
			}

			// Parse the first pattern (required).
			patterns.Add(ParsePatternElement());

			// Check for additional patterns separated by '...' or a trailing '...'.
			bool hasTrailingWildcard = false;

			while (lexer.CurrentToken.Type == TokenType.ellipsis)
			{
				lexer.Accept(TokenType.ellipsis);

				// If '...' is followed by ';' then it's a trailing wildcard.
				if (lexer.CurrentToken.Type == TokenType.semicolon)
				{
					hasTrailingWildcard = true;
					break;
				}

				// Otherwise, another pattern must follow.
				patterns.Add(ParsePatternElement());
			}

			// If there is just one pattern and no wildcards, return the simple expression.
			if (patterns.Count == 1 && !hasLeadingWildcard && !hasTrailingWildcard)
			{
				return patterns[0];
			}

			// If there are wildcards or multiple patterns, build a PartialPatternNode.
			return new PartialPatternNode(hasLeadingWildcard, patterns, hasTrailingWildcard);
		}

		// ParsePatternElement: parses a single pattern element.
		// Can be: [instance:Type].Member, [Type].Member, Type(...), a literal, or a wildcard.
		private ExpressionNode ParsePatternElement()
		{
			// Check for a simple wildcard.
			if (lexer.CurrentToken.Type == TokenType.wildcard)
			{
				lexer.Accept(TokenType.wildcard);
				return new WildcardExpressionNode();
			}

			// Check for a literal (number, string, etc.).
			if (lexer.CurrentToken.Type == TokenType.number ||
				lexer.CurrentToken.Type == TokenType.@decimal ||
				lexer.CurrentToken.Type == TokenType.@double ||
				lexer.CurrentToken.Type == TokenType.stringLit ||
				lexer.CurrentToken.Type == TokenType.boolTrue ||
				lexer.CurrentToken.Type == TokenType.boolFalse)
			{
				return new LiteralExpressionNode(ParseLiteral());
			}

			if (lexer.CurrentToken.Type == TokenType.lBracket)
			{
				// Could be [instance:Type], [Type], or [Type](...).
				lexer.Accept(TokenType.lBracket);

				string firstId = ParseIdentifier();

				if (lexer.CurrentToken.Type == TokenType.colon)
				{
					// [instance:Type] form.
					lexer.Accept(TokenType.colon);
					string typeName = ParseIdentifier();
					lexer.Accept(TokenType.rBracket);

					MemberAccessNode memberAccess = null;
					if (lexer.CurrentToken.Type == TokenType.dot)
					{
						memberAccess = ParseMemberAccess();
					}

					var instanceAccess = new InstanceAccessNode(firstId, typeName, memberAccess);

					// Check for assignment form: [instance:Type].Member = value ;
					if (lexer.CurrentToken.Type == TokenType.assign)
					{
						lexer.Accept(TokenType.assign);
						ExpressionNode value = ParseAssignmentValue();
						lexer.Accept(TokenType.semicolon);
						return new AssignmentNode(instanceAccess.ToString(), value);
					}

					return instanceAccess;
				}
				else if (lexer.CurrentToken.Type == TokenType.rBracket)
				{
					// [Type] - either a type access or a constructor call.
					lexer.Accept(TokenType.rBracket);

					if (lexer.CurrentToken.Type == TokenType.lParen)
					{
						// [Type](...) - constructor.
						List<ParameterNode> parameters = ParseMethodCall();
						return new ConstructorCallNode(firstId, parameters);
					}
					else if (lexer.CurrentToken.Type == TokenType.dot)
					{
						// [Type].Member - type access.
						MemberAccessNode memberAccess = ParseMemberAccess();
						var typeAccess = new TypeAccessNode(firstId, memberAccess);

						// Check for assignment form: [Type].Member = value ;
						if (lexer.CurrentToken.Type == TokenType.assign)
						{
							lexer.Accept(TokenType.assign);
							ExpressionNode value = ParseAssignmentValue();
							lexer.Accept(TokenType.semicolon);
							return new AssignmentNode(typeAccess.ToString(), value);
						}

						return typeAccess;
					}
					else
					{
						// [Type] alone - type access without a member.
						return new TypeAccessNode(firstId, null);
					}
				}
				else
				{
					throw new LanguageException($"Expected ':' or ']' after the identifier in pattern, but found '{lexer.CurrentLexeme()}'.");
				}
			}
			else if (lexer.CurrentToken.Type == TokenType.id)
			{
				// Could be Type(...) - constructor.
				string typeName = ParseIdentifier();

				if (lexer.CurrentToken.Type == TokenType.lParen)
				{
					// Type(...) - constructor.
					List<ParameterNode> parameters = ParseMethodCall();
					return new ConstructorCallNode(typeName, parameters);
				}
				else
				{
					throw new LanguageException($"Expected '(' after identifier '{typeName}' for a constructor call, but found '{lexer.CurrentLexeme()}'.");
				}
			}
			else
			{
				throw new LanguageException($"Expected '[' or an identifier in pattern, but found '{lexer.CurrentLexeme()}'.");
			}
		}

		// <member-access> ::= '.' <identifier> <method-call>? | '.' <identifier> <member-access>?
		private MemberAccessNode ParseMemberAccess()
		{
			lexer.Accept(TokenType.dot);
			string memberName = ParseIdentifier();

			List<ParameterNode> parameters = null;
			MemberAccessNode nextAccess = null;

			if (lexer.CurrentToken.Type == TokenType.lParen)
			{
				// Method call.
				parameters = ParseMethodCall();

				// Check for further chaining.
				if (lexer.CurrentToken.Type == TokenType.dot)
				{
					nextAccess = ParseMemberAccess();
				}
			}
			else if (lexer.CurrentToken.Type == TokenType.dot)
			{
				// Property with chaining.
				nextAccess = ParseMemberAccess();
			}
			// else: property without chaining.

			return new MemberAccessNode(memberName, parameters, nextAccess);
		}

		// <method-call> ::= '(' <parameter-list>? ')'
		private List<ParameterNode> ParseMethodCall()
		{
			lexer.Accept(TokenType.lParen);

			List<ParameterNode> parameters = new List<ParameterNode>();

			if (lexer.CurrentToken.Type != TokenType.rParen)
			{
				parameters = ParseParameterList();
			}

			lexer.Accept(TokenType.rParen);
			return parameters;
		}

		// <parameter-list> ::= <parameter> (',' <parameter>)*
		private List<ParameterNode> ParseParameterList()
		{
			List<ParameterNode> parameters = new List<ParameterNode>();

			parameters.Add(ParseParameter());

			while (lexer.CurrentToken.Type == TokenType.comma)
			{
				lexer.Accept(TokenType.comma);
				parameters.Add(ParseParameter());
			}

			return parameters;
		}

		// <parameter> ::= <typed-parameter> | <literal> | <variable> | '_'
		// <typed-parameter> ::= <identifier> ':' <type-name> | <literal> ':' <type-name>
		private ParameterNode ParseParameter()
		{
			// Argument that is a nested call-with-receiver: foo([_:Derived].goo($x)).
			// Reuses ParsePatternElement for the bracketed form ([instance:Type].m(...)
			// or [Type].m(...)) and wraps it. The receiver carries its type, which is what
			// the matcher needs to resolve the inner method.
			if (lexer.CurrentToken.Type == TokenType.lBracket)
			{
				ExpressionNode nestedCall = ParsePatternElement();
				return new NestedCallParameterNode(nestedCall);
			}

			if (lexer.CurrentToken.Type == TokenType.wildcard)
			{
				lexer.Accept(TokenType.wildcard);

				// Check whether the wildcard has a type: _:Type
				if (lexer.CurrentToken.Type == TokenType.colon)
				{
					lexer.Accept(TokenType.colon);
					Type paramType = ParseType();
					return new TypedParameterNode(null, paramType);
				}

				return new WildcardParameterNode();
			}
			else if (lexer.CurrentToken.Type == TokenType.variable)
			{
				string varName = lexer.CurrentLexeme().ToString();
				lexer.Accept(TokenType.variable);

				// Check whether the variable has a type: $variable:Type
				if (lexer.CurrentToken.Type == TokenType.colon)
				{
					lexer.Accept(TokenType.colon);
					Type paramType = ParseType();
					return new TypedParameterNode(varName, paramType);
				}

				return new VariableParameterNode(varName);
			}
			else if (lexer.CurrentToken.Type == TokenType.id)
			{
				// Could be identifier:Type, or just an identifier (free pattern).
				string identifier = lexer.CurrentLexeme().ToString();
				lexer.Accept(TokenType.id);

				if (lexer.CurrentToken.Type == TokenType.colon)
				{
					// Typed parameter: identifier:Type.
					lexer.Accept(TokenType.colon);
					Type paramType = ParseType();
					return new TypedParameterNode(identifier, paramType);
				}
				else
				{
					// Free pattern: identifier without a type.
					// Captures the identifier name from the script.
					return new TypedParameterNode(identifier, typeof(string));
				}
			}
			else
			{
				// Literal, possibly with an optional type.
				return ParseLiteralWithOptionalType();
			}
		}

		// ParseLiteralWithOptionalType: literal with an optional type (literal:Type).
		private LiteralParameterNode ParseLiteralWithOptionalType()
		{
			LiteralParameterNode literal = ParseLiteral();

			// Check for an explicit type.
			if (lexer.CurrentToken.Type == TokenType.colon)
			{
				lexer.Accept(TokenType.colon);
				Type explicitType = ParseType();

				// Validate that the explicit type is compatible with the literal.
				ValidateLiteralType(literal, explicitType);

				return new LiteralParameterNode(literal.Value, literal.LiteralType, explicitType);
			}

			return literal;
		}

		// ParseType: parses a type name and resolves it to System.Type.
		// Supports simple types (int, string) and array types (int[], string[]).
		private Type ParseType()
		{
			if (lexer.CurrentToken.Type != TokenType.id)
			{
				throw new LanguageException($"Expected a type name, but found '{lexer.CurrentLexeme()}'.");
			}

			string typeName = lexer.CurrentLexeme().ToString();
			lexer.Accept(TokenType.id);

			// Resolve the base type.
			Type resolvedType = ResolveType(typeName);

			// Check whether this is an array type: type[]
			if (lexer.CurrentToken.Type == TokenType.lBracket)
			{
				lexer.Accept(TokenType.lBracket);
				lexer.Accept(TokenType.rBracket);

				// Build the array type from the base type:
				// - For primitive types: int[] -> typeof(int[]).
				// - For UnresolvedDomainType: wrap it in UnresolvedArrayType.
				if (resolvedType is UnresolvedDomainType unresolvedType)
				{
					// Unresolved domain types use the UnresolvedArrayType placeholder.
					resolvedType = new UnresolvedArrayType(unresolvedType);
				}
				else
				{
					// Primitive types build a real array type.
					resolvedType = resolvedType.MakeArrayType();
				}
			}

			return resolvedType;
		}

		// ResolveType: converts the type name into a System.Type.
		private Type ResolveType(string typeName)
		{
			// Common primitive types.
			if (typeName.Equals("int", StringComparison.OrdinalIgnoreCase))
				return typeof(int);
			else if (typeName.Equals("string", StringComparison.OrdinalIgnoreCase))
				return typeof(string);
			else if (typeName.Equals("bool", StringComparison.OrdinalIgnoreCase))
				return typeof(bool);
			else if (typeName.Equals("double", StringComparison.OrdinalIgnoreCase))
				return typeof(double);
			else if (typeName.Equals("decimal", StringComparison.OrdinalIgnoreCase))
				return typeof(decimal);
			else if (typeName.Equals("datetime", StringComparison.OrdinalIgnoreCase))
				return typeof(DateTime);
			else if (typeName.Equals("byte", StringComparison.OrdinalIgnoreCase))
				return typeof(byte);
			else if (typeName.Equals("object", StringComparison.OrdinalIgnoreCase))
				return typeof(object);

			// If it isn't a primitive type, treat it as a domain type.
			// We return UnresolvedDomainType as a placeholder; actual resolution
			// happens during the matching phase, with access to the loaded DLLs.
			return new UnresolvedDomainType(typeName);
		}

		// ValidateLiteralType: validates that a literal is compatible with the given type.
		private void ValidateLiteralType(LiteralParameterNode literal, Type explicitType)
		{
			// Unresolved domain types are not validated at parse time.
			if (explicitType is UnresolvedDomainType)
			{
				// Validation happens during the matching phase.
				return;
			}

			if (literal.Value == null)
			{
				// null is compatible with any reference type.
				if (explicitType.IsValueType && Nullable.GetUnderlyingType(explicitType) == null)
				{
					throw new LanguageException($"Cannot assign null to value type '{explicitType.Name}'.");
				}
				return;
			}

			Type literalType = literal.LiteralType;

			// Check compatibility using the same criterion as AstExpression.AreCompatible:
			// - literalType is the argument type (value we have).
			// - explicitType is the parameter type (type we want).
			if (AstExpression.AreCompatible(literalType, explicitType))
			{
				return;
			}

			// Throw if they are not compatible.
			throw new LanguageException($"Literal of type '{literalType.Name}' is not compatible with the specified type '{explicitType.Name}'. " +
				$"For example, {(literalType == typeof(int) ? "10" : (literalType == typeof(decimal) ? "10m" : "3.14"))} cannot be a '{explicitType.Name}'.");
		}

		// <literal> ::= <string-literal> | <number-literal> | <bool-literal> | <datetime-literal> | <null-literal>
		private LiteralParameterNode ParseLiteral()
		{
			TokenType type = lexer.CurrentToken.Type;

			switch (type)
			{
				case TokenType.stringLit:
					{
						string value = lexer.CurrentLexeme().ToString();
						lexer.Accept(TokenType.stringLit);
						return new LiteralParameterNode(value, typeof(string));
					}

				case TokenType.number:
					{
						string value = lexer.CurrentLexeme().ToString();
						lexer.Accept(TokenType.number);
						int intValue = int.Parse(value, customFormat);
						return new LiteralParameterNode(intValue, typeof(int));
					}

				case TokenType.@decimal:
					{
						string value = lexer.CurrentLexeme().ToString();
						lexer.Accept(TokenType.@decimal);
						decimal decimalValue = decimal.Parse(value, customFormat);
						return new LiteralParameterNode(decimalValue, typeof(decimal));
					}

				case TokenType.@double:
					{
						string value = lexer.CurrentLexeme().ToString();
						lexer.Accept(TokenType.@double);
						double doubleValue = double.Parse(value, customFormat);
						return new LiteralParameterNode(doubleValue, typeof(double));
					}

				case TokenType.boolTrue:
					{
						lexer.Accept(TokenType.boolTrue);
						return new LiteralParameterNode(true, typeof(bool));
					}

				case TokenType.boolFalse:
					{
						lexer.Accept(TokenType.boolFalse);
						return new LiteralParameterNode(false, typeof(bool));
					}

				case TokenType.nullToken:
					{
						lexer.Accept(TokenType.nullToken);
						return new LiteralParameterNode(null, typeof(object));
					}

				case TokenType.date:
					{
						string value = lexer.CurrentLexeme().ToString();
						lexer.Accept(TokenType.date);
						DateTime dateValue = DateTime.Parse(value, CultureInfo.InvariantCulture);
						return new LiteralParameterNode(dateValue, typeof(DateTime));
					}

				case TokenType.time:
					{
						string value = lexer.CurrentLexeme().ToString();
						lexer.Accept(TokenType.time);
						// Parse as a TimeSpan and convert to DateTime.
						TimeSpan timeValue = TimeSpan.Parse(value, CultureInfo.InvariantCulture);
						DateTime dateTimeValue = DateTime.Today.Add(timeValue);
						return new LiteralParameterNode(dateTimeValue, typeof(DateTime));
					}

				case TokenType.begin:
					{
						// Array literal: {1,2,3} or {'a','b','c'}.
						return ParseArrayLiteral();
					}

				default:
					throw new LanguageException($"Expected a literal, but found '{lexer.CurrentLexeme()}'.");
			}
		}

		// ParseArrayLiteral: parses an array literal {element1, element2, ...}
		// and validates that all elements share the same type.
		private LiteralParameterNode ParseArrayLiteral()
		{
			lexer.Accept(TokenType.begin);

			List<LiteralParameterNode> elements = new List<LiteralParameterNode>();

			// Empty array: {}
			if (lexer.CurrentToken.Type == TokenType.end)
			{
				lexer.Accept(TokenType.end);
				// An empty array defaults to int[].
				return new LiteralParameterNode(Array.Empty<int>(), typeof(int[]));
			}

			// Reject nested arrays (not supported in this version).
			if (lexer.CurrentToken.Type == TokenType.begin)
			{
				throw new LanguageException("Nested arrays are not supported in this version. Use simple types in array literals.");
			}

			// Parse the first element.
			LiteralParameterNode firstElement = ParseLiteral();
			elements.Add(firstElement);
			Type elementType = firstElement.LiteralType;

			// Parse remaining elements.
			while (lexer.CurrentToken.Type == TokenType.comma)
			{
				lexer.Accept(TokenType.comma);

				// Reject nested arrays.
				if (lexer.CurrentToken.Type == TokenType.begin)
				{
					throw new LanguageException("Nested arrays are not supported in this version. Use simple types in array literals.");
				}

				LiteralParameterNode element = ParseLiteral();

				// Validate that the element type matches the first one.
				if (element.LiteralType != elementType)
				{
					throw new LanguageException($"All elements of an array literal must share the same type. " +
						$"Expected '{elementType.Name}' but found '{element.LiteralType.Name}'.");
				}

				elements.Add(element);
			}

			lexer.Accept(TokenType.end);

			// Build the array with the parsed values.
			Array arrayValue = CreateArrayFromElements(elements, elementType);
			Type arrayType = arrayValue.GetType();

			return new LiteralParameterNode(arrayValue, arrayType);
		}

		// CreateArrayFromElements: builds an array from a list of literal elements.
		private Array CreateArrayFromElements(List<LiteralParameterNode> elements, Type elementType)
		{
			Array array = Array.CreateInstance(elementType, elements.Count);

			for (int i = 0; i < elements.Count; i++)
			{
				array.SetValue(elements[i].Value, i);
			}

			return array;
		}

		// <identifier> ::= [a-zA-Z_#@][a-zA-Z0-9_#@]*
		private string ParseIdentifier()
		{
			if (lexer.CurrentToken.Type == TokenType.id)
			{
				string id = lexer.CurrentLexeme().ToString();
				lexer.Accept(TokenType.id);
				return id;
			}
			else if (lexer.CurrentToken.Type == TokenType.wildcard)
			{
				// '_' can also serve as an instance name.
				lexer.Accept(TokenType.wildcard);
				return "_";
			}
			else
			{
				throw new LanguageException($"Expected an identifier, but found '{lexer.CurrentLexeme()}'.");
			}
		}
	// <expose> ::= 'expose' <parameter> <identifier> ';'
	// Examples:
	//   expose _:int total;      → match alias "total" with type int.
	//   expose 100 total;        → match alias "total" with literal value 100.
	//   expose _ total;          → match any value at alias "total".
	//   expose $x total;         → capture the alias "total" value into $x (Step 13).
	private ExposeNode ParseExpose()
	{
		lexer.Accept(TokenType.expose);

		ParameterNode expression = ParseParameter();

		string alias = ParseIdentifier();

		lexer.Accept(TokenType.semicolon);

		return new ExposeNode(expression, alias);
	}
}
}



