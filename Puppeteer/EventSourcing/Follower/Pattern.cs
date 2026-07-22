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
		// B.2: kept as a direct field so the per-Reaction match cache can be
		// consulted from Match() without walking back through ReactionEngine.
		private readonly Reaction reaction;

		private readonly PatternListNode patternAst;
		private readonly QuickTest quickTest;
		private readonly DomainLibraries libraries;

		// Names captured only for diagnostics raised by the definition-time static
		// method-signature validation (B): they let a mismatch cite the offending
		// Reaction and Seek.
		private readonly string reactionName;
		private readonly string seekName;

		internal Pattern(ReactionEngine reactionEngine, string patternText)
		{
			ArgumentNullException.ThrowIfNull(reactionEngine);
			ArgumentNullException.ThrowIfNull(patternText);

			this.patternText = patternText;
			this.reaction = reactionEngine.Reaction;
			this.actorHandler = reaction.ActorHandler;
			this.libraries = actorHandler.Libraries;
			this.reactionName = reaction.Name;
			this.seekName = reactionEngine.PatternDescription;

			var parser = new PatternParser();
			this.patternAst = parser.Parse(patternText);

			ValidateTypesInPattern(patternAst);
			ResolveTypesInPattern(patternAst);
			ValidateMethodSignaturesInPattern(patternAst);

			this.quickTest = GenerateQuickTest(patternAst);
		}

		internal string PatternText => patternText;

		internal PatternListNode PatternAst => patternAst;

		// Router support (Tier-1 structural admission). Runs this pattern's QuickTest — the
		// SAME ordered-substring prefilter Match() uses as its first gate — against a FIXED
		// Action body text (entry.Script, which carries @param NAMES, not per-invocation
		// values). Since that text is invariant per ActionId, so is this verdict, which is
		// what lets the router memoize it per ActionId. QuickTest never rejects a script the
		// full matcher would accept (its no-false-negative contract), so a `false` here
		// proves this pattern can NEVER match any invocation of that Action — the Seek level
		// is safe to skip. A `true` is only "maybe": Match() still applies the literal /
		// $-correlation / Where tests per event. (Alternatives contribute no substrings, so
		// QuickTest passes and the level is routed — graceful, sound degradation.)
		internal bool StructurallyAdmits(string actionBodyText)
		{
			ArgumentNullException.ThrowIfNull(actionBodyText);
			return quickTest.Execute(actionBodyText);
		}

		internal bool Match(string script, DateTime eventTimestamp, Parameters parameters, SymbolTable symbolTable, Program cachedProgram = null, bool cachedProgramIsCanonical = false, string exposeDataJson = null)
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
				cachedProgram.Parameters["Now", typeof(DateTime)] = eventTimestamp;
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
				parametersForTyping["Now", typeof(DateTime)] = eventTimestamp;

				var parser = reaction.ParsersPool.Rent();
				try
				{
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
				finally
				{
					reaction.ParsersPool.Return(parser);
				}
			}

			// B.2: per-Reaction match cache. Only canonical Programs participate.
			// "Canonical" means the Program reference is reused across many
			// events (currently: ActionEvents via the Reaction's per-ActionId
			// LRU, which mirrors actorHandler.actionCommands). ScriptEvents
			// reuse a Program only for the immediate next consumer via the
			// last-executed-script fast path (`cachedProgram != null` but
			// `cachedProgramIsCanonical == false`); engaging the per-Pattern
			// MatchCache on such transient Programs would add entries that
			// can never re-hit (each EntryId is unique) and would retain the
			// Program in memory unnecessarily, so the match cache is skipped
			// for that path — only the parse skip is harvested.
			MatchCacheEntry cachedEntry = null;
			MatchCacheKey cacheKey = null;
			ReactionMatchCache cache = reaction.MatchCache;
			bool cacheable = cachedProgram != null && cachedProgramIsCanonical;
			if (cacheable)
			{
				string initialVarsSignature = MatchCacheKey.SignatureOf(parameters);
				string programParametersSignature = MatchCacheKey.SignatureOf(scriptAst.Parameters);
				cacheKey = new MatchCacheKey(this, scriptAst, initialVarsSignature, programParametersSignature, exposeDataJson);

				if (cache.TryGet(cacheKey, out cachedEntry))
				{
					if (needsCleanup)
					{
						parametersForTyping.PurgeUserParameters();
						parametersPool.Return(parametersForTyping);
					}
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[Pattern.Match] MatchCache HIT (matched={cachedEntry.Matched})");
#endif
					if (!cachedEntry.Matched) return false;
					foreach (var cap in cachedEntry.Captures)
					{
						parameters[cap.Name, cap.Type] = cap.Value;
					}
					return true;
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
				// Re-throw pattern-AUTHORING errors (a $-capture over a non-capturable
				// argument, or OUT-parameter misuse). Exceptional paths are NOT cached —
				// they represent invalid pattern definitions, not "match/no-match"
				// outcomes over data — and must surface rather than be swallowed as a
				// silent no-match.
				if (ex is PatternCaptureException || ex.Message.Contains("Cannot match an OUT parameter"))
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

				if (cacheable)
				{
					// B.2: snapshot captures into the cache entry BEFORE returning
					// matchResult to the pool (the pool reset clears Name/Type/Value).
					List<CapturedValue> snapshot = new List<CapturedValue>();
					foreach (var param in matchResult)
					{
						snapshot.Add(new CapturedValue(param.Name, param.ParameterType, param.GetValue()));
					}
					cache.Store(cacheKey, new MatchCacheEntry(matched: true, captures: snapshot));

					foreach (var cap in snapshot)
					{
						parameters[cap.Name, cap.Type] = cap.Value;
					}

				}
				else
				{
					// ScriptEvent passthrough — copy captures directly without
					// snapshotting; nothing to cache because the Program
					// instance is fresh and will not be reused.
					foreach (var param in matchResult)
					{
						parameters[param.Name, param.ParameterType] = param.GetValue();
					}
				}
				matchResult.PurgeUserParameters();
				parametersPool.Return(matchResult);
				return true;
			}
#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[Pattern.Match] PatternMatcher DID NOT MATCH");
#endif
			// B.2: negative caching — record the no-match outcome so subsequent
			// identical (Pattern, Program, initialVars, programParams, expose)
			// tuples short-circuit. ScriptEvents skip this step (cacheable=false).
			if (cacheable)
			{
				cache.Store(cacheKey, MatchCacheEntry.NegativeMiss);
			}
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
					// [instance:Type].Member - the instance type does NOT appear
					// literally in the script: the script uses the name of the
					// bound variable (counter.Bump(5)), not the type name
					// (DemoCounter). The type binding is resolved via SymbolTable
					// in the PatternMatcher; here only the member chain is
					// required to appear as a fast prefilter. Previously the
					// TypeName was required as a substring and let through only
					// those scripts whose variable name contained the type name
					// as a substring (case-insensitive) — a false negative
					// that silenced the push loop of Cued reactions when the
					// variable had a different name from the type.
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

			// Add the member name.
			substrings.Add(memberAccess.MemberName);

			// If there is chaining, continue recursively.
			if (memberAccess.NextAccess != null)
			{
				ExtractMemberSubstrings(memberAccess.NextAccess, substrings);
			}
		}

		// Collects the value-capture names this pattern binds — each '$name' stripped of
		// its '$' — i.e. the parameters the matcher would place into a match's
		// CapturedParams. Mirrors PatternMatcher.IsValueCaptureNode: only an untyped
		// '$name' (VariableParameterNode) or a typed '[$name:Type]' (TypedParameterNode
		// whose name starts with '$') binds a value into the results; a wildcard, a free
		// identifier and a 'name:Type' match read no value and contribute nothing.
		// Consumed at definition time so a ForEach can prove its source collection was
		// captured by a prior Seek before the Reaction ever replays an event.
		internal void CollectCaptureNames(ISet<string> names)
		{
			ArgumentNullException.ThrowIfNull(names);

			foreach (var expression in patternAst.Expressions)
			{
				CollectCaptureNamesFromExpression(expression, names);
			}
		}

		private static void CollectCaptureNamesFromExpression(ExpressionNode expression, ISet<string> names)
		{
			if (expression == null) return;

			switch (expression)
			{
				case TypeAccessNode typeAccess:
					CollectCaptureNamesFromMemberAccess(typeAccess.MemberAccess, names);
					break;

				case InstanceAccessNode instanceAccess:
					CollectCaptureNamesFromMemberAccess(instanceAccess.MemberAccess, names);
					break;

				case ConstructorCallNode constructor:
					foreach (var parameter in constructor.Parameters)
						CollectCaptureNamesFromParameter(parameter, names);
					break;

				case AssignmentNode assignment:
					AddCaptureName(assignment.VariableName, names);
					CollectCaptureNamesFromExpression(assignment.Value, names);
					break;

				case PartialPatternNode partial:
					foreach (var pattern in partial.Patterns)
						CollectCaptureNamesFromExpression(pattern, names);
					break;

				case GuardedExpressionNode guarded:
					// A guard ('where' clause) only REFERENCES a capture; the value is bound
					// by the inner expression, so only that is walked.
					CollectCaptureNamesFromExpression(guarded.InnerExpression, names);
					break;

				case AlternativeExpressionNode alternative:
					foreach (var branch in alternative.Branches)
						CollectCaptureNamesFromExpression(branch.Expression, names);
					break;

				case ExposeNode expose:
					CollectCaptureNamesFromParameter(expose.Expression, names);
					break;

				case TellPatternNode tell:
					foreach (var parameter in tell.WithParameters)
						CollectCaptureNamesFromParameter(parameter, names);
					CollectCaptureNamesFromParameter(tell.AddresseeInstanceParameter, names);
					CollectCaptureNamesFromParameter(tell.OnceParameter, names);
					break;

				case TellAckPatternNode ack:
					CollectCaptureNamesFromParameter(ack.AckIdParameter, names);
					CollectCaptureNamesFromParameter(ack.FromAddresseeInstanceParameter, names);
					break;
			}
		}

		private static void CollectCaptureNamesFromMemberAccess(MemberAccessNode memberAccess, ISet<string> names)
		{
			while (memberAccess != null)
			{
				if (memberAccess.Parameters != null)
				{
					foreach (var parameter in memberAccess.Parameters)
						CollectCaptureNamesFromParameter(parameter, names);
				}
				memberAccess = memberAccess.NextAccess;
			}
		}

		private static void CollectCaptureNamesFromParameter(ParameterNode parameter, ISet<string> names)
		{
			switch (parameter)
			{
				case VariableParameterNode variable:
					AddCaptureName(variable.VariableName, names);
					break;

				case TypedParameterNode typed:
					AddCaptureName(typed.ParameterName, names);
					break;

				case NestedCallParameterNode nested:
					CollectCaptureNamesFromExpression(nested.Call, names);
					break;
			}
		}

		// A name only enters the results as a capture when its source token is a '$name'
		// (an untyped '$x' or a typed '$x:Type'); a free identifier or a 'name:Type' match
		// carries no '$' and binds no value, so it is ignored. The stored parameter name
		// drops the '$' (matching PatternMatcher), and a trailing ':Type' suffix — possible
		// on an assignment target like '$x:Type = ...' — is discarded.
		private static void AddCaptureName(string rawName, ISet<string> names)
		{
			if (string.IsNullOrEmpty(rawName) || rawName[0] != '$') return;

			string name = rawName.Substring(1);
			int colon = name.IndexOf(':');
			if (colon >= 0) name = name.Substring(0, colon);
			if (name.Length > 0) names.Add(name);
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
					// Validate the type in the expose expression (if it's a TypedParameterNode).
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
				String.Equals(typeName, "long", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "double", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "decimal", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "bool", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "DateTime", StringComparison.OrdinalIgnoreCase) ||
				String.Equals(typeName, "byte", StringComparison.OrdinalIgnoreCase) ||
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

		// B: definition-time static validation of a Seek pattern's method (and
		// constructor) calls against the receiver TYPE's signature in the actor's
		// DomainLibraries. The pattern names the receiver type directly ([c:Company]
		// or [Company]), so the callable's signature is statically known even though
		// the concrete Action a pattern will match is not. A genuinely impossible
		// pattern (wrong arity, or a typed/literal position incompatible with the
		// callable at that position) can never match at runtime — today that is a
		// silent failure; here it fails fast with a LanguageException.
		//
		// This is INDEPENDENT of the frozen Action global signature (A2): it validates
		// against the library, not against a specific Action, and never resolves
		// runtime symbols nor touches any symbol table.
		//
		// The overriding constraint is NO FALSE POSITIVES: a pattern that matches
		// today must keep defining. Wherever the signature cannot be pinned down
		// unambiguously (receiver type not in the libraries, callable name not found
		// among the scanned candidates, or overload resolution ambiguous) the check
		// is skipped rather than raised.
		private void ValidateMethodSignaturesInPattern(PatternListNode patternAst)
		{
			foreach (var expression in patternAst.Expressions)
			{
				ValidateSignaturesInExpression(expression);
			}
		}

		private void ValidateSignaturesInExpression(ExpressionNode expression)
		{
			switch (expression)
			{
				case InstanceAccessNode instanceAccess:
					if (instanceAccess.MemberAccess != null
						&& libraries.TryGetType(instanceAccess.TypeName, out Type instanceReceiverType))
					{
						ValidateMemberChain(instanceReceiverType, instanceAccess.MemberAccess);
					}
					break;

				case TypeAccessNode typeAccess:
					if (typeAccess.MemberAccess != null
						&& libraries.TryGetType(typeAccess.TypeName, out Type typeReceiverType))
					{
						ValidateMemberChain(typeReceiverType, typeAccess.MemberAccess);
					}
					break;

				case ConstructorCallNode constructor:
					// Constructor arity/type is intentionally NOT validated here: a pattern
					// may name a constructor shape that no declared constructor provides yet
					// still be a legitimate pattern-text fixture, and the matcher already
					// treats such a call as a plain non-match. Only recurse into arguments
					// that are themselves calls-with-receiver.
					foreach (var param in constructor.Parameters)
					{
						ValidateSignaturesInNestedParameter(param);
					}
					break;

				case AssignmentNode assignment:
					ValidateSignaturesInExpression(assignment.Value);
					break;

				case PartialPatternNode partialPattern:
					foreach (var pattern in partialPattern.Patterns)
					{
						ValidateSignaturesInExpression(pattern);
					}
					break;

				case GuardedExpressionNode guarded:
					ValidateSignaturesInExpression(guarded.InnerExpression);
					break;

				case AlternativeExpressionNode alternative:
					foreach (var branch in alternative.Branches)
					{
						ValidateSignaturesInExpression(branch.Expression);
					}
					break;

				// ExposeNode, tell/ack pattern nodes, wildcard and literal expressions
				// carry no receiver-typed callable to validate statically.
			}
		}

		// An argument that is itself a call-with-receiver (foo([_:Derived].goo($x)))
		// is validated as its own call; other argument kinds carry nothing to recurse
		// into.
		private void ValidateSignaturesInNestedParameter(ParameterNode parameter)
		{
			if (parameter is NestedCallParameterNode nested)
			{
				ValidateSignaturesInExpression(nested.Call);
			}
		}

		private void ValidateMemberChain(Type receiverType, MemberAccessNode access)
		{
			Type currentType = receiverType;
			MemberAccessNode current = access;
			while (current != null && currentType != null)
			{
				if (current.Parameters != null)
				{
					// Method-call node: validate arity and per-position types.
					ValidateMethodCall(currentType, current);
					foreach (var param in current.Parameters)
					{
						ValidateSignaturesInNestedParameter(param);
					}
					// Advance to the return type only when a single method signature
					// resolves it unambiguously; otherwise stop walking (an unknown
					// receiver type further down must not raise).
					currentType = ResolveUnambiguousMethodReturnType(currentType, current);
				}
				else
				{
					// Property/field-access node: resolve its member type to continue.
					currentType = ResolvePropertyOrFieldType(currentType, current.MemberName);
				}
				current = current.NextAccess;
			}
		}

		private void ValidateMethodCall(Type receiverType, MemberAccessNode methodNode)
		{
			int argCount = methodNode.Parameters.Count;
			List<Type[]> arityMatchingSignatures = GatherMethodSignatures(receiverType, methodNode.MemberName, argCount);

			// Callable name not found among the scanned candidates. NOT an error: the
			// DSL also reaches methods this static scan does not fully reproduce
			// (extension methods outside the domain libraries, interface members). A
			// method-existence error would risk false positives, so skip.
			if (arityMatchingSignatures == null)
			{
				return;
			}

			if (arityMatchingSignatures.Count == 0)
			{
				throw new LanguageException(
					$"Reaction '{reactionName}', Seek '{seekName}': the pattern calls '{receiverType.Name}.{methodNode.MemberName}' with {argCount} argument(s), but no overload accepts that number of arguments.");
			}

			// Per-position type checks only when the arity-matching candidates collapse
			// to a single expected type vector; ambiguous overloads are left unchecked.
			Type[] expected = SingleExpectedTypeVector(arityMatchingSignatures);
			if (expected == null)
			{
				return;
			}

			for (int i = 0; i < argCount; i++)
			{
				Type constrained = ConstrainedPatternType(methodNode.Parameters[i]);
				if (constrained == null) continue; // wildcard / untyped $var accepts any type.
				if (!IsPatternTypeCompatible(constrained, expected[i]))
				{
					throw new LanguageException(
						$"Reaction '{reactionName}', Seek '{seekName}': in the pattern call '{receiverType.Name}.{methodNode.MemberName}', argument {i + 1} is typed '{constrained.Name}' but the method expects '{expected[i].Name}' at that position.");
				}
			}
		}

		// Returns null when NO callable by this name was found among the candidates
		// (receiver hierarchy, its descendants in the libraries, and domain extension
		// methods over it); an empty list when candidates exist by name but none
		// accepts argCount; otherwise the expected per-position type vector (length
		// argCount, params-expanded) of every candidate that accepts argCount.
		private List<Type[]> GatherMethodSignatures(Type receiverType, string methodName, int argCount)
		{
			bool anyByName = false;
			var vectors = new List<Type[]>();
			foreach (var (method, isExtension) in EnumerateCandidateMethods(receiverType, methodName))
			{
				anyByName = true;
				if (TryBuildExpectedVector(method.GetParameters(), isExtension, argCount, out Type[] vec))
				{
					vectors.Add(vec);
				}
			}
			return anyByName ? vectors : null;
		}

		private Type ResolveUnambiguousMethodReturnType(Type receiverType, MemberAccessNode methodNode)
		{
			int argCount = methodNode.Parameters.Count;
			Type found = null;
			foreach (var (method, isExtension) in EnumerateCandidateMethods(receiverType, methodNode.MemberName))
			{
				if (!TryBuildExpectedVector(method.GetParameters(), isExtension, argCount, out _)) continue;
				Type ret = method.ReturnType;
				if (ret == typeof(void)) return null;      // cannot continue a chain past void.
				if (found == null) found = ret;
				else if (found != ret) return null;        // ambiguous return → stop.
			}
			return found;
		}

		private IEnumerable<(System.Reflection.MethodInfo method, bool isExtension)> EnumerateCandidateMethods(Type receiverType, string methodName)
		{
			const System.Reflection.BindingFlags flags =
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
				System.Reflection.BindingFlags.FlattenHierarchy;

			foreach (var m in receiverType.GetMethods(flags))
			{
				if (m.IsGenericMethodDefinition) continue;
				if (string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
				{
					yield return (m, false);
				}
			}

			// A pattern typed against a base receiver matches a call on a subtype, so a
			// method declared only on a descendant is a legitimate target.
			foreach (Type t in libraries.AllTypes)
			{
				if (t == receiverType) continue;
				if (t.IsGenericTypeDefinition) continue;
				if (!receiverType.IsAssignableFrom(t)) continue;
				foreach (var m in t.GetMethods(flags))
				{
					if (m.IsGenericMethodDefinition) continue;
					if (string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
					{
						yield return (m, false);
					}
				}
			}

			// Extension methods declared over the receiver type in the domain libraries.
			foreach (Type s in libraries.AllTypes)
			{
				if (!(s.IsAbstract && s.IsSealed)) continue; // static class
				foreach (var m in s.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
				{
					if (m.IsGenericMethodDefinition) continue;
					if (!string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)) continue;
					if (!m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) continue;
					var ps = m.GetParameters();
					if (ps.Length == 0) continue;
					if (!ps[0].ParameterType.IsAssignableFrom(receiverType)) continue;
					yield return (m, true);
				}
			}
		}

		// Builds the expected per-position type vector for a candidate against argCount,
		// or returns false when the candidate does not accept that number of arguments.
		// For an extension method the leading 'this' parameter is excluded; a trailing
		// params array is expanded to its element type across the surplus positions.
		private static bool TryBuildExpectedVector(System.Reflection.ParameterInfo[] parameters, bool isExtension, int argCount, out Type[] vector)
		{
			vector = null;
			int start = isExtension ? 1 : 0;
			int effectiveCount = parameters.Length - start;
			if (effectiveCount < 0) return false;

			bool isParams = effectiveCount > 0
				&& parameters[parameters.Length - 1].IsDefined(typeof(ParamArrayAttribute), false);

			if (isParams)
			{
				int fixedCount = effectiveCount - 1;
				if (argCount < fixedCount) return false;
				Type paramsElement = parameters[parameters.Length - 1].ParameterType.GetElementType();
				var vec = new Type[argCount];
				for (int i = 0; i < argCount; i++)
				{
					vec[i] = i < fixedCount ? parameters[start + i].ParameterType : paramsElement;
				}
				vector = vec;
				return true;
			}

			if (argCount != effectiveCount) return false;
			var exact = new Type[argCount];
			for (int i = 0; i < argCount; i++)
			{
				exact[i] = parameters[start + i].ParameterType;
			}
			vector = exact;
			return true;
		}

		// Returns the shared expected type vector when every candidate agrees on it
		// element-wise, or null when the overloads disagree (ambiguous → skip).
		private static Type[] SingleExpectedTypeVector(List<Type[]> vectors)
		{
			Type[] first = vectors[0];
			for (int v = 1; v < vectors.Count; v++)
			{
				Type[] cur = vectors[v];
				if (cur.Length != first.Length) return null;
				for (int i = 0; i < first.Length; i++)
				{
					if (cur[i] != first[i]) return null;
				}
			}
			return first;
		}

		// The static type a pattern position constrains, or null when it accepts any
		// type (a bare wildcard '_' or an untyped '$var'). A typed parameter ('$x:int',
		// '_:Type', 'name:Type') constrains its type; a literal constrains its explicit
		// or literal type.
		private static Type ConstrainedPatternType(ParameterNode parameter)
		{
			switch (parameter)
			{
				case TypedParameterNode typed:
					return IsResolvableType(typed.ParameterType) ? typed.ParameterType : null;
				case LiteralParameterNode literal:
					Type literalType = literal.ExplicitType ?? literal.LiteralType;
					return IsResolvableType(literalType) ? literalType : null;
				default:
					return null;
			}
		}

		private static bool IsResolvableType(Type type)
			=> type != null && type is not UnresolvedDomainType && type is not UnresolvedArrayType;

		// Conservative compatibility between a pattern position type and the callable's
		// parameter type at that position. Errs toward compatible: it rejects only
		// clearly unrelated types (no numeric coercion, no subtype relationship in
		// either direction, neither an enum, interface, nor collection). This mirrors
		// what the runtime matcher would accept while never rejecting a pattern that
		// could match today.
		private static bool IsPatternTypeCompatible(Type patternType, Type expected)
		{
			if (patternType == null || expected == null) return true;
			if (!IsResolvableType(expected)) return true;

			if (patternType == expected) return true;

			// Enums accept name/underlying-value forms the matcher resolves per value.
			if (expected.IsEnum || patternType.IsEnum) return true;

			// An interface position may be satisfied by a concrete argument whose
			// implementation relationship is not visible from these two types alone.
			if (expected.IsInterface || patternType.IsInterface) return true;

			// Collection/array element variance is matched value-by-value at runtime.
			if (IsCollectionLike(expected) || IsCollectionLike(patternType)) return true;

			// Numeric widening/narrowing across the primitive numeric types.
			if (IsNumeric(expected) && IsNumeric(patternType)) return true;

			// A pattern position is an upper bound on the argument the matcher will see,
			// and the parameter type is an upper bound on what the compiled Action
			// passed; accept when the two are related in either direction (which also
			// covers the numeric coercions AreCompatible knows about).
			if (AstExpression.AreCompatible(patternType, expected)) return true;
			if (AstExpression.AreCompatible(expected, patternType)) return true;

			return false;
		}

		private static bool IsCollectionLike(Type type)
		{
			if (type.IsArray) return true;
			if (type.IsGenericType)
			{
				Type def = type.GetGenericTypeDefinition();
				if (def == typeof(List<>) || def == typeof(IEnumerable<>) ||
					def == typeof(IList<>) || def == typeof(ICollection<>))
				{
					return true;
				}
			}
			return false;
		}

		private static bool IsNumeric(Type type)
		{
			return type == typeof(byte) || type == typeof(sbyte) ||
				type == typeof(short) || type == typeof(ushort) ||
				type == typeof(int) || type == typeof(uint) ||
				type == typeof(long) || type == typeof(ulong) ||
				type == typeof(float) || type == typeof(double) || type == typeof(decimal);
		}

		private Type ResolvePropertyOrFieldType(Type receiverType, string memberName)
		{
			const System.Reflection.BindingFlags flags =
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
				System.Reflection.BindingFlags.FlattenHierarchy | System.Reflection.BindingFlags.IgnoreCase;

			Type resolved = TryResolveMemberType(receiverType, memberName, flags);
			if (resolved != null) return resolved;

			// The member may live on a descendant the pattern typed against a base.
			foreach (Type t in libraries.AllTypes)
			{
				if (t == receiverType) continue;
				if (t.IsGenericTypeDefinition) continue;
				if (!receiverType.IsAssignableFrom(t)) continue;
				resolved = TryResolveMemberType(t, memberName, flags);
				if (resolved != null) return resolved;
			}
			return null; // unresolved → stop walking the chain (no error).
		}

		private static Type TryResolveMemberType(Type type, string memberName, System.Reflection.BindingFlags flags)
		{
			try
			{
				var property = type.GetProperty(memberName, flags);
				if (property != null) return property.PropertyType;
			}
			catch (System.Reflection.AmbiguousMatchException)
			{
				// Ambiguous member (e.g. shadowed by 'new') → cannot pin the type; skip.
				return null;
			}
			var field = type.GetField(memberName, flags);
			if (field != null) return field.FieldType;
			return null;
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
