using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Puppeteer.EventSourcing.Follower
{
	internal class Pattern
	{
		private readonly string patternText;
		private readonly ActorHandler actorHandler;

		private readonly PatternListNode patternAst;
		private readonly QuickTest quickTest;
		private readonly DomainLibraries libraries;

		internal Pattern(ReactionEngine reactionEngine, string patternText)
		{
			ArgumentNullException.ThrowIfNull(reactionEngine);
			ArgumentNullException.ThrowIfNull(patternText);

			this.patternText = patternText;
			this.actorHandler = reactionEngine.Reaction.ActorHandler;
			this.libraries = actorHandler.Libraries;

			var parser = new PatternParser();
			this.patternAst = parser.Parse(patternText);

			ValidateTypesInPattern(patternAst);
			ResolveTypesInPattern(patternAst);

			this.quickTest = GenerateQuickTest(patternAst);
		}

		internal string PatternText => patternText;

		internal PatternListNode PatternAst => patternAst;

		internal bool Match(string script, DateTime eventTimestamp, Parameters parameters, SymbolTable symbolTable, Program cachedProgram = null, string exposeDataJson = null)
		{
			ArgumentNullException.ThrowIfNull(script);
			ArgumentNullException.ThrowIfNull(parameters);
			ArgumentNullException.ThrowIfNull(symbolTable);

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[Pattern.Match] Trying pattern: {this.patternText}");
			System.Diagnostics.Debug.WriteLine($"[Pattern.Match] Against script: {script}");
#endif

			DomainLibraries libraries = actorHandler.Libraries;
			ActorHandler.ConcurrentParametersPool parametersPool = actorHandler.ParametersPool;

			// Step 1: quick test — fast substring search.
			if (!quickTest.Execute(script))
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[Pattern.Match] Quick test FAILED");
#endif
				return false; // No match: skip the expensive parse.
			}
#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[Pattern.Match] Quick test PASSED");
#endif

			// Step 2: obtain the script AST.
			// If a cached Program (with parameters already loaded) is provided, use it;
			// otherwise, parse the script.
			Program scriptAst;
			Parameters parametersForTyping = null;
			bool needsCleanup = false;

			if (cachedProgram != null)
			{
				// Reuse the cached Program, which already has parameters loaded and IsParameter set correctly.
				scriptAst = cachedProgram;
				// Refresh 'Now' so 'now' tokens in the journaled script resolve to
				// the event's OccurredAt — the same value ActorHandler.Perform uses
				// when replaying the event. Keeps pattern matching deterministic
				// across re-executions of the same journal.
				cachedProgram.Parameters.SystemParameter<DateTime>("Now", eventTimestamp);
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[Pattern.Match] Using cached Program with parameters already loaded");
#endif
			}
			else
			{
				// Parse the script normally (ScriptEventData or scripts without parameters).
				parametersForTyping = parametersPool.Rent();
				needsCleanup = true;
				// Override the pool's default 'Now' so 'now' in the journaled script
				// resolves to the event's OccurredAt (the moment the entry was journaled),
				// not to the pool's default(DateTime). Mirrors the behavior of
				// ActorHandler.Perform and MatchTree.EvaluateWhere for '@Now'.
				parametersForTyping.SystemParameter<DateTime>("Now", eventTimestamp);

				var parser = new Parser(libraries, symbolTable);
				parser.SetSource(script);

				try
				{
					scriptAst = parser.Parse(isQuery: false, isCheck: false);
					scriptAst.SolveReferences(parametersForTyping, withStaticValidation: true);
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[Pattern.Match] Script parsed successfully");
#endif
				}
				catch (LanguageException ex)
				{
					// If the script does not parse correctly, there is no match.
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[Pattern.Match] Parse/SolveReferences FAILED: {ex.Message}");
#endif
					parametersForTyping.PurgeUserParameters();
					parametersPool.Return(parametersForTyping);
					return false;
				}
			}

			// Step 3: use the PatternMatcher to compare ASTs.
			// The Program creates and prepares the PatternMatcher automatically.
			// Now we pass parameters carrying earlier captures so the matcher can verify them.
			Parameters matchResult;
			try
			{
				PatternMatcher matcher = scriptAst.CreatePatternMatcher(parametersPool);
				matcher.SetScriptText(script);
				matchResult = matcher.Match(patternAst, parameters, exposeDataJson);
			}
			catch (LanguageException ex)
			{
				// Re-throw exceptions related to OUT-parameter validation.
				if (ex.Message.Contains("Cannot match an OUT parameter"))
				{
					if (needsCleanup)
					{
						parametersForTyping.PurgeUserParameters();
						parametersPool.Return(parametersForTyping);
					}
					throw;
				}

				// Any error during matching means no match.
				if (needsCleanup)
				{
					parametersForTyping.PurgeUserParameters();
					parametersPool.Return(parametersForTyping);
				}
				return false;
			}
			finally
			{
				// Release the temporary parameters only if we created them ourselves.
				if (needsCleanup)
				{
					parametersForTyping.PurgeUserParameters();
					parametersPool.Return(parametersForTyping);
				}
			}

			if (matchResult != null)
			{
#if DEBUG
				int capturedCount = 0;
				foreach (var param in matchResult)
				{
					capturedCount++;
				}
				System.Diagnostics.Debug.WriteLine($"[Pattern.Match] PatternMatcher MATCHED! Captured params: {capturedCount}");
#endif
				// Copy the matched parameters into 'parameters'.
				foreach (var param in matchResult)
				{
					parameters[param.Name, param.ParameterType] = param.GetValue();
				}
				matchResult.PurgeUserParameters();
				parametersPool.Return(matchResult);
				return true;
			}
#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[Pattern.Match] PatternMatcher DID NOT MATCH");
#endif
			return false;
		}
		private QuickTest GenerateQuickTest(PatternListNode patternAst)
		{
			ArgumentNullException.ThrowIfNull(patternAst);

			var substrings = new List<string>();

			// Walk over the pattern expressions to extract substrings.
			foreach (var expression in patternAst.Expressions)
			{
				ExtractSubstrings(expression, substrings);
			}

			return new QuickTest(substrings);
		}
		private void ExtractSubstrings(ExpressionNode expression, List<string> substrings)
		{
			ArgumentNullException.ThrowIfNull(expression);

			switch (expression)
			{
				case TypeAccessNode typeAccess:
					// [Type].Member → search for "Type" and "Member".
					substrings.Add(typeAccess.TypeName);
					if (typeAccess.MemberAccess != null)
					{
						ExtractMemberSubstrings(typeAccess.MemberAccess, substrings);
					}
					break;

				case InstanceAccessNode instanceAccess:
					// [instance:Type].Member → search for "Type" and "Member".
					substrings.Add(instanceAccess.TypeName);
					if (instanceAccess.MemberAccess != null)
					{
						ExtractMemberSubstrings(instanceAccess.MemberAccess, substrings);
					}
					break;

				case ConstructorCallNode constructor:
					// Type(...) or [Type](...) → search for "Type".
					substrings.Add(constructor.TypeName);
					break;

				case AssignmentNode assignment:
					// $x = expression; → extract substrings from the right-hand side.
					ExtractSubstrings(assignment.Value, substrings);
					break;

				case PartialPatternNode partialPattern:
					// ... pattern1 ... pattern2 ... → extract substrings from each pattern.
					foreach (var pattern in partialPattern.Patterns)
					{
						ExtractSubstrings(pattern, substrings);
					}
					break;
				case ExposeNode exposeNode:
					// expose expression alias; → search for "expose".
					substrings.Add("expose");
					break;

				case GuardedExpressionNode guarded:
					// Extract substrings from the inner expression (not the guard).
					ExtractSubstrings(guarded.InnerExpression, substrings);
					break;

				case AlternativeExpressionNode alternative:
					// For alternatives we CANNOT add substrings to the quick test:
					// any branch can match (only one needs to be present).
					// Add nothing - the quick test cannot optimize alternatives.
					break;
			}
		}
		private void ExtractMemberSubstrings(MemberAccessNode memberAccess, List<string> substrings)
		{
			ArgumentNullException.ThrowIfNull(memberAccess);

			// Agregar el nombre del miembro
			substrings.Add(memberAccess.MemberName);

			// Si hay chaining, continuar recursivamente
			if (memberAccess.NextAccess != null)
			{
				ExtractMemberSubstrings(memberAccess.NextAccess, substrings);
			}
		}

		private void ValidateTypesInPattern(PatternListNode patternAst)
		{
			foreach (var expression in patternAst.Expressions)
			{
				ValidateTypesInExpression(expression);
			}
		}

		private void ValidateTypesInExpression(ExpressionNode expression)
		{
			switch (expression)
			{
				case TypeAccessNode typeAccess:
					ValidateType(typeAccess.TypeName);
					if (typeAccess.MemberAccess != null)
					{
						ValidateTypesInMemberAccess(typeAccess.MemberAccess);
					}
					break;

				case InstanceAccessNode instanceAccess:
					ValidateType(instanceAccess.TypeName);
					if (instanceAccess.MemberAccess != null)
					{
						ValidateTypesInMemberAccess(instanceAccess.MemberAccess);
					}
					break;

				case ConstructorCallNode constructor:
					ValidateType(constructor.TypeName);
					if (constructor.Parameters != null)
					{
						foreach (var param in constructor.Parameters)
						{
							ValidateTypesInParameter(param);
						}
					}
					break;

				case AssignmentNode assignment:
					ValidateTypesInExpression(assignment.Value);
					break;

				case PartialPatternNode partialPattern:
					foreach (var pattern in partialPattern.Patterns)
					{
						ValidateTypesInExpression(pattern);
					}
					break;
				case ExposeNode exposeNode:
					// Validar el type en la expression del expose (si es TypedParameterNode)
					ValidateTypesInParameter(exposeNode.Expression);
					break;

				case GuardedExpressionNode guarded:
					ValidateTypesInExpression(guarded.InnerExpression);
					break;

				case AlternativeExpressionNode alternative:
					foreach (var branch in alternative.Branches)
					{
						ValidateTypesInExpression(branch.Expression);
					}
					break;
			}
		}

		private void ValidateTypesInMemberAccess(MemberAccessNode memberAccess)
		{
			if (memberAccess.Parameters != null)
			{
				foreach (var param in memberAccess.Parameters)
				{
					ValidateTypesInParameter(param);
				}
			}

			if (memberAccess.NextAccess != null)
			{
				ValidateTypesInMemberAccess(memberAccess.NextAccess);
			}
		}

		private void ValidateTypesInParameter(ParameterNode parameter)
		{
			if (parameter is TypedParameterNode typedParam)
			{
				if (typedParam.ParameterType is UnresolvedArrayType unresolvedArray)
				{
					// Validate the array element type.
					Type elementType = unresolvedArray.ElementType;
					if (elementType is UnresolvedDomainType unresolvedElement)
					{
						ValidateType(unresolvedElement.TypeName);
					}
					// Primitive types (int[], string[]) need no further validation.
				}
				else if (typedParam.ParameterType is UnresolvedDomainType unresolved)
				{
					ValidateType(unresolved.TypeName);
				}
			}
			else if (parameter is LiteralParameterNode literalParam)
			{
				if (literalParam.ExplicitType != null)
				{
					if (literalParam.ExplicitType is UnresolvedArrayType unresolvedArray)
					{
						// Validate the array element type.
						Type elementType = unresolvedArray.ElementType;
						if (elementType is UnresolvedDomainType unresolvedElement)
						{
							ValidateType(unresolvedElement.TypeName);
						}
					}
					else if (literalParam.ExplicitType is UnresolvedDomainType unresolved)
					{
						ValidateType(unresolved.TypeName);
					}
				}
			}
		}

		private void ValidateType(string typeName)
		{
			if (
				String.Equals(typeName, "string", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "int", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "double", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "decimal", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "bool", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "DateTime", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "object", StringComparison.OrdinalIgnoreCase)
			)
			{
				return;
			}
			if (!libraries.Knows(typeName))
			{
				throw new LanguageException($"Type '{typeName}' was not found in the domain libraries.");
			}
		}

		private void ResolveTypesInPattern(PatternListNode patternAst)
		{
			foreach (var expression in patternAst.Expressions)
			{
				ResolveTypesInExpression(expression);
			}
		}

		private void ResolveTypesInExpression(ExpressionNode expression)
		{
			switch (expression)
			{
				case TypeAccessNode typeAccess:
					if (typeAccess.MemberAccess != null)
					{
						ResolveTypesInMemberAccess(typeAccess.MemberAccess);
					}
					break;

				case InstanceAccessNode instanceAccess:
					if (instanceAccess.MemberAccess != null)
					{
						ResolveTypesInMemberAccess(instanceAccess.MemberAccess);
					}
					break;

				case ConstructorCallNode constructor:
					if (constructor.Parameters != null)
					{
						foreach (var param in constructor.Parameters)
						{
							ResolveTypesInParameter(param);
						}
					}
					break;

				case AssignmentNode assignment:
					ResolveTypesInExpression(assignment.Value);
					break;

				case PartialPatternNode partialPattern:
					foreach (var pattern in partialPattern.Patterns)
					{
						ResolveTypesInExpression(pattern);
					}
					break;
				case ExposeNode exposeNode:
					// Resolve the type in the expose expression (if it's a TypedParameterNode).
					ResolveTypesInParameter(exposeNode.Expression);
					break;

				case GuardedExpressionNode guarded:
					ResolveTypesInExpression(guarded.InnerExpression);
					break;

				case AlternativeExpressionNode alternative:
					foreach (var branch in alternative.Branches)
					{
						ResolveTypesInExpression(branch.Expression);
					}
					break;
			}
		}

		private void ResolveTypesInMemberAccess(MemberAccessNode memberAccess)
		{
			if (memberAccess.Parameters != null)
			{
				foreach (var param in memberAccess.Parameters)
				{
					ResolveTypesInParameter(param);
				}
			}

			if (memberAccess.NextAccess != null)
			{
				ResolveTypesInMemberAccess(memberAccess.NextAccess);
			}
		}

		private void ResolveTypesInParameter(ParameterNode parameter)
		{
			if (parameter is TypedParameterNode typedParam)
			{
				if (typedParam.ParameterType is UnresolvedArrayType unresolvedArray)
				{
					// Resolve the array element type.
					Type elementType = unresolvedArray.ElementType;
					Type resolvedElementType = elementType;

					if (elementType is UnresolvedDomainType unresolvedElement)
					{
						// Look up the domain type case-insensitively.
						if (!libraries.TryGetType(unresolvedElement.TypeName, out resolvedElementType))
							throw new LanguageException($"Type '{unresolvedElement.TypeName}' was not found in the registered libraries.");
					}
					// If elementType is primitive (int, string, etc.), it is already resolved.

					// Build the real array type: int[] -> typeof(int[]).
					Type resolvedArrayType = resolvedElementType.MakeArrayType();

					// Replace the unresolved type with the real array type.
					var field = typeof(TypedParameterNode).GetField("<ParameterType>k__BackingField",
						System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
					if (field != null)
					{
						field.SetValue(typedParam, resolvedArrayType);
					}
				}
				else if (typedParam.ParameterType is UnresolvedDomainType unresolved)
				{
					// Look up the type case-insensitively.
					if (!libraries.TryGetType(unresolved.TypeName, out Type resolvedType))
						throw new LanguageException($"Type '{unresolved.TypeName}' was not found in the registered libraries.");
					// Replace the unresolved type with the real one.
					var field = typeof(TypedParameterNode).GetField("<ParameterType>k__BackingField",
						System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
					if (field != null)
					{
						field.SetValue(typedParam, resolvedType);
					}
				}
			}
			else if (parameter is LiteralParameterNode literalParam)
			{
				if (literalParam.ExplicitType != null && literalParam.ExplicitType is UnresolvedDomainType unresolved)
				{
					if (!libraries.TryGetType(unresolved.TypeName, out Type resolvedType))
						throw new LanguageException($"Type '{unresolved.TypeName}' was not found in the registered libraries.");
					// Replace the unresolved type with the real one.
					var field = typeof(LiteralParameterNode).GetField("<ExplicitType>k__BackingField",
						System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
					if (field != null)
					{
						field.SetValue(literalParam, resolvedType);
					}
				}
			}
		}
	}
	internal class QuickTest
	{
		private readonly List<string> substrings;

		internal QuickTest(List<string> substrings)
		{
			ArgumentNullException.ThrowIfNull(substrings);

			this.substrings = substrings;
		}
		internal bool Execute(string script)
		{
			ArgumentNullException.ThrowIfNull(script);

			int currentPosition = 0;

			// Check that each substring appears in order.
			foreach (var substring in substrings)
			{
				int foundPosition = script.IndexOf(substring, currentPosition, StringComparison.OrdinalIgnoreCase);

				if (foundPosition == -1)
				{
					// Substring not found.
					return false;
				}

				// Advance the position for the next search.
				currentPosition = foundPosition + substring.Length;
			}

			// All substrings found in order.
			return true;
		}
	}
}
