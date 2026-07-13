using Puppeteer.EventSourcing.Interpreter.Libraries;
using Puppeteer.EventSourcing.Interpreter.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Puppeteer.EventSourcing.Follower
{
	internal class PatternMatcher
	{
		private readonly Program program;
		private readonly ActorHandler.ConcurrentParametersPool parametersPool;
		private string exposeDataJson;
		private string scriptText;
		private readonly HashSet<int> usedMemberAccessIndices = new HashSet<int>();
		private readonly HashSet<int> usedMethodCallIndices = new HashSet<int>();
		private readonly HashSet<int> usedTellStatementIndices = new HashSet<int>();
		private readonly HashSet<int> usedTellAckStatementIndices = new HashSet<int>();
		internal PatternMatcher(Program program, ActorHandler.ConcurrentParametersPool parametersPool)
		{
			ArgumentNullException.ThrowIfNull(program);
			ArgumentNullException.ThrowIfNull(parametersPool);

			this.program = program;
			this.parametersPool = parametersPool;
		}

		internal void SetScriptText(string scriptText)
		{
			this.scriptText = scriptText;
		}
		internal Parameters Match(PatternListNode patternAst, Parameters initialCapturedVariables = null, string exposeDataJson = null)
		{
			ArgumentNullException.ThrowIfNull(patternAst);

			// Keep the ExposeData JSON to use it when matching ExposeNode.
			this.exposeDataJson = exposeDataJson;

			// IMPORTANT: clear the previous script's info before preparing the match.
			// This prevents method calls and parameter values from accumulating when
			// the same cached Program is reused across multiple events.
			patternAst.ClearScriptInfo();

			// Prepare the patternAst with the script's information.
			int position = 0;
			program.PreparePatternMatching(patternAst, ref position);

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Script has {patternAst.ScriptMethodCalls.Count} method calls, {patternAst.ScriptMemberAccesses.Count} member accesses");
			foreach (var call in patternAst.ScriptMethodCalls)
			{
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher]   Method: {call.Method?.DeclaringType?.Name}.{call.Method?.Name}, Target: {call.TargetName}");
			}
#endif

			// Rent a Parameters instance from the pool and wrap it as the match's
			// capture bag (MatchParameters owns it as an implementation detail).
			MatchParameters capturedVariables = new MatchParameters(parametersPool.Rent());
			capturedVariables.Reset();

			// If there are parameters captured earlier (from a previous ThenSeek), copy them in.
			int initCount = (initialCapturedVariables != null) ? initialCapturedVariables.Count() : 0;
			if (initialCapturedVariables != null)
			{
				foreach (var param in initialCapturedVariables)
				{
					capturedVariables[param.Name, param.ParameterType] = param.GetValue();
				}
			}

			// Reuse instance HashSets to avoid allocations on every match.
			usedMemberAccessIndices.Clear();
			usedMethodCallIndices.Clear();
			usedTellStatementIndices.Clear();
			usedTellAckStatementIndices.Clear();

			// Track position to ensure sequential matching
			int lastMatchedPosition = -1;

			// Try to match each expression in the pattern.
#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Pattern has {patternAst.Expressions.Count} expressions to match");
#endif
			bool allMatch = true;
			foreach (var expression in patternAst.Expressions)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Trying to match expression: {expression.GetType().Name}");
#endif
				if (!MatchExpression(expression, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition))
				{
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Expression did NOT match");
#endif
					allMatch = false;
					break;
				}
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Expression matched!");
#endif
			}

			if (allMatch)
			{
				// Successful match - return the captured variables. The match result still
				// crosses into MatchTree as a Parameters, so hand back the underlying bag
				// here (boundary tightened in a later step of the read-only refactor).
				return capturedVariables.Underlying;
			}
			else
			{
				// No match - return to the pool.
				capturedVariables.PurgeUserParameters();
				parametersPool.Return(capturedVariables.Underlying);
				return null;
			}
		}
		private bool MatchExpression(ExpressionNode expression, PatternListNode patternAst, MatchParameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			if (expression == null)
				return false;

			switch (expression)
			{
				case TypeAccessNode typeAccess:
					return MatchTypeAccess(typeAccess, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition);

				case InstanceAccessNode instanceAccess:
					return MatchInstanceAccess(instanceAccess, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition);

				case ConstructorCallNode constructor:
					return MatchConstructorCall(constructor, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition);

				case AssignmentNode assignment:
					return MatchAssignment(assignment, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition);

				case PartialPatternNode partialPattern:
					return MatchPartialPattern(partialPattern, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition);

			case ExposeNode exposeNode:
				return MatchExposeNode(exposeNode, patternAst, capturedVariables, ref lastMatchedPosition);

				case GuardedExpressionNode guarded:
					return MatchGuardedExpression(guarded, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition);

				case AlternativeExpressionNode alternative:
					return MatchAlternative(alternative, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition);

				// Plan 7 of the Tell primitive roadmap: tell-shaped patterns
				// dispatch to dedicated routines that compare against the
				// script-side ScriptTellStatement / ScriptTellAckStatement
				// records the pattern AST collected during PreparePatternMatching.
				case TellPatternNode tellPattern:
					return MatchTellPattern(tellPattern, patternAst, capturedVariables, ref lastMatchedPosition);

				case TellAckPatternNode tellAckPattern:
					return MatchTellAckPattern(tellAckPattern, patternAst, capturedVariables, ref lastMatchedPosition);

				default:
					return false;
			}
		}

		// Plan 7 of the Tell primitive roadmap: match an outbound tell pattern
		// against the script's ScriptTellStatement entries. Captures variables
		// (target id, command args, envelope id) into capturedVariables and
		// constraint-matches when a variable is already bound from a prior Seek.
		private bool MatchTellPattern(TellPatternNode pattern, PatternListNode patternAst, MatchParameters capturedVariables, ref int lastMatchedPosition)
		{
			for (int i = 0; i < patternAst.ScriptTellStatements.Count; i++)
			{
				if (usedTellStatementIndices.Contains(i)) continue;
				ScriptTellStatement script = patternAst.ScriptTellStatements[i];
				if (script.Position <= lastMatchedPosition) continue;

				if (!string.Equals(script.MessageName, pattern.MessageName, StringComparison.Ordinal)) continue;
				if (!string.Equals(script.Addressee, pattern.Addressee, StringComparison.Ordinal)) continue;
				if (script.WithValues.Length != pattern.WithParameters.Count) continue;

				// Snapshot capturedVariables in case sub-matches partially fill it
				// before a later mismatch — we restore on rollback. Simple tentative
				// approach: try in order, bail out on mismatch.
				if (pattern.AddresseeInstanceParameter != null)
				{
					if (!MatchOrConstraintParameter(pattern.AddresseeInstanceParameter, script.AddresseeInstanceValue, capturedVariables)) continue;
				}

				bool argsMatch = true;
				for (int a = 0; a < pattern.WithParameters.Count; a++)
				{
					if (!MatchOrConstraintParameter(pattern.WithParameters[a], script.WithValues[a], capturedVariables))
					{
						argsMatch = false;
						break;
					}
				}
				if (!argsMatch) continue;

				if (pattern.OnceParameter != null)
				{
					if (!MatchOrConstraintParameter(pattern.OnceParameter, script.EnvelopeId, capturedVariables)) continue;
				}

				usedTellStatementIndices.Add(i);
				lastMatchedPosition = script.Position;
				return true;
			}
			return false;
		}

		// Plan 7 of the Tell primitive roadmap: match an ack pattern against the
		// script's ScriptTellAckStatement entries.
		private bool MatchTellAckPattern(TellAckPatternNode pattern, PatternListNode patternAst, MatchParameters capturedVariables, ref int lastMatchedPosition)
		{
			for (int i = 0; i < patternAst.ScriptTellAckStatements.Count; i++)
			{
				if (usedTellAckStatementIndices.Contains(i)) continue;
				ScriptTellAckStatement script = patternAst.ScriptTellAckStatements[i];
				if (script.Position <= lastMatchedPosition) continue;

				if (!MatchOrConstraintParameter(pattern.AckIdParameter, script.AckId, capturedVariables)) continue;

				if (pattern.FromAddressee != null)
				{
					if (!string.Equals(script.FromAddressee, pattern.FromAddressee, StringComparison.Ordinal)) continue;
					if (pattern.FromAddresseeInstanceParameter != null
						&& !MatchOrConstraintParameter(pattern.FromAddresseeInstanceParameter, script.FromAddresseeInstanceValue, capturedVariables)) continue;
				}

				usedTellAckStatementIndices.Add(i);
				lastMatchedPosition = script.Position;
				return true;
			}
			return false;
		}

		// Plan 7 of the Tell primitive roadmap: parameter comparison with
		// constraint semantics — a VariableParameterNode that has already been
		// captured at a prior Seek is treated as a constraint (script value
		// must equal the captured value), not a re-capture. This is what makes
		// `OnSeek tell ... id $tid` -> `ThenSeek tell ack $tid` correlate the
		// envelope id across the two seeks.
		private bool MatchOrConstraintParameter(ParameterNode patternParam, object scriptValue, MatchParameters capturedVariables)
		{
			if (patternParam == null) return false;
			switch (patternParam)
			{
				case WildcardParameterNode:
					return true;

				case VariableParameterNode variable:
					string paramName = variable.VariableName.StartsWith("$") ? variable.VariableName.Substring(1) : variable.VariableName;
					if (capturedVariables.ContainsParameter(paramName))
					{
						// Constraint: the value at this position must equal the
						// previously captured value. Used for cross-Seek correlation.
						// Shares ValuesUnifyForConstraint with the domain method-call path
						// so both routes have ONE constraint semantics (promotion-aware).
						object captured = capturedVariables[paramName]?.GetValue();
						return ValuesUnifyForConstraint(captured, scriptValue);
					}
					// First capture.
					if (scriptValue != null)
					{
						capturedVariables[paramName, scriptValue.GetType()] = scriptValue;
					}
					return true;

				case LiteralParameterNode literal:
					if (scriptValue == null && literal.Value == null) return true;
					if (scriptValue == null || literal.Value == null) return false;
					return literal.CompareLiteralsUsingInterpreter(scriptValue);

				case TypedParameterNode typed:
					if (scriptValue == null) return typed.ParameterType.IsClass;
					return AreTypesCompatible(typed.ParameterType, scriptValue.GetType());

				default:
					return false;
			}
		}
		// Cross-Seek correlation constraint: does the value observed at THIS Seek unify
		// with the value already bound to this pattern variable at an EARLIER Seek? A
		// reused pattern variable ($x in foo($x) … endFoo($x)) is one logical variable —
		// an equality JOIN — so the two must carry the same value for the tuple to be a
		// solution; otherwise the candidate is discarded.
		//
		// Equality is PROMOTION-AWARE, comparing in the type already established for the
		// variable at its first capture:
		//   - exact/reference equality first (strings, enums, domain instances, same-typed
		//     numerics) — cheap and covers the common case;
		//   - then the interpreter's OpEqual (CompareValues) for numeric widening across
		//     types (e.g. int captured, long observed) so promotable-equal values unify.
		// A value that is neither equal nor promotable-equal fails the match — which is
		// exactly "the position is not of $x's type nor promotable, or not the same value".
		private bool ValuesUnifyForConstraint(object captured, object observed)
		{
			if (captured == null && observed == null) return true;
			if (captured == null || observed == null) return false;

			// Same type / same value.
			if (captured.Equals(observed) || observed.Equals(captured)) return true;

			Type tc = captured.GetType();
			Type to = observed.GetType();

			// Numeric widening among {byte..long, float, double, decimal}: 10 == 10.0 == 10m.
			// The ONLY numeric equivalence; string<->number is NOT a DSL conversion.
			if (IsNumericType(tc) && IsNumericType(to))
			{
				try { return Convert.ToDecimal(captured) == Convert.ToDecimal(observed); }
				catch { return false; }
			}

			// string <-> enum BY NAME — the single cross-kind implicit conversion the DSL
			// has here ('Lunes' -> EnumDias.Lunes). Directional in the type system
			// (string->enum only), but for unification we always coerce the STRING side to
			// the enum (the permitted direction), so either seek order unifies. Mirrors the
			// DSL boundary coercion (Enum.Parse ignoreCase). enum<->int (underlying) is NOT
			// admitted: 'Lunes' never denotes 0/1, so a numeric never correlates with an enum.
			if (tc.IsEnum && to == typeof(string)) return EnumMatchesName(tc, (string)observed, captured);
			if (to.IsEnum && tc == typeof(string)) return EnumMatchesName(to, (string)captured, observed);

			return false;
		}

		private static bool EnumMatchesName(Type enumType, string name, object enumValue)
		{
			try { return Enum.Parse(enumType, name, ignoreCase: true).Equals(enumValue); }
			catch { return false; }
		}

		// A receiver-type pattern ([_:T] or [name:T]) matches a script call/access when T
		// names a type the receiver "is-a": any type on the chain from the receiver's static
		// type up through its base types. When the receiver's static type was not resolved
		// (unnamed chained expression) it falls back to the matched member's declaring-type
		// chain. Plain name equality at the declaring type is the degenerate case. This is
		// what lets a pattern be written against the receiver's declared type even when the
		// matched member is inherited from an abstract base — i.e. the type that DECLARES the
		// member is a base of the type the script NAMED. Without this, a method declared only
		// on an abstract base would match exclusively through the base's name and never
		// through the (abstract or concrete) subtype the receiver was typed as.
		private static bool ReceiverTypePatternMatches(string patternTypeName, Type receiverType, Type declaringType)
		{
			if (string.IsNullOrEmpty(patternTypeName)) return false;

			for (Type t = receiverType; t != null; t = t.BaseType)
			{
				if (string.Equals(t.Name, patternTypeName, StringComparison.OrdinalIgnoreCase)) return true;
			}

			for (Type t = declaringType; t != null; t = t.BaseType)
			{
				if (string.Equals(t.Name, patternTypeName, StringComparison.OrdinalIgnoreCase)) return true;
			}

			return false;
		}
		private bool MatchTypeAccess(TypeAccessNode typeAccess, PatternListNode patternAst, MatchParameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			if (typeAccess == null) return false;
			if (patternAst == null) return false;

			// [Type].Member(...) matches any access to type Type, regardless of the instance.

			// 1. If the pattern specifies a method with parameters, search ScriptMethodCalls.
			if (typeAccess.MemberAccess != null && typeAccess.MemberAccess.Parameters != null)
			{
				for (int i = 0; i < patternAst.ScriptMethodCalls.Count; i++)
				{
					// Skip if already used
					if (usedMethodCallIndices.Contains(i))
						continue;

					var scriptMethodCall = patternAst.ScriptMethodCalls[i];

					// Skip if position is not greater than last matched position
					if (scriptMethodCall.Position <= lastMatchedPosition)
						continue;

					// Check whether the type matches using the precomputed information.
					if (scriptMethodCall.Method.DeclaringType == null)
						continue;

					if (!ReceiverTypePatternMatches(typeAccess.TypeName, scriptMethodCall.ReceiverType, scriptMethodCall.Method.DeclaringType))
						continue;

					// Check the method against its arguments.
					if (!MatchMethodCall(typeAccess.MemberAccess, scriptMethodCall, capturedVariables, patternAst, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition))
						continue;

					// Mark as used and update position
					usedMethodCallIndices.Add(i);
					lastMatchedPosition = scriptMethodCall.Position;
					return true;
				}
			}
			else
			{
				// Look up ScriptMemberAccesses (properties, fields, or methods without parameter checks).
				for (int i = 0; i < patternAst.ScriptMemberAccesses.Count; i++)
				{
					// Skip if already used
					if (usedMemberAccessIndices.Contains(i))
						continue;

					var scriptAccess = patternAst.ScriptMemberAccesses[i];

					// Skip if position is not greater than last matched position
					if (scriptAccess.Position <= lastMatchedPosition)
						continue;

					// Check whether the type matches using the precomputed information.
					if (scriptAccess.Member == null || scriptAccess.Member.DeclaringType == null)
						continue;

					if (!ReceiverTypePatternMatches(typeAccess.TypeName, scriptAccess.ReceiverType, scriptAccess.Member.DeclaringType))
						continue;

					// 3. Check that the member matches.
					if (typeAccess.MemberAccess == null)
					{
						// The pattern only specifies [Type] with no member access,
						// which matches any access of that type.
						usedMemberAccessIndices.Add(i);
						lastMatchedPosition = scriptAccess.Position;
						return true;
					}

					// Check the member.
					if (!MatchMemberAccess(typeAccess.MemberAccess, scriptAccess.Member, patternAst, capturedVariables))
					{
						continue;
					}

					// Successful match.
					usedMemberAccessIndices.Add(i);
					lastMatchedPosition = scriptAccess.Position;
					return true;
				}
			}

			return false;
		}
		private bool MatchInstanceAccess(InstanceAccessNode instanceAccess, PatternListNode patternAst, MatchParameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			if (instanceAccess == null) return false;
			if (patternAst == null) return false;

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[MatchInstanceAccess] Looking for [{instanceAccess.InstanceName}:{instanceAccess.TypeName}].{instanceAccess.MemberAccess?.MemberName}");
			System.Diagnostics.Debug.WriteLine($"[MatchInstanceAccess] Script has {patternAst.ScriptMethodCalls.Count} method calls");
#endif

			// The pattern is: [instanceName:TypeName].Member(...)
			// We need to find a matching access in the script.

			// 1. If the pattern specifies a method with parameters, search ScriptMethodCalls.
			if (instanceAccess.MemberAccess != null && instanceAccess.MemberAccess.Parameters != null)
			{
				for (int i = 0; i < patternAst.ScriptMethodCalls.Count; i++)
				{
					var scriptMethodCall = patternAst.ScriptMethodCalls[i];
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchInstanceAccess]   Checking method call #{i}: {scriptMethodCall.Method?.DeclaringType?.Name}.{scriptMethodCall.Method?.Name} on target '{scriptMethodCall.TargetName}'");
#endif

					// Skip if already used
					if (usedMethodCallIndices.Contains(i))
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchInstanceAccess]     SKIP: already used");
#endif
						continue;
					}

					// Skip if position is not greater than last matched position
					if (scriptMethodCall.Position <= lastMatchedPosition)
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchInstanceAccess]     SKIP: position {scriptMethodCall.Position} <= {lastMatchedPosition}");
#endif
						continue;
					}

					// Check whether the type matches using the precomputed information.
					if (scriptMethodCall.Method.DeclaringType == null)
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchInstanceAccess]     SKIP: DeclaringType is null");
#endif
						continue;
					}

					if (!ReceiverTypePatternMatches(instanceAccess.TypeName, scriptMethodCall.ReceiverType, scriptMethodCall.Method.DeclaringType))
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchInstanceAccess]     SKIP: type mismatch {scriptMethodCall.Method.DeclaringType.Name} != {instanceAccess.TypeName}");
#endif
						continue;
					}

