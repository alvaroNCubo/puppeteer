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

			// Guardar el ExposeData JSON para usarlo en matching de ExposeNode
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

			// Rent a Parameters instance from the pool.
			Parameters capturedVariables = parametersPool.Rent();
			capturedVariables.PurgeUserParameters();
			capturedVariables.Clear();

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
				// Successful match - return captured variables.
				return capturedVariables;
			}
			else
			{
				// No match - return to the pool.
				capturedVariables.PurgeUserParameters();
				parametersPool.Return(capturedVariables);
				return null;
			}
		}
		private bool MatchExpression(ExpressionNode expression, PatternListNode patternAst, Parameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
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
		private bool MatchTellPattern(TellPatternNode pattern, PatternListNode patternAst, Parameters capturedVariables, ref int lastMatchedPosition)
		{
			for (int i = 0; i < patternAst.ScriptTellStatements.Count; i++)
			{
				if (usedTellStatementIndices.Contains(i)) continue;
				ScriptTellStatement script = patternAst.ScriptTellStatements[i];
				if (script.Position <= lastMatchedPosition) continue;

				if (!string.Equals(script.TargetClass, pattern.TargetClass, StringComparison.Ordinal)) continue;
				if (!string.Equals(script.CommandName, pattern.CommandName, StringComparison.Ordinal)) continue;
				if (script.CommandArgsValues.Length != pattern.CommandParameters.Count) continue;

				// Snapshot capturedVariables in case sub-matches partially fill it
				// before a later mismatch — we restore on rollback. Plan 7b uses a
				// simple tentative approach: try in order, bail out on mismatch.
				if (!MatchOrConstraintParameter(pattern.TargetParameter, script.TargetIdValue, capturedVariables)) continue;

				bool argsMatch = true;
				for (int a = 0; a < pattern.CommandParameters.Count; a++)
				{
					if (!MatchOrConstraintParameter(pattern.CommandParameters[a], script.CommandArgsValues[a], capturedVariables))
					{
						argsMatch = false;
						break;
					}
				}
				if (!argsMatch) continue;

				if (pattern.IdParameter != null)
				{
					if (!MatchOrConstraintParameter(pattern.IdParameter, script.EnvelopeId, capturedVariables)) continue;
				}

				usedTellStatementIndices.Add(i);
				lastMatchedPosition = script.Position;
				return true;
			}
			return false;
		}

		// Plan 7 of the Tell primitive roadmap: match an ack pattern against the
		// script's ScriptTellAckStatement entries.
		private bool MatchTellAckPattern(TellAckPatternNode pattern, PatternListNode patternAst, Parameters capturedVariables, ref int lastMatchedPosition)
		{
			for (int i = 0; i < patternAst.ScriptTellAckStatements.Count; i++)
			{
				if (usedTellAckStatementIndices.Contains(i)) continue;
				ScriptTellAckStatement script = patternAst.ScriptTellAckStatements[i];
				if (script.Position <= lastMatchedPosition) continue;

				if (!MatchOrConstraintParameter(pattern.AckIdParameter, script.AckId, capturedVariables)) continue;

				if (pattern.FromTargetClass != null)
				{
					if (!string.Equals(script.FromTargetClass, pattern.FromTargetClass, StringComparison.Ordinal)) continue;
					if (!MatchOrConstraintParameter(pattern.FromTargetParameter, script.FromTargetIdValue, capturedVariables)) continue;
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
		private bool MatchOrConstraintParameter(ParameterNode patternParam, object scriptValue, Parameters capturedVariables)
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
						object captured = capturedVariables[paramName]?.GetValue();
						if (captured == null && scriptValue == null) return true;
						if (captured == null || scriptValue == null) return false;
						return captured.Equals(scriptValue) || scriptValue.Equals(captured);
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
		private bool MatchTypeAccess(TypeAccessNode typeAccess, PatternListNode patternAst, Parameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
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

					if (!scriptMethodCall.Method.DeclaringType.Name.Equals(typeAccess.TypeName, StringComparison.OrdinalIgnoreCase))
						continue;

					// Check the method against its arguments.
					if (!MatchMethodCall(typeAccess.MemberAccess, scriptMethodCall, capturedVariables))
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

					if (!scriptAccess.Member.DeclaringType.Name.Equals(typeAccess.TypeName, StringComparison.OrdinalIgnoreCase))
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
		private bool MatchInstanceAccess(InstanceAccessNode instanceAccess, PatternListNode patternAst, Parameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
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

					if (!scriptMethodCall.Method.DeclaringType.Name.Equals(instanceAccess.TypeName, StringComparison.OrdinalIgnoreCase))
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
					if (!MatchMethodCall(instanceAccess.MemberAccess, scriptMethodCall, capturedVariables))
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

					if (!scriptAccess.Member.DeclaringType.Name.Equals(instanceAccess.TypeName, StringComparison.OrdinalIgnoreCase))
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
		private bool MatchMemberAccess(MemberAccessNode memberAccess, System.Reflection.MemberInfo scriptMember, PatternListNode patternAst, Parameters capturedVariables)
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
		private bool MatchParameter(ParameterNode parameterNode, Type scriptParameterType, Parameters capturedVariables)
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
		private bool MatchConstructorCall(ConstructorCallNode constructor, PatternListNode patternAst, Parameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
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
				if (constructor.Parameters.Count != scriptCall.Arguments.Count)
					continue;

				// Check each parameter.
				bool allParametersMatch = true;
				for (int i = 0; i < constructor.Parameters.Count; i++)
				{
					var patternParam = constructor.Parameters[i];
					var scriptParamType = scriptCall.Arguments[i];

					if (!MatchParameter(patternParam, scriptParamType, capturedVariables))
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
		private bool MatchAssignment(AssignmentNode assignment, PatternListNode patternAst, Parameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
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
					// The right-hand side of the pattern is a constructor.
					// Verify the script value has the expected type.
					if (scriptAssignment.Value is TypedValuePlaceholder placeholder)
					{
						// Check that the type matches the pattern's constructor.
						if (!placeholder.ValueType.Name.Equals(constructorPattern.TypeName, StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
					}
					else
					{
						// The script value is not a placeholder (it's a literal), so it does not match a constructor.
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
		private bool MatchPartialPattern(PartialPatternNode partialPattern, PatternListNode patternAst, Parameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			if (partialPattern.Patterns.Count == 0)
				return false;

			int currentScriptIndex = 0;
			var scriptAccesses = patternAst.ScriptMemberAccesses.ToList();

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
		private bool MatchMethodCall(MemberAccessNode memberAccess, ScriptMethodCall scriptMethodCall, Parameters capturedVariables)
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

			for (int i = 0; i < memberAccess.Parameters.Count; i++)
			{
				var patternParam = memberAccess.Parameters[i];
				var scriptArgument = scriptMethodCall.Arguments[i];

#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchMethodCall] Checking param {i}: Pattern={patternParam.GetType().Name}, Script={scriptArgument?.GetType().Name ?? "NULL"}");
#endif

				if (!MatchParameterValue(patternParam, scriptArgument, capturedVariables))
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
		private bool MatchParameterValue(ParameterNode parameterNode, object scriptValue, Parameters capturedVariables)
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
					// Capture the concrete value if available (for guard evaluation).
					if (scriptValue != null && !(scriptValue is TypedValuePlaceholder))
					{
						string paramName = variable.VariableName.StartsWith("$") ? variable.VariableName.Substring(1) : variable.VariableName;
						capturedVariables[paramName, scriptValue.GetType()] = scriptValue;
					}
					else if (scriptValue is TypedValuePlaceholder tvp && !string.IsNullOrEmpty(tvp.VariableName))
					{
						string paramName = variable.VariableName.StartsWith("$") ? variable.VariableName.Substring(1) : variable.VariableName;
						capturedVariables[paramName, typeof(string)] = tvp.VariableName;
					}
					return true;

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
					Type scriptValueType = scriptValue.GetType();
					return AreTypesCompatible(typed.ParameterType, scriptValueType) ||
						   AstExpression.AreCompatible(typed.ParameterType, scriptValueType);

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


		private bool MatchAlternative(AlternativeExpressionNode alternative, PatternListNode patternAst, Parameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			// Intentar cada rama secuencialmente, la primera que matchea gana
			foreach (var branch in alternative.Branches)
			{
				// Guardar estado para rollback si esta rama no matchea
				int savedPosition = lastMatchedPosition;
				var savedMemberIndices = new HashSet<int>(usedMemberAccessIndices);
				var savedMethodIndices = new HashSet<int>(usedMethodCallIndices);

				if (MatchExpression(branch.Expression, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition))
				{
					// Si la rama tiene label, capturarla
					if (branch.Label != null)
					{
						capturedVariables["_matchedBranch", typeof(string)] = branch.Label;
					}
					return true;
				}

				// Rollback del estado
				lastMatchedPosition = savedPosition;
				usedMemberAccessIndices.Clear();
				foreach (var idx in savedMemberIndices) usedMemberAccessIndices.Add(idx);
				usedMethodCallIndices.Clear();
				foreach (var idx in savedMethodIndices) usedMethodCallIndices.Add(idx);
			}

			return false;
		}

		private bool MatchGuardedExpression(GuardedExpressionNode guarded, PatternListNode patternAst, Parameters capturedVariables, HashSet<int> usedMemberAccessIndices, HashSet<int> usedMethodCallIndices, ref int lastMatchedPosition)
		{
			// Primero: match estructural de la expression interna
			if (!MatchExpression(guarded.InnerExpression, patternAst, capturedVariables, usedMemberAccessIndices, usedMethodCallIndices, ref lastMatchedPosition))
			{
				return false;
			}

			// Segundo: evaluar todas las guard clauses (AND implicito)
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

		private bool EvaluateGuard(GuardClause guard, Parameters capturedVariables, PatternListNode patternAst)
		{
			// Caso especial: contains / not contains sobre el text completo del script
			if (guard.Operator == GuardOperator.Contains || guard.Operator == GuardOperator.NotContains)
			{
				if (guard.VariableName == null)
				{
					// Evaluar sobre el text completo del script
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
					// contains/not contains sobre una variable capturada
					object variableValue = GetCapturedVariableValue(guard.VariableName, capturedVariables, patternAst);
					if (variableValue == null || variableValue is TypedValuePlaceholder)
						return false; // ValueGetter runtime no evaluable, falla el guard

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

		private object GetCapturedVariableValue(string variableName, Parameters capturedVariables, PatternListNode patternAst)
		{
			// Buscar en los parameters capturados
			string cleanName = variableName.StartsWith("$") ? variableName.Substring(1) : variableName;

			if (capturedVariables.ContainsParameter(cleanName))
			{
				var param = capturedVariables[cleanName];
				return param?.GetValue();
			}

			// Buscar en los ScriptMethodCalls - arguments que matchearon con la variable
			// Los values de arguments de methods estan en ScriptMethodCall.Arguments
			// Necesitamos buscar por el nombre de la variable que matcheo
			foreach (var methodCall in patternAst.ScriptMethodCalls)
			{
				for (int i = 0; i < methodCall.Arguments.Count; i++)
				{
					var arg = methodCall.Arguments[i];
					if (arg is TypedValuePlaceholder placeholder && placeholder.VariableName == cleanName)
					{
						return placeholder; // Es runtime, el guard fallara
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
		//   expose $x total;          → Captura el value del alias "total" en $x (Step 13, pendiente)
		private bool MatchExposeNode(ExposeNode exposeNode, PatternListNode patternAst, Parameters capturedVariables, ref int lastMatchedPosition)
		{
			if (exposeNode == null) return false;
			if (string.IsNullOrEmpty(exposeDataJson)) return false;

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[PatternMatcher] Matching ExposeNode for alias: {exposeNode.Alias}");
#endif

			// Buscar el alias en el JSON del expose
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
		private bool CaptureExposeValue(string alias, string variableName, Type expectedType, Parameters capturedVariables)
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
				// Parsear el JSON
				var jsonDoc = System.Text.Json.JsonDocument.Parse(json);
				var root = jsonDoc.RootElement;

				// Buscar recursivamente el alias
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

		// Helper recursivo para buscar alias en JSON
		private object FindAliasRecursive(System.Text.Json.JsonElement element, string alias)
		{
			switch (element.ValueKind)
			{
				case System.Text.Json.JsonValueKind.Object:
					// Buscar la property directamente
					if (element.TryGetProperty(alias, out var property))
					{
						return JsonElementToObject(property);
					}

					// Si no se encuentra, buscar recursivamente en todas las properties
					foreach (var prop in element.EnumerateObject())
					{
						var result = FindAliasRecursive(prop.Value, alias);
						if (result != null) return result;
					}
					break;

				case System.Text.Json.JsonValueKind.Array:
					// Buscar en cada elemento del array
					foreach (var item in element.EnumerateArray())
					{
						var result = FindAliasRecursive(item, alias);
						if (result != null) return result;
					}
					break;
			}

			return null;
		}

		// Helper: Convierte JsonElement a value CLR
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
						return (int)longValue; // Convertir a int
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

		// Step 13: Extraer TODOS los values de un alias desde el JSON (con aplanamiento de arrays)
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

		// Helper recursivo para extraer TODOS los values de un alias
		private void ExtractAllAliasValuesRecursive(System.Text.Json.JsonElement element, string alias, List<object> values)
		{
			switch (element.ValueKind)
			{
				case System.Text.Json.JsonValueKind.Object:
					// Buscar la property directamente
					if (element.TryGetProperty(alias, out var property))
					{
						var value = JsonElementToObject(property);
						if (value != null)
						{
							values.Add(value);
						}
					}

					// Continuar buscando en todas las properties del value
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