#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchInstanceAccess]     Type matches! Checking instance name and method...");
#endif

					// If the pattern specifies an instance name (neither null nor "_"), check
					// whether it is a previously captured variable (free pattern).
					if (!string.IsNullOrEmpty(instanceAccess.InstanceName) && instanceAccess.InstanceName != "_")
					{
						// Check whether instanceName was captured earlier.
						if (capturedVariables.ContainsParameter(instanceAccess.InstanceName))
						{
							// It is a captured variable; ensure the target matches.
							var capturedParam = capturedVariables[instanceAccess.InstanceName];
							string capturedVarName = capturedParam?.GetValue() as string;
							if (capturedVarName != null && !string.Equals(scriptMethodCall.TargetName, capturedVarName, StringComparison.OrdinalIgnoreCase))
							{
								continue; // Does not match; keep searching.
							}
						}
						else
						{
							// Not a captured variable; capture the variable name from the script.
							capturedVariables[instanceAccess.InstanceName, typeof(string)] = scriptMethodCall.TargetName;
						}
					}

					// Check the method against its arguments.
					if (!MatchMethodCall(instanceAccess.MemberAccess, scriptMethodCall, capturedVariables, patternAst, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition))
						continue;

					// Mark as used and update position
					usedMethodCallIndices.Add(i);
					lastMatchedPosition = scriptMethodCall.Position;
					return true;
				}
			}
			else
			{
				// Look up ScriptMemberAccesses (properties, fields, or methods without parameter checks).
				for (int i = 0; i < patternAst.ScriptMemberAccesses.Count; i++)
				{
					// Skip if already used
					if (usedMemberAccessIndices.Contains(i))
						continue;

					var scriptAccess = patternAst.ScriptMemberAccesses[i];

					// Skip if position is not greater than last matched position
					if (scriptAccess.Position <= lastMatchedPosition)
						continue;

					// Check whether the type matches using the precomputed information.
					if (scriptAccess.Member == null || scriptAccess.Member.DeclaringType == null)
						continue;

					if (!ReceiverTypePatternMatches(instanceAccess.TypeName, scriptAccess.ReceiverType, scriptAccess.Member.DeclaringType))
						continue;

					// If the pattern specifies an instance name (neither null nor "_"), verify it matches.
					if (!string.IsNullOrEmpty(instanceAccess.InstanceName) &&
						instanceAccess.InstanceName != "_" &&
						scriptAccess.TargetName != instanceAccess.InstanceName)
					{
						continue;
					}

					// 3. Check that the member matches.
					if (instanceAccess.MemberAccess == null)
					{
						// The pattern only specifies [instance:Type] with no member access,
						// which matches any access of that type.
						usedMemberAccessIndices.Add(i);
						lastMatchedPosition = scriptAccess.Position;
						return true;
					}

					// Check the first level of access.
					if (!MatchMemberAccess(instanceAccess.MemberAccess, scriptAccess.Member, patternAst, capturedVariables))
					{
						continue;
					}

					// Successful match.
					usedMemberAccessIndices.Add(i);
					lastMatchedPosition = scriptAccess.Position;
					return true;
				}
			}

			return false;
		}
		private bool MatchMemberAccess(MemberAccessNode memberAccess, System.Reflection.MemberInfo scriptMember, PatternListNode patternAst, MatchParameters capturedVariables)
		{
			if (memberAccess == null) return false;
			if (scriptMember == null) return false;

			// Check that the member name matches.
			if (memberAccess.MemberName != scriptMember.Name)
				return false;

			// If the member is a method, validate the parameters.
			if (memberAccess.Parameters != null)
			{
				// Method case.
				if (scriptMember is not System.Reflection.MethodInfo methodInfo)
					return false;

				// Check the number of parameters.
				var methodParams = methodInfo.GetParameters();
				if (memberAccess.Parameters.Count != methodParams.Length)
					return false;

				// Check each parameter.
				for (int i = 0; i < memberAccess.Parameters.Count; i++)
				{
					var patternParam = memberAccess.Parameters[i];
					var scriptParam = methodParams[i];

					if (!MatchParameter(patternParam, scriptParam.ParameterType, capturedVariables))
						return false;
				}
			}
			else
			{
				// Property or field case.
				if (scriptMember is not System.Reflection.PropertyInfo &&
					scriptMember is not System.Reflection.FieldInfo)
					return false;
			}

			// If there is chaining, recurse.
			if (memberAccess.NextAccess != null)
			{
				return false;
			}

			return true;
		}
		private bool MatchParameter(ParameterNode parameterNode, Type scriptParameterType, MatchParameters capturedVariables)
		{
			if (parameterNode == null) return false;

			switch (parameterNode)
			{
				case WildcardParameterNode:
					// Wildcard matches any parameter.
					return true;

				case VariableParameterNode variable:
					// Capture the value into capturedVariables.
					// For now, just mark it as matching.
					return true;

				case LiteralParameterNode literal:
					// Check that the literal's type matches the expected one.
					if (literal.ExplicitType != null)
					{
						return AreTypesCompatible(literal.ExplicitType, scriptParameterType);
					}
					return AreTypesCompatible(literal.LiteralType, scriptParameterType);

				case TypedParameterNode typed:
					// Check that the type matches (with array support).
					return AreTypesCompatible(typed.ParameterType, scriptParameterType);

				default:
					return false;
			}
		}
		private bool MatchConstructorCall(ConstructorCallNode constructor, PatternListNode patternAst, MatchParameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			if (constructor == null) return false;
			if (patternAst == null) return false;

			// 1. Search ScriptConstructorCalls for a matching call.
			foreach (var scriptCall in patternAst.ScriptConstructorCalls)
			{
				// Check whether the type matches using the precomputed information.
				if (scriptCall.Type == null)
					continue;

				if (!scriptCall.Type.Name.Equals(constructor.TypeName, StringComparison.OrdinalIgnoreCase))
					continue;

				// Check the number of parameters.
				if (constructor.Parameters.Count != scriptCall.ArgumentValues.Count)
					continue;

				// Match each argument by VALUE through the same path as method calls
				// (MatchParameterValue), so a constructor position captures/constrains/compares
				// $x, honors literals, and selects ':T' by the RESOLVED overload's declared
				// parameter type (from scriptCall.Constructor). Previously this used a type-only
				// helper that never captured, so foo(new C($x)) / C($x) could not correlate.
				System.Reflection.ParameterInfo[] ctorParams = scriptCall.Constructor?.GetParameters();

				bool allParametersMatch = true;
				for (int i = 0; i < constructor.Parameters.Count; i++)
				{
					var patternParam = constructor.Parameters[i];
					object scriptArgValue = scriptCall.ArgumentValues[i];
					// Declared parameter type of the resolved overload at this position. Null for the
					// params tail (i beyond the fixed parameters) — MatchParameterValue then falls back
					// to the observed value's type, which is the sound behavior for a params element.
					Type targetParameterType = (ctorParams != null && i < ctorParams.Length)
						? ctorParams[i].ParameterType
						: null;

					if (!MatchParameterValue(patternParam, scriptArgValue, capturedVariables, targetParameterType))
					{
						allParametersMatch = false;
						break;
					}
				}

				if (allParametersMatch)
				{
					return true;
				}
			}

			return false;
		}
		private bool MatchAssignment(AssignmentNode assignment, PatternListNode patternAst, MatchParameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			string varName = assignment.VariableName;
			Type requiredType = null;
			string actualVarName = varName;

			if (varName.Contains(':'))
			{
				string[] parts = varName.Split(':');
				actualVarName = parts[0];
				string typeName = parts[1];

				requiredType = ResolveType(typeName);
			}

			foreach (var scriptAssignment in patternAst.ScriptAssignments)
			{
				if (requiredType != null)
				{
					if (scriptAssignment.TargetType != requiredType &&
						!requiredType.IsAssignableFrom(scriptAssignment.TargetType))
					{
						continue;
					}
				}

				if (assignment.Value is ConstructorCallNode constructorPattern)
				{
					// RHS is a constructor: match type + arity + ARGUMENTS (capturing/constraining
					// $x, honoring literals, selecting ':T' by the resolved overload) via the shared
					// constructor path — not merely the type name. This is what lets
					// `o = ClaseObjeto($x)` correlate $x. The LHS type constraint (requiredType) was
					// already applied above.
					if (!MatchConstructorCall(constructorPattern, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition))
					{
						continue;
					}
				}
				else if (!MatchAssignmentValue(assignment.Value, scriptAssignment.Value))
				{
					continue;
				}

				// Handle the left-hand side (the variable name).
				if (!string.IsNullOrEmpty(actualVarName) && actualVarName != "_" && !actualVarName.StartsWith("$"))
				{
					// Free pattern.
					if (capturedVariables.ContainsParameter(actualVarName))
					{
						// The variable was already captured, so this is a verification.
						var capturedParam = capturedVariables[actualVarName];
						string capturedIdentifier = capturedParam?.GetValue() as string;
						if (!string.Equals(capturedIdentifier, scriptAssignment.TargetName, StringComparison.OrdinalIgnoreCase))
						{
							continue; // The script's variable name does not match the one we captured before.
						}
					}
					else
					{
						// The variable has not been captured yet; capture it now.
						capturedVariables[actualVarName, typeof(string)] = scriptAssignment.TargetName;
					}
				}
				else if (actualVarName.StartsWith("$"))
				{
					// LHS $y: capture/constrain the ASSIGNED VALUE, not the variable name. Only a
					// literal or @parameter RHS records a real value; a constructor/method/variable
					// RHS records a TypedValuePlaceholder (the assigned object has no journaled
					// identity), so $y does NOT bind there — consistent with the $-capture contract
					// (no value to bind). When bindable it correlates like any other $x.
					object assignedValue = scriptAssignment.Value;
					if (assignedValue != null && !(assignedValue is TypedValuePlaceholder))
					{
						string lhsCapture = actualVarName.Substring(1);
						if (capturedVariables.ContainsParameter(lhsCapture))
						{
							if (!ValuesUnifyForConstraint(capturedVariables[lhsCapture]?.GetValue(), assignedValue))
							{
								continue;
							}
						}
						else
						{
							capturedVariables[lhsCapture, assignedValue.GetType()] = assignedValue;
						}
					}
				}

				return true;
			}

			return false;
		}

		private bool MatchAssignmentValue(ExpressionNode patternValue, object scriptValue)
		{
			if (patternValue is LiteralExpressionNode literalExpr)
			{
				if (scriptValue is TypedValuePlaceholder)
					return false;

				object patternLiteralValue = literalExpr.Literal.Value;
				if (scriptValue == null && patternLiteralValue == null)
					return true;
				if (scriptValue == null || patternLiteralValue == null)
					return false;
				return patternLiteralValue.Equals(scriptValue);
			}
			else if (patternValue is WildcardExpressionNode)
			{
				return true;
			}

			return false;
		}

		private Type ResolveType(string typeName)
		{
			switch (typeName.ToLowerInvariant())
			{
				case "int":
					return typeof(int);
				case "string":
					return typeof(string);
				case "bool":
					return typeof(bool);
				case "decimal":
					return typeof(decimal);
				case "double":
					return typeof(double);
				case "datetime":
					return typeof(DateTime);
				default:
					return new UnresolvedDomainType(typeName);
			}
		}
		private bool MatchPartialPattern(PartialPatternNode partialPattern, PatternListNode patternAst, MatchParameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			if (partialPattern.Patterns.Count == 0)
				return false;

			int currentScriptIndex = 0;
			// PERF (Tier 3): ScriptMemberAccesses is already an IReadOnlyList; this
			// loop only reads it by index, so the previous .ToList() copy was a pure
			// per-call allocation with no purpose. Iterate the list directly.
			var scriptAccesses = patternAst.ScriptMemberAccesses;

			for (int patternIndex = 0; patternIndex < partialPattern.Patterns.Count; patternIndex++)
			{
				var currentPattern = partialPattern.Patterns[patternIndex];
				bool found = false;

				if (currentPattern is InstanceAccessNode instanceAccess)
				{
					for (int i = currentScriptIndex; i < scriptAccesses.Count; i++)
					{
						var scriptAccess = scriptAccesses[i];

						if (scriptAccess.Member == null || scriptAccess.Member.DeclaringType == null)
							continue;

						if (!scriptAccess.Member.DeclaringType.Name.Equals(instanceAccess.TypeName, StringComparison.OrdinalIgnoreCase))
							continue;

						if (instanceAccess.MemberAccess != null)
						{
							if (!scriptAccess.MemberName.Equals(instanceAccess.MemberAccess.MemberName, StringComparison.OrdinalIgnoreCase))
								continue;
						}

						found = true;
						currentScriptIndex = i + 1;
						break;
					}
				}
				else if (currentPattern is TypeAccessNode typeAccess)
				{
					for (int i = currentScriptIndex; i < scriptAccesses.Count; i++)
					{
						var scriptAccess = scriptAccesses[i];

						if (scriptAccess.Member == null || scriptAccess.Member.DeclaringType == null)
							continue;

						if (!scriptAccess.Member.DeclaringType.Name.Equals(typeAccess.TypeName, StringComparison.OrdinalIgnoreCase))
							continue;

						if (typeAccess.MemberAccess != null)
						{
							if (!scriptAccess.MemberName.Equals(typeAccess.MemberAccess.MemberName, StringComparison.OrdinalIgnoreCase))
								continue;
						}

						found = true;
						currentScriptIndex = i + 1;
						break;
					}
				}

				if (!found)
					return false;
			}

			return true;
		}
		private bool MatchMethodCall(MemberAccessNode memberAccess, ScriptMethodCall scriptMethodCall, MatchParameters capturedVariables,
			PatternListNode patternAst, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			if (memberAccess == null) return false;
			if (scriptMethodCall == null) return false;

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[MatchMethodCall] Pattern method: {memberAccess.MemberName}, Script method: {scriptMethodCall.Method.Name}");
#endif

			if (memberAccess.MemberName != scriptMethodCall.Method.Name)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchMethodCall]   FAIL: Method name mismatch");
#endif
				return false;
			}

			if (memberAccess.Parameters == null)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchMethodCall]   FAIL: Pattern has no parameters");
#endif
				return false;
			}

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[MatchMethodCall] Pattern params: {memberAccess.Parameters.Count}, Script args: {scriptMethodCall.Arguments.Count}");
#endif

			if (memberAccess.Parameters.Count != scriptMethodCall.Arguments.Count)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchMethodCall]   FAIL: Parameter count mismatch");
#endif
				return false;
			}

			// The matched method's declared parameter types — used so a $-capture over a
			// script literal is typed from the SIGNATURE (what the method received), not
			// from the literal's naive parse type. See the capture site in MatchParameterValue.
			System.Reflection.ParameterInfo[] methodParams = scriptMethodCall.Method?.GetParameters();

			for (int i = 0; i < memberAccess.Parameters.Count; i++)
			{
				var patternParam = memberAccess.Parameters[i];
				var scriptArgument = scriptMethodCall.Arguments[i];

#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchMethodCall] Checking param {i}: Pattern={patternParam.GetType().Name}, Script={scriptArgument?.GetType().Name ?? "NULL"}");
#endif

				bool argMatched;
				if (patternParam is NestedCallParameterNode nestedCall)
				{
					// Argument that is itself a call-with-receiver: we match it against
					// the registered ScriptMethodCalls (the inner call was registered by
					// the recursion in DottedId/ChainedDotAccess.PreparePatternMatching),
					// capturing its inner $vars. We do not look at scriptArgument (placeholder).
					argMatched = MatchExpression(nestedCall.Call, patternAst, capturedVariables,
						usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition);
				}
				else
				{
					Type targetParameterType = (methodParams != null && i < methodParams.Length)
						? methodParams[i].ParameterType
						: null;
					argMatched = MatchParameterValue(patternParam, scriptArgument, capturedVariables, targetParameterType);
				}

				if (!argMatched)
				{
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchMethodCall]   FAIL: Parameter {i} did not match");
#endif
					return false;
				}
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchMethodCall]   OK: Parameter {i} matched");
#endif
			}

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[MatchMethodCall] SUCCESS: All checks passed");
#endif
			return true;
		}
		// Coerce a captured literal value to the TYPE the matched method declares for its
		// position, mirroring what the interpreter's call boundary does when it actually
		// invokes the method (numeric widening + string/int -> enum). Returns the coerced
		// value ONLY when the conversion yields the target type EXACTLY; otherwise the raw
		// value is returned unchanged. Two consequences of the exact-type gate:
		//   - a subclass value keeps its concrete runtime type (no lossy up-cast), and
		//   - the already-correctly-typed @parameter path (value type == target) is a no-op,
		//     so this never perturbs a parametrized command's captures.
		// Best-effort: any conversion failure falls back to the raw value.
		private static object CoerceCapturedValueToParameterType(object value, Type targetType)
		{
			if (value == null || targetType == null || targetType == typeof(object)) return value;

			Type actual = value.GetType();
			if (actual == targetType) return value;

			try
			{
				if (targetType.IsEnum)
				{
					// The DSL admits an enum argument as its string name ('Store') or as its
					// underlying numeric value; both resolve to the enum constant here.
					if (value is string enumName)
					{
						return Enum.Parse(targetType, enumName, ignoreCase: true);
					}
					if (actual.IsPrimitive)
					{
						return Enum.ToObject(targetType, value);
					}
					return value;
				}

				// Numeric widening only (int -> decimal/double, double -> decimal, ...).
				// Restricted to numeric<->numeric so we never silently turn, say, an int
				// into a string. Convert.ChangeType handles boxed numerics correctly (unlike
				// a direct unboxing cast, which throws for int-boxed-as-double).
				if (IsNumericType(actual) && IsNumericType(targetType))
				{
					object coerced = Convert.ChangeType(value, targetType);
					if (coerced != null && coerced.GetType() == targetType)
					{
						return coerced;
					}
				}
			}
			catch
			{
				// Best-effort: leave the raw value in place on any conversion failure.
			}

			return value;
		}

		private static bool IsNumericType(Type t)
		{
			return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
				|| t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort)
				|| t == typeof(float) || t == typeof(double) || t == typeof(decimal);
		}

		private bool MatchParameterValue(ParameterNode parameterNode, object scriptValue, MatchParameters capturedVariables, Type targetParameterType = null)
		{
			if (parameterNode == null) return false;

			// Reject attempts to match an OUT parameter by literal value.
			if (scriptValue is OutParameterMarker outMarker)
			{
				if (parameterNode is LiteralParameterNode)
				{
					throw new LanguageException($"Cannot match an OUT parameter '{outMarker.ParameterName}' by its literal value. OUT parameters can only be matched by their identifier (e.g. {outMarker.ParameterName}:{outMarker.ValueType.Name}) or by a wildcard (_).");
				}
				// Wildcards and TypedParameterNodes can match OUT parameters.
				// Convert OutParameterMarker into a TypedValuePlaceholder for the normal flow.
				scriptValue = new TypedValuePlaceholder(outMarker.ValueType, outMarker.ParameterName);
			}

			switch (parameterNode)
			{
				case WildcardParameterNode:
					return true;

				case VariableParameterNode variable:
					// ==================================================================
					// CAPTURE CONTRACT — when a pattern variable ($x) binds a value, and
					// when it cannot. This is the single authoritative statement of the
					// rule; PatternCaptureContractTests enumerates every case below.
					//
					// A $-capture binds the runtime VALUE the matcher can read FROM THE
					// JOURNAL. Reactions never compute: the matcher does not re-run the
					// observed command, so it can only see a value when the observed
					// argument carries one intrinsically. The observed argument (as reduced
					// by DotAccess.GetArgumentValues) is exactly one of:
					//
					//   CAPTURABLE (a concrete value reaches here):
					//     1. a LITERAL written directly at the call position (`Sale(..., 'x')`)
					//        — captured, and typed from the method SIGNATURE (see
					//        CoerceCapturedValueToParameterType), not the literal's parse type.
					//     2. an @PARAMETER reference (id.IsParameter) — its value travels in
					//        the invocation row and resolves to a concrete value.
					//     3. an EXPOSE label (`expose x 'l'`, capture over 'l') — handled by
					//        the expose path (CaptureExposeValue), not here.
					//
					//   NOT CAPTURABLE (arrives as a TypedValuePlaceholder — no journaled value):
					//     4. a GLOBAL/LOCAL VARIABLE (`Sale(..., x)` where x is not a param):
					//        placeholder carrying the identifier NAME. Its value existed only
					//        in the (now gone) command runtime; the matcher cannot recover it.
					//     5. an OPERATED / DERIVED expression (`x + 'A'`, `foo(bar())`):
					//        placeholder with no name. Same reason.
					//     (4) and (5) are AUTHORING errors -> hard PatternCaptureException. The
					//     author must `expose` the value and capture the label instead.
					//
					//   NO-MATCH (fail gracefully, NOT a hard error):
					//     6. a genuine NULL value — a null is data, not an authoring mistake
					//        (a648232 contract); a later event may carry a value.
					//     7. a DECLARED PARAMETER whose value failed to resolve at this moment
					//        (name is in the action's parameter signature). A legitimate
					//        capturable position with a transiently-absent value — a
					//        framework/data resolution issue, distinguished from a variable via
					//        TypedValuePlaceholder.IsDeclaredParameter. Failing (not throwing)
					//        keeps a live push loop alive; an ordered batch replay (where the
					//        value resolves) still binds and fires. §4.3 resilience preserved.
					// ==================================================================
					if (scriptValue != null && !(scriptValue is TypedValuePlaceholder))
					{
						string paramName = variable.VariableName.StartsWith("$") ? variable.VariableName.Substring(1) : variable.VariableName;

						// Capture with the TYPE the matched method actually declares for this
						// position, not the raw runtime type of the observed argument. A script
						// LITERAL parses to a naive type (e.g. `250` -> int, `'Store'` -> string)
						// that can differ from the method's declared parameter type (`decimal
						// amount`, `SaleChannel channel`): the interpreter coerces it at the call
						// boundary, so the value the method truly received is a decimal / an enum.
						// Without this, a literal-argument command captures with the wrong type
						// while the same command issued with a typed @parameter captures correctly
						// — the two paths diverge, and the downstream tell journals the wrong
						// signature / a different content hash.
						object captureValue = CoerceCapturedValueToParameterType(scriptValue, targetParameterType);

						// CROSS-SEEK CORRELATION: a $x already bound at a PRIOR Seek is a
						// CONSTRAINT, not a re-capture. The reused pattern variable is a single
						// unified logical variable (a value-equality JOIN across seeks): the value
						// observed here must unify with the one captured earlier, or this candidate
						// is discarded. This is the same rule the Tell paths already apply via
						// MatchOrConstraintParameter — consolidated here (ValuesUnifyForConstraint)
						// so domain method-call arguments correlate too. Without it the variable was
						// silently re-bound and every close matched every open regardless of key
						// (foo($x) … endFoo($x) failed to correlate).
						if (capturedVariables.ContainsParameter(paramName))
						{
							object priorValue = capturedVariables[paramName]?.GetValue();
							return ValuesUnifyForConstraint(priorValue, captureValue);
						}

						capturedVariables[paramName, captureValue.GetType()] = captureValue;
						return true;
					}

					if (scriptValue is TypedValuePlaceholder notBindable)
					{
						// A DECLARED PARAMETER whose value merely failed to resolve at this
						// moment is a legitimate (capturable) position — not an authoring error.
						// Fail the match gracefully (the value may resolve in an ordered batch
						// replay); the underlying resolution failure is recorded elsewhere via
						// LastResolutionError. This is the §4.3 resilience: an unresolved observed
						// @parameter must NOT poison a live push loop.
						if (notBindable.IsDeclaredParameter)
						{
							return false;
						}

						// Cases 4 & 5: a genuine global/local VARIABLE or an OPERATED expression.
						// There is no journaled value to bind — HARD ERROR (authoring).
						string captureName = variable.VariableName.StartsWith("$") ? variable.VariableName.Substring(1) : variable.VariableName;
						string observed = notBindable.VariableName != null
							? $"the global/local variable '{notBindable.VariableName}'"
							: "an operated or derived expression";
						string exposeName = notBindable.VariableName ?? "value";
						throw new PatternCaptureException(
							$"Pattern capture '${captureName}' has no value to bind: the observed argument is {observed}, "
							+ "not a literal written at the call position nor a parameter (@name), so no value was captured. "
							+ "The matcher reads captured values from the journal and never re-runs the command "
							+ "(Reactions never compute), so a variable's runtime value — which existed only during the "
							+ "original command — is gone by match time. "
							+ $"Expose it in the command (e.g. `expose {exposeName} '{exposeName}';`) and capture over the "
							+ "exposed label instead. (If this argument is a parameter whose value failed to resolve, that is "
							+ "a resolution issue, not a capture over a variable.)");
					}

					// Case 6: a genuine null value. A null is data, not an authoring mistake;
					// a $-capture cannot bind null, so the match simply FAILS here (it does not
					// throw, and it does not fabricate an "incomplete complete match" that would
					// later blow up in the Causation body). Keeping this a no-match lets a live
					// Cue/Job reaction stay alive and lets a later event carry a real value.
					// This is the a648232 contract.
					return false;

				case LiteralParameterNode literal:
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchParameterValue] LiteralParameterNode: pattern value={literal.Value} ({literal.Value?.GetType().Name}), script value={scriptValue} ({scriptValue?.GetType().Name})");
#endif
					if (scriptValue is TypedValuePlaceholder)
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchParameterValue]   FAIL: scriptValue is TypedValuePlaceholder");
#endif
						return false;
					}
					if (scriptValue == null && literal.Value == null)
						return true;
					if (scriptValue == null || literal.Value == null)
						return false;

					// Arrays/collections: convert scriptValue to a compatible type before comparing.
					object scriptValueToCompare = scriptValue;
					if (literal.Value is Array literalArray)
					{
						Type literalElementType = GetElementType(literal.LiteralType);
						Type scriptElementType = GetElementType(scriptValue.GetType());

#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchParameterValue]   Array detected: literalElement={literalElementType?.Name}, scriptElement={scriptElementType?.Name}");
#endif

						if (literalElementType != null && scriptElementType != null && AreTypesCompatible(literalElementType, scriptElementType))
						{
							// Both are array/collection types with compatible elements.
							// Convert scriptValue (List<T>) to T[] for comparison.
#if DEBUG
							System.Diagnostics.Debug.WriteLine($"[MatchParameterValue]   Calling TypeConversion.ImplicitCast({scriptValue.GetType().Name} → {literal.LiteralType.Name})");
#endif
							scriptValueToCompare = TypeConversion.ImplicitCast(scriptValue, literal.LiteralType);
#if DEBUG
							System.Diagnostics.Debug.WriteLine($"[MatchParameterValue]   Result type after cast: {scriptValueToCompare?.GetType().Name}");
#endif
						}
					}

					// Use the same equality logic as the interpreter (OpEqual).
					bool equals = literal.CompareLiteralsUsingInterpreter(scriptValueToCompare);
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchParameterValue]   CompareLiteralsUsingInterpreter = {equals}");
#endif
					return equals;

				case TypedParameterNode typed:
					if (scriptValue is TypedValuePlaceholder placeholder)
					{
						bool typeMatches = AreTypesCompatible(typed.ParameterType, placeholder.ValueType);

						if (typeMatches && !string.IsNullOrEmpty(typed.ParameterName) && typed.ParameterName != "_" && !typed.ParameterName.StartsWith("$"))
						{
							// Free pattern nested inside a parameter.
							if (capturedVariables.ContainsParameter(typed.ParameterName))
							{
								// The variable was already captured, so this is a verification.
								var capturedParam = capturedVariables[typed.ParameterName];
								string capturedIdentifier = capturedParam?.GetValue() as string;
								if (!string.Equals(capturedIdentifier, placeholder.VariableName, StringComparison.OrdinalIgnoreCase))
								{
									return false; // The script's variable name does not match.
								}
							}
							else
							{
								// The variable has not been captured yet; capture it now.
								if (!string.IsNullOrEmpty(placeholder.VariableName))
								{
									capturedVariables[typed.ParameterName, typeof(string)] = placeholder.VariableName;
								}
							}
						}
						return typeMatches;
					}
					if (scriptValue == null)
						return typed.ParameterType.IsClass;

					// ':T' is an OVERLOAD/SIGNATURE selector, not a value-type test: it must
					// match the RESOLVED action's declared parameter type at this position
					// (targetParameterType, sourced exactly from ActionId -> cached program ->
					// MethodInfo.GetParameters()), NOT the observed value's runtime type. So
					// foo($x:int, _:decimal) selects the foo(int, decimal) overload — 'decimal'
					// means the declared param is decimal, not "a value that widens to decimal".
					// Fallback: when the signature is unknown (targetParameterType == null, e.g.
					// a ScriptEvent that carries no resolved method) use the observed value type.
					bool typeOk = targetParameterType != null
						? AreTypesCompatible(typed.ParameterType, targetParameterType)
						: (AreTypesCompatible(typed.ParameterType, scriptValue.GetType()) ||
						   AstExpression.AreCompatible(typed.ParameterType, scriptValue.GetType()));
					if (!typeOk) return false;

					// A TYPED capture ($x:T) is still a capture/constraint on $x — the :T
					// only adds a type filter (checked just above). Correlate the value
					// exactly like the untyped $x path (VariableParameterNode): capture on
					// first sight, constrain (promotion-aware, ValuesUnifyForConstraint) on
					// reuse from a prior seek. Without this a type hint silently disabled
					// correlation (the close matched every open). Names that are not
					// $-captures ('_', a bare instance name) bind no value.
					if (!string.IsNullOrEmpty(typed.ParameterName) && typed.ParameterName.StartsWith("$"))
					{
						string typedParamName = typed.ParameterName.Substring(1);
						object typedCaptureValue = CoerceCapturedValueToParameterType(scriptValue, targetParameterType);
						if (capturedVariables.ContainsParameter(typedParamName))
						{
							object priorTyped = capturedVariables[typedParamName]?.GetValue();
							return ValuesUnifyForConstraint(priorTyped, typedCaptureValue);
						}
						capturedVariables[typedParamName, typedCaptureValue.GetType()] = typedCaptureValue;
					}
					return true;

				default:
					return false;
			}
		}

		private bool AreTypesCompatible(Type patternType, Type scriptType)
		{
			if (patternType == null || scriptType == null)
				return false;

			// Exact match
			if (patternType == scriptType)
				return true;

			// Check if script type is assignable to pattern type
			if (patternType.IsAssignableFrom(scriptType))
				return true;

			// Special handling for arrays and collections
			Type patternElementType = GetElementType(patternType);
			Type scriptElementType = GetElementType(scriptType);

			if (patternElementType != null && scriptElementType != null)
			{
				// Both are array/collection types - compare element types recursively
				// Use AreCompatible to handle numeric coercions (int->decimal, int->double, etc.)
				return AreTypesCompatible(patternElementType, scriptElementType);
			}

			// Use AstExpression.AreCompatible for final compatibility check
			// This handles implicit conversions (int->decimal, int->double, List<T>->IEnumerable<T>, etc.)
			return AstExpression.AreCompatible(scriptType, patternType);
		}

		private Type GetElementType(Type type)
		{
			if (type == null)
				return null;

			// Check if it's an array (int[], string[], etc.)
			if (type.IsArray)
				return type.GetElementType();

			// Check if it's a generic collection (List<int>, IEnumerable<string>, etc.)
			if (type.IsGenericType)
			{
				var genericDef = type.GetGenericTypeDefinition();
				// Check for common collection interfaces/classes
				if (genericDef == typeof(List<>) ||
					genericDef == typeof(IEnumerable<>) ||
					genericDef == typeof(IList<>) ||
					genericDef == typeof(ICollection<>))
				{
					return type.GetGenericArguments()[0];
				}
			}

			// Check if type implements IEnumerable<T>
			var enumerableInterface = type.GetInterfaces().FirstOrDefault(i =>
				i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
			if (enumerableInterface != null)
			{
				return enumerableInterface.GetGenericArguments()[0];
			}

			return null;
		}


		private bool MatchAlternative(AlternativeExpressionNode alternative, PatternListNode patternAst, MatchParameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			// Try each branch sequentially; the first that matches wins.
			foreach (var branch in alternative.Branches)
			{
				// Save state for rollback if this branch does not match.
				// PERF (Tier 3): the snapshot is only needed if there are already
				// consumed indices. For alternatives that appear at the start of the
				// pattern (the common case) the sets are empty and the rollback is a
				// simple Clear(), avoiding the two HashSet copies. Recursion-safe: each
				// invocation (including nested alternatives) has its own local snapshot
				// variables.
				int savedPosition = lastMatchedPosition;
				HashSet<int> savedMemberIndices = usedMemberAccessIndices.Count > 0 ? new HashSet<int>(usedMemberAccessIndices) : null;
				HashSet<int> savedMethodIndices = usedMethodCallIndices.Count > 0 ? new HashSet<int>(usedMethodCallIndices) : null;

				if (MatchExpression(branch.Expression, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition))
				{
					// If the branch has a label, capture it.
					if (branch.Label != null)
					{
						capturedVariables["_matchedBranch", typeof(string)] = branch.Label;
					}
					return true;
				}

				// Rollback the state.
				lastMatchedPosition = savedPosition;
				usedMemberAccessIndices.Clear();
				if (savedMemberIndices != null) foreach (var idx in savedMemberIndices) usedMemberAccessIndices.Add(idx);
				usedMethodCallIndices.Clear();
				if (savedMethodIndices != null) foreach (var idx in savedMethodIndices) usedMethodCallIndices.Add(idx);
			}

			return false;
		}

		private bool MatchGuardedExpression(GuardedExpressionNode guarded, PatternListNode patternAst, MatchParameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			// First: structural match of the inner expression.
			if (!MatchExpression(guarded.InnerExpression, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition))
			{
				return false;
			}

			// Second: evaluate all guard clauses (implicit AND).
			foreach (var guard in guarded.Guards)
			{
				if (!EvaluateGuard(guard, capturedVariables, patternAst))
				{
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Guard failed: {guard.VariableName} {guard.Operator} {guard.Value}");
#endif
					return false;
				}
			}

			return true;
		}

		private bool EvaluateGuard(GuardClause guard, MatchParameters capturedVariables, PatternListNode patternAst)
		{
			// Special case: contains / not contains over the whole script text.
			if (guard.Operator == GuardOperator.Contains || guard.Operator == GuardOperator.NotContains)
			{
				if (guard.VariableName == null)
				{
					// Evaluate over the whole script text.
					if (scriptText == null)
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Guard contains/not contains: no script text available, failing");
#endif
						return false;
					}

					string searchText = guard.Value?.ToString();
					if (searchText == null) return false;

					bool found = scriptText.IndexOf(searchText, StringComparison.Ordinal) != -1;
					return guard.Operator == GuardOperator.Contains ? found : !found;
				}
				else
				{
					// contains/not contains over a captured variable.
					object variableValue = GetCapturedVariableValue(guard.VariableName, capturedVariables, patternAst);
					if (variableValue == null || variableValue is TypedValuePlaceholder)
						return false; // Runtime-only value, not evaluable; the guard fails.

					string variableStr = variableValue.ToString();
					string searchText = guard.Value?.ToString();
					if (searchText == null) return false;

					bool found = variableStr.IndexOf(searchText, StringComparison.Ordinal) != -1;
					return guard.Operator == GuardOperator.Contains ? found : !found;
				}
			}

			// General case: $variable op literal.
			if (guard.VariableName == null)
			{
				throw new LanguageException("Expected a variable name for the comparison guard.");
			}

			object capturedValue = GetCapturedVariableValue(guard.VariableName, capturedVariables, patternAst);
			if (capturedValue == null || capturedValue is TypedValuePlaceholder)
			{
				// Runtime-only value (not a literal or parameter); the guard FAILS.
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Guard: variable '{guard.VariableName}' is runtime-only, failing guard");
#endif
				return false;
			}

			return CompareValues(capturedValue, guard.Operator, guard.Value);
		}

		private object GetCapturedVariableValue(string variableName, MatchParameters capturedVariables, PatternListNode patternAst)
		{
			// Look up the captured parameters.
			string cleanName = variableName.StartsWith("$") ? variableName.Substring(1) : variableName;

			if (capturedVariables.ContainsParameter(cleanName))
			{
				var param = capturedVariables[cleanName];
				return param?.GetValue();
			}

			// Look up the ScriptMethodCalls - arguments that matched the variable.
			// Method argument values live in ScriptMethodCall.Arguments.
			// We need to search by the name of the variable that matched.
			foreach (var methodCall in patternAst.ScriptMethodCalls)
			{
				for (int i = 0; i < methodCall.Arguments.Count; i++)
				{
					var arg = methodCall.Arguments[i];
					if (arg is TypedValuePlaceholder placeholder && placeholder.VariableName == cleanName)
					{
						return placeholder; // Runtime-only value; the guard will fail.
					}
				}
			}

			return null;
		}

		private bool CompareValues(object left, GuardOperator op, object right)
		{
			if (left == null || right == null) return false;

			try
			{
				AstExpression leftExpr = CreateLiteralExpressionForGuard(left);
				AstExpression rightExpr = CreateLiteralExpressionForGuard(right);

				AstExpression comparison;
				switch (op)
				{
					case GuardOperator.Equal:
						comparison = new OpEqual(leftExpr, rightExpr);
						break;
					case GuardOperator.NotEqual:
						comparison = new OpNotEqual(leftExpr, rightExpr);
						break;
					case GuardOperator.GreaterThan:
						comparison = new OpGreaterThan(leftExpr, rightExpr);
						break;
					case GuardOperator.LessThan:
						comparison = new OpLessThan(leftExpr, rightExpr);
						break;
					case GuardOperator.GreaterOrEqual:
						comparison = new OpGreaterOrEqual(leftExpr, rightExpr);
						break;
					case GuardOperator.LessOrEqual:
						comparison = new OpLessOrEqual(leftExpr, rightExpr);
						break;
					default:
						return false;
				}

				var result = comparison.Execute();
				return result is bool boolResult && boolResult;
			}
			catch
			{
				return false;
			}
		}

		private AstExpression CreateLiteralExpressionForGuard(object value)
		{
			if (value == null)
				return new LiteralNull();

			Type valueType = value.GetType();

			if (valueType == typeof(int))
				return new LiteralNumber((int)value);
			if (valueType == typeof(double))
				return new LiteralDouble((double)value);
			if (valueType == typeof(decimal))
				return new LiteralDecimal((decimal)value);
			if (valueType == typeof(string))
				return new LiteralString((string)value);
			if (valueType == typeof(bool))
				return (bool)value ? LiteralBoolean.LiteralTrue : LiteralBoolean.LiteralFalse;
			if (valueType == typeof(DateTime))
				return new LiteralDateTime((DateTime)value);

			throw new InvalidOperationException($"Cannot create a literal for type '{valueType.Name}' inside a guard.");
		}

		// Match for Expose patterns.
		// Examples:
		//   expose _:int total;      → match alias "total" with type int.
		//   expose 100 total;        → match alias "total" with literal value 100.
		//   expose _ total;          → match any value at alias "total".
		//   expose $x total;          → capture the alias "total" value into $x (Step 13, pending).
		private bool MatchExposeNode(ExposeNode exposeNode, PatternListNode patternAst, MatchParameters capturedVariables, ref int lastMatchedPosition)
		{
			if (exposeNode == null) return false;
			if (string.IsNullOrEmpty(exposeDataJson)) return false;

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Matching ExposeNode for alias: {exposeNode.Alias}");
#endif

			// Look up the alias in the expose JSON.
			var aliasValue = FindAliasInExposeJson(exposeDataJson, exposeNode.Alias);
			if (aliasValue == null)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Alias '{exposeNode.Alias}' not found in ExposeData");
#endif
				return false;
			}

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Found alias '{exposeNode.Alias}' with value: {aliasValue}");
#endif

			// Evaluate the pattern expression against the located value.
			switch (exposeNode.Expression)
			{
				case WildcardParameterNode:
					// expose _ total; → match any value.
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Wildcard match - always succeeds");
#endif
					return true;

				case TypedParameterNode typedParam:
					// Check whether this is a typed capture: expose $x:int total;
					if (typedParam.ParameterName != null && typedParam.ParameterName.StartsWith("$"))
					{
						// Typed capture.
						return CaptureExposeValue(exposeNode.Alias, typedParam.ParameterName, typedParam.ParameterType, capturedVariables);
					}
					else
					{
						// expose _:int total; → type-only match (Step 12).
						Type expectedType = typedParam.ParameterType;
						Type actualType = aliasValue.GetType();

						// Compare types.
						bool typeMatches = expectedType.IsAssignableFrom(actualType) || actualType == expectedType;
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Type match: expected={expectedType.Name}, actual={actualType.Name}, matches={typeMatches}");
#endif
						return typeMatches;
					}

				case LiteralParameterNode literalParam:
					// expose 100 total; → match by literal value.
					bool valueMatches = literalParam.CompareLiteralsUsingInterpreter(aliasValue);
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Literal match: expected={literalParam.Value}, actual={aliasValue}, matches={valueMatches}");
#endif
					return valueMatches;

				case VariableParameterNode variableParam:
					// expose $x total; → capture without type validation (Step 13).
					return CaptureExposeValue(exposeNode.Alias, variableParam.VariableName, null, capturedVariables);

				default:
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Unknown parameter type: {exposeNode.Expression.GetType().Name}");
#endif
					return false;
			}
		}

		// Step 13: capture expose values into a $variable.
		private bool CaptureExposeValue(string alias, string variableName, Type expectedType, MatchParameters capturedVariables)
		{
#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Capturing expose alias '{alias}' into variable '{variableName}'");
#endif

			// Extract ALL alias values (flattening nested arrays).
			var values = ExtractAllAliasValues(exposeDataJson, alias);
			if (values == null || values.Count == 0)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] No values found for alias '{alias}'");
#endif
				return false;
			}

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Found {values.Count} value(s) for alias '{alias}'");
#endif

			// Decide the value to capture: a single one or an array.
			object capturedValue;
			Type capturedType;

			if (values.Count == 1)
			{
				// A single value: capture as a simple type.
				capturedValue = values[0];
				capturedType = capturedValue.GetType();
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Single value: {capturedValue} (type={capturedType.Name})");
#endif
			}
			else
			{
				// Multiple values: build an array and flatten.
				// Infer the type from the first value.
				Type elementType = values[0].GetType();
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Multiple values, creating array of {elementType.Name}");
#endif

				// Build an array of the inferred type.
				Array array = Array.CreateInstance(elementType, values.Count);
				for (int i = 0; i < values.Count; i++)
				{
					array.SetValue(values[i], i);
				}

				capturedValue = array;
				capturedType = array.GetType();
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Created array: {capturedType.Name} with {values.Count} elements");
#endif
			}

			// Validate the type if one was specified.
			if (expectedType != null)
			{
				bool typeMatches = expectedType.IsAssignableFrom(capturedType) ||
								   capturedType == expectedType ||
								   (expectedType.IsArray && capturedType.IsArray &&
									expectedType.GetElementType().IsAssignableFrom(capturedType.GetElementType()));

				if (!typeMatches)
				{
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Type mismatch: expected={expectedType.Name}, actual={capturedType.Name}");
#endif
					return false;
				}

#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Type validation passed: {expectedType.Name}");
#endif
			}

			// Capture into Parameters (strip the leading '$' if present).
			string paramName = variableName.StartsWith("$") ? variableName.Substring(1) : variableName;
			capturedVariables[paramName, capturedType] = capturedValue;
#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Captured as parameter: {paramName} = {capturedValue}");
#endif

			return true;
		}

		// Helper: search for an alias inside the expose JSON.
		// Supports simple JSON: {"total": 100}
		// Supports JSON produced by 'for': {"items": [{"subtotal": 10}, {"subtotal": 20}]}
		// Returns the first value found (may be nested inside 'for' arrays).
		private object FindAliasInExposeJson(string json, string alias)
		{
			if (string.IsNullOrEmpty(json)) return null;
			if (string.IsNullOrEmpty(alias)) return null;

			try
			{
				// Parse the JSON.
				var jsonDoc = System.Text.Json.JsonDocument.Parse(json);
				var root = jsonDoc.RootElement;

				// Search recursively for the alias.
				return FindAliasRecursive(root, alias);
			}
			catch (Exception ex)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Error parsing ExposeData JSON: {ex.Message}");
#endif
				return null;
			}
		}

		// Recursive helper to search for an alias in JSON.
		private object FindAliasRecursive(System.Text.Json.JsonElement element, string alias)
		{
			switch (element.ValueKind)
			{
				case System.Text.Json.JsonValueKind.Object:
					// Search the property directly.
					if (element.TryGetProperty(alias, out var property))
					{
						return JsonElementToObject(property);
					}

					// If not found, search recursively in all properties.
					foreach (var prop in element.EnumerateObject())
					{
						var result = FindAliasRecursive(prop.Value, alias);
						if (result != null) return result;
					}
					break;

				case System.Text.Json.JsonValueKind.Array:
					// Search each array element.
					foreach (var item in element.EnumerateArray())
					{
						var result = FindAliasRecursive(item, alias);
						if (result != null) return result;
					}
					break;
			}

			return null;
		}

		// Helper: convert a JsonElement to a CLR value.
		private object JsonElementToObject(System.Text.Json.JsonElement element)
		{
			switch (element.ValueKind)
			{
				case System.Text.Json.JsonValueKind.String:
					return element.GetString();

				case System.Text.Json.JsonValueKind.Number:
					if (element.TryGetInt32(out int intValue))
						return intValue;
					if (element.TryGetInt64(out long longValue))
						return (int)longValue; // Convert to int.
					if (element.TryGetDecimal(out decimal decimalValue))
						return decimalValue;
					if (element.TryGetDouble(out double doubleValue))
						return doubleValue;
					return element.GetRawText();

				case System.Text.Json.JsonValueKind.True:
					return true;

				case System.Text.Json.JsonValueKind.False:
					return false;

				case System.Text.Json.JsonValueKind.Null:
					return null;

				default:
					return element.GetRawText();
			}
		}

		// Step 13: extract ALL values of an alias from the JSON (flattening arrays).
		private List<object> ExtractAllAliasValues(string json, string alias)
		{
			if (string.IsNullOrEmpty(json)) return null;
			if (string.IsNullOrEmpty(alias)) return null;

			try
			{
				var jsonDoc = System.Text.Json.JsonDocument.Parse(json);
				var root = jsonDoc.RootElement;
				var values = new List<object>();
				ExtractAllAliasValuesRecursive(root, alias, values);
				return values.Count > 0 ? values : null;
			}
			catch (Exception ex)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Error extracting alias values: {ex.Message}");
#endif
				return null;
			}
		}

		// Recursive helper to extract ALL values of an alias.
		private void ExtractAllAliasValuesRecursive(System.Text.Json.JsonElement element, string alias, List<object> values)
		{
			switch (element.ValueKind)
			{
				case System.Text.Json.JsonValueKind.Object:
					// Search the property directly.
					if (element.TryGetProperty(alias, out var property))
					{
						var value = JsonElementToObject(property);
						if (value != null)
						{
							values.Add(value);
						}
					}

					// Keep searching in all the value's properties.
					foreach (var prop in element.EnumerateObject())
					{
						if (!string.Equals(prop.Name, alias, StringComparison.Ordinal))
						{
							ExtractAllAliasValuesRecursive(prop.Value, alias, values);
						}
					}
					break;

				case System.Text.Json.JsonValueKind.Array:
					// Recurse into each array element.
					foreach (var item in element.EnumerateArray())
					{
						ExtractAllAliasValuesRecursive(item, alias, values);
					}
					break;

				default:
					// Primitive types: nothing more to search.
					break;
			}
		}
	}

}
