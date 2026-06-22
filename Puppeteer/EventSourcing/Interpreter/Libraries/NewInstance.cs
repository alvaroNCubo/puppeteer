using System;
using System.Collections.Generic;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	using Puppeteer;
	using Puppeteer.EventSourcing.Follower;
	using Puppeteer.EventSourcing.Interpreter.Utils;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using SymbolTable = SymbolTable;

	class NewInstance : AstExpression
    {
        private readonly string className;
        private readonly AstExpression[] arguments;
        private readonly SymbolTable symbolTable;
		private List<ConstructorInfo> constructors;
		private readonly string targetNamespace;

		// State captured in CollectConstructors only for the diagnostic of
		// BuildConstructorMismatchMessage: distinguishes "class absent from the library"
		// from "class present but without a constructor of that arity", and lists what the
		// library DID load. Without this both causes emitted the same misleading message.
		private bool classRegisteredInLibrary;
		private List<EventSourcing.DomainLibraries.ClassInfo> registeredClasses;
		private string[] loadedAssemblyNames;
		private string[] knownClassNames;

		internal NewInstance(DomainLibraries libraries, SymbolTable symbolTable, Id clazz, AstExpression[] arguments, string targetNamespace = null)
        {
			if (libraries == null) throw new ArgumentNullException(nameof(libraries));
			if (symbolTable == null) throw new ArgumentNullException(nameof(symbolTable));
			if (clazz == null) throw new ArgumentNullException(nameof(clazz));

            this.symbolTable = symbolTable;
            this.className = clazz.Name;
            this.arguments = arguments;
			this.targetNamespace = targetNamespace;
			CollectConstructors(libraries);
		}

        private NewInstance(SymbolTable symbolTable, string className, AstExpression[] arguments, List<ConstructorInfo> constructors)
        {
            this.symbolTable = symbolTable;
            this.className = className;
            this.arguments = arguments;
            this.constructors = constructors;
		}

        internal override void write(StringBuilder result, DatabaseType databaseType)
        {
            result.Append(className);
            result.Append('(');
            bool needsComma = false;
            foreach (AstExpression e in arguments)
            {
                if (needsComma) result.Append(','); else needsComma = true;

                e.write(result, databaseType);
            }
            result.Append(')');
        }

		internal override Type ComputeType()
		{
			return ResolveConstructor().DeclaringType;
		}

        internal string ClassName()
        {
            return className;
        }

        internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
        {
            // Process arguments first.
            List<Type> argumentTypes = new List<Type>();
            if (arguments != null)
            {
                foreach (AstExpression arg in arguments)
                {
                    arg.PreparePatternMatching(patternAst, ref position);
                    argumentTypes.Add(arg.ComputeType());
                }
            }

            // Register the constructor call with its type and argument types.
            Type constructorType = this.ComputeType();
            patternAst.RegisterConstructorCall(constructorType, argumentTypes, position);

            // Advance the position.
            position++;
        }

        internal override void Visit(ASTVisitor v)
        {
            if (this.GetType() == v.Target)
            {
                v.OnVisit(this);
            }
            if (arguments != null) foreach (AstExpression e in arguments)
            {
                e.Visit(v);
            }
        }

        internal override object Execute()
        {
            LabCounter.Increment();
            object instance = InstantiateObject();
            return instance;
        }

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			// Obtain the matching constructor.
			ConstructorInfo constructor = FindMatchingConstructor();
			ParameterInfo[] parameters = constructor.GetParameters();

			if (ConstructorUsesParamsExpansion(parameters, out Type paramsElementType, out int paramsFixedCount))
			{
				return BuildParamsExpansionNew(constructor, parameters, paramsElementType, paramsFixedCount, parametersParam);
			}

			// Build the argument expressions.
			Expression[] args = new Expression[arguments.Length];
			for (int i = 0; i < arguments.Length; i++)
			{
				args[i] = BuildConstructorArgExpression(i, parameters[i].ParameterType, parametersParam);
			}

			// Build the new-instance expression.
			return Expression.New(constructor, args);
		}

		private Expression BuildConstructorArgExpression(int i, Type parameterType, ParameterExpression parametersParam)
		{
			Expression argExpr;

			// If the parameter is Enum and the argument is an Id, convert the symbolic name to the Enum value.
			// IMPORTANT: do NOT call arguments[i].ExecuteExpression(...) in this branch — the Id has no scope
			// (it is neither a local, global, nor parameter), and AllocateStorageExpression would throw
			// "Cannot generate an Expression for Id '...' because its scope is undefined".
			if (parameterType.IsEnum && AstExpression.ClassifyEnumArg(arguments[i]) != AstExpression.EnumArgKind.NotEnumBindable)
			{
				// Bare symbol -> Enum.Parse(Name) without ExecuteExpression (it has no scope);
				// string with a value (parameter/variable/literal) -> Enum.Parse(value). The helper
				// chooses the source of the name according to the argument's type.
				argExpr = AstExpression.ParseEnumArgExpression(parameterType, arguments[i], parametersParam);
			}
			else
			{
				argExpr = arguments[i].ExecuteExpression(parametersParam);
				Type argumentType = arguments[i].ComputeType();

				if (!parameterType.IsAssignableFrom(argumentType))
				{
					// If the type is not exactly compatible, try converting via TypeConversion.ImplicitCast.
					var implicitCastMethod = typeof(TypeConversion).GetMethod(
						nameof(TypeConversion.ImplicitCast),
						BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
						null,
						new[] { typeof(object), typeof(Type) },
						null
					);

					if (implicitCastMethod == null)
						throw new LanguageException($"Method '{nameof(TypeConversion.ImplicitCast)}' was not found on '{nameof(TypeConversion)}'.");

					// Call TypeConversion.ImplicitCast(argExpr, parameterType) as an expression.
					var implicitCast = Expression.Call(
						implicitCastMethod,
						Expression.Convert(argExpr, typeof(object)),
						Expression.Constant(parameterType, typeof(Type))
					);

					argExpr = Expression.Convert(implicitCast, parameterType);
				}
			}

			return argExpr;
		}

		// Compiled binding of an expanded params constructor: binds the fixed ones by the normal path and
		// emits Expression.NewArrayInit of the element-type with each trailing one converted by the
		// scalar path (numeric via CoerceScalarExpression + enum via ParseEnumArgExpression).
		private Expression BuildParamsExpansionNew(ConstructorInfo constructor, ParameterInfo[] parameters, Type elementType, int fixedCount, ParameterExpression parametersParam)
		{
			Expression[] args = new Expression[parameters.Length];

			for (int i = 0; i < fixedCount; i++)
			{
				args[i] = BuildConstructorArgExpression(i, parameters[i].ParameterType, parametersParam);
			}

			List<Expression> elementExprs = new List<Expression>();
			for (int j = fixedCount; j < arguments.Length; j++)
			{
				AstExpression argNode = arguments[j];
				Expression elementExpr;
				if (elementType.IsEnum && AstExpression.ClassifyEnumArg(argNode) != AstExpression.EnumArgKind.NotEnumBindable)
				{
					elementExpr = AstExpression.ParseEnumArgExpression(elementType, argNode, parametersParam);
				}
				else
				{
					elementExpr = AstExpression.CoerceScalarExpression(argNode.ExecuteExpression(parametersParam), elementType);
				}
				elementExprs.Add(elementExpr);
			}

			args[fixedCount] = Expression.NewArrayInit(elementType, elementExprs);

			return Expression.New(constructor, args);
		}

        private object InstantiateObject()
        {
            ConstructorInfo constructor = FindMatchingConstructor();
            ParameterInfo[] constructorSignature = constructor.GetParameters();

            object[] constructorArgValues;
            if (ConstructorUsesParamsExpansion(constructorSignature, out Type paramsElementType, out int paramsFixedCount))
            {
                constructorArgValues = BindParamsExpansionInterpreted(constructorSignature, paramsElementType, paramsFixedCount);
            }
            else
            {
                object[] evaluatedArguments = new object[this.arguments.Length];

                for (int i = 0; i < this.arguments.Length; i++)
                {
                    // If the parameter is an enum and the argument is a bare Id (symbolic enum-value
                    // reference such as 'Text' for InputType.Text), pass the AST node itself so
                    // BindValuesToParameters can convert it via Enum.TryParse.
                    // Calling Execute() on the Id would throw because the variable was never defined.
                    ParameterInfo parameterInfo = constructorSignature[i];
                    if (parameterInfo.ParameterType.IsEnum && AstExpression.ClassifyEnumArg(this.arguments[i]) != AstExpression.EnumArgKind.NotEnumBindable)
                    {
                        evaluatedArguments[i] = AstExpression.ParseEnumArgValue(parameterInfo.ParameterType, this.arguments[i]);
                    }
                    else
                    {
                        evaluatedArguments[i] = this.arguments[i].Execute();
                    }
                }

                constructorArgValues = DotAccess.BindValuesToParameters(constructorSignature, evaluatedArguments);
            }

            try
            {
                return (object)constructor.Invoke(constructorArgValues);
            }
            catch (Exception e)
            {
                throw new LanguageException($"Error while instantiating class '{className}'. Details: {e.Message}");
            }
        }

        // Interpreted binding of an expanded params constructor: binds the fixed parameters by the
        // normal scalar path and builds the element-type array coercing each trailing element
        // by the same scalar path (numeric + enum). Parallel to DotAccess.BindParamsExpansionInterpreted.
        private object[] BindParamsExpansionInterpreted(ParameterInfo[] parameters, Type elementType, int fixedCount)
        {
            object[] result = new object[parameters.Length];

            object[] fixedEvaluated = new object[fixedCount];
            ParameterInfo[] fixedParameters = new ParameterInfo[fixedCount];
            for (int i = 0; i < fixedCount; i++)
            {
                ParameterInfo parameterInfo = parameters[i];
                fixedParameters[i] = parameterInfo;
                if (parameterInfo.ParameterType.IsEnum && AstExpression.ClassifyEnumArg(this.arguments[i]) != AstExpression.EnumArgKind.NotEnumBindable)
                {
                    fixedEvaluated[i] = AstExpression.ParseEnumArgValue(parameterInfo.ParameterType, this.arguments[i]);
                }
                else
                {
                    fixedEvaluated[i] = this.arguments[i].Execute();
                }
            }

            object[] boundFixed = DotAccess.BindValuesToParameters(fixedParameters, fixedEvaluated);
            for (int i = 0; i < fixedCount; i++)
            {
                result[i] = boundFixed[i];
            }

            int trailing = this.arguments.Length - fixedCount;
            Array array = Array.CreateInstance(elementType, trailing);
            for (int j = 0; j < trailing; j++)
            {
                AstExpression argNode = this.arguments[fixedCount + j];
                object element;
                if (elementType.IsEnum && AstExpression.ClassifyEnumArg(argNode) != AstExpression.EnumArgKind.NotEnumBindable)
                {
                    element = AstExpression.ParseEnumArgValue(elementType, argNode);
                }
                else
                {
                    element = AstExpression.CoerceScalarValue(argNode.Execute(), elementType);
                }
                array.SetValue(element, j);
            }
            result[fixedCount] = array;

            return result;
        }

	private void CollectConstructors(DomainLibraries libraries)
	{
		constructors = new List<ConstructorInfo>();

		// We capture the library context for the absent-class diagnostic.
		// It is cheap (a list of names) and only used if resolution fails.
		loadedAssemblyNames = libraries.LoadedAssemblyNames.ToArray();

		if (!libraries.TryFindClassesByName(className, out var classInfosEnumerable))
		{
			classRegisteredInLibrary = false;
			knownClassNames = libraries.KnownClassNames.ToArray();
			return;
		}

		var classInfos = classInfosEnumerable.ToList();

		if (classInfos.Count == 0)
		{
			classRegisteredInLibrary = false;
			knownClassNames = libraries.KnownClassNames.ToArray();
			return;
		}

		classRegisteredInLibrary = true;
		List<EventSourcing.DomainLibraries.ClassInfo> candidateClasses;

		if (targetNamespace != null)
		{
			candidateClasses = classInfos.Where(ci =>
				string.Equals(ci.Namespace, targetNamespace, StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (candidateClasses.Count == 0)
			{
				var availableNamespaces = string.Join(", ", classInfos.Select(ci => ci.Namespace).Distinct());
				throw new LanguageException($"Class '{className}' was not found in namespace '{targetNamespace}'. Available namespaces: {availableNamespaces}.");
			}
		}
		else
		{
			// Without the 'in' clause: we accept all candidates by simple name. The
			// ambiguity from homonymy across namespaces is NOT decided here — it is deferred
			// to ResolveConstructor (via ComputeType/FindMatchingConstructor), where it is
			// filtered by compatibility of the constructor signature. When the argument
			// types identify a single class, there is no real ambiguity and
			// instantiation proceeds; if the signature matches constructors of several
			// namespaces, there the "Ambiguous reference" error is thrown with the
			// correct detail (signature + namespaces that satisfy it).
			candidateClasses = classInfos;
		}

		// We keep the candidate classes (by name/namespace) so that, if NO
		// constructor matches the arity, the diagnostic can list the constructors
		// actually available instead of lying with "no class with that name is registered".
		registeredClasses = candidateClasses;

		foreach (var classInfo in candidateClasses)
		{
			foreach (ConstructorInfo constructorInfo in classInfo.Type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				ParameterInfo[] constructorParameters = constructorInfo.GetParameters();
				int parameterCount = constructorParameters.Length;
				bool sameArgumentCount = parameterCount == arguments.Length;

				// params candidate: last parameter is params and at least N-1 arguments. This way a
				// variable-arity call also collects the constructor params even though its
				// parameter count does not exactly match the number of arguments.
				bool paramsCandidate =
					parameterCount > 0
					&& AstExpression.IsParamsParameter(constructorParameters[parameterCount - 1])
					&& arguments.Length >= parameterCount - 1;

				bool isPublicOrInternal = constructorInfo.IsPublic || constructorInfo.IsAssembly;
				if ((sameArgumentCount || paramsCandidate) && isPublicOrInternal)
				{
					constructors.Add(constructorInfo);
				}
			}
		}
	}

        private ConstructorInfo FindMatchingConstructor()
        {
            return ResolveConstructor();
        }

        private ConstructorInfo ResolveConstructor()
        {
            if (constructors == null) throw new LanguageException($"Constructors have not been collected for class '{className}'. CollectConstructors must be called before resolving a constructor.");

            // Deterministic selection: first the non-params constructors of exact arity. Only
            // if none matches are the params ones considered. Thus a non-params constructor of exact
            // arity ALWAYS wins over the params variant (parallel to FindMethod in DotAccess).
            List<ConstructorInfo> exactConstructors = new List<ConstructorInfo>();
            List<ConstructorInfo> paramsConstructors = new List<ConstructorInfo>();
            foreach (ConstructorInfo constructorInfo in constructors)
            {
                if (!(constructorInfo.IsPublic || constructorInfo.IsAssembly)) continue;

                if (IsConstructorExactCompatible(constructorInfo))
                {
                    exactConstructors.Add(constructorInfo);
                }
                else if (IsConstructorParamsCompatible(constructorInfo))
                {
                    paramsConstructors.Add(constructorInfo);
                }
            }

            bool usingExact = exactConstructors.Count > 0;
            List<ConstructorInfo> compatibleConstructors = usingExact ? exactConstructors : paramsConstructors;

            if (compatibleConstructors.Count == 0)
            {
                throw new LanguageException(BuildConstructorMismatchMessage());
            }

            List<Type> distinctDeclaringTypes = compatibleConstructors
                .Select(c => c.DeclaringType)
                .Distinct()
                .ToList();

            if (distinctDeclaringTypes.Count > 1)
            {
                string namespaces = string.Join(", ", distinctDeclaringTypes
                    .Select(t => t.Namespace ?? string.Empty)
                    .Distinct());
                throw new LanguageException($"Ambiguous reference: class '{className}' with the given constructor signature exists in multiple namespaces: {namespaces}. Use the 'in' clause to specify the namespace (e.g. ClassName(...) in MyNamespace).");
            }

            // Non-params path: the historical selection "first compatible in CLR order" is kept
            // so as not to alter the numeric disambiguation already established between collection
            // overloads (e.g. List<double> binds to both List<double> and List<decimal>).
            // Params path: deterministic pick (does not depend on CLR order).
            return usingExact ? compatibleConstructors[0] : PickDeterministicConstructor(compatibleConstructors);
        }

        private static ConstructorInfo PickDeterministicConstructor(List<ConstructorInfo> candidates)
        {
            ConstructorInfo best = candidates[0];
            string bestSignature = BuildConstructorSignature(best);
            for (int i = 1; i < candidates.Count; i++)
            {
                int bestFixed = best.GetParameters().Length;
                int candidateFixed = candidates[i].GetParameters().Length;
                string candidateSignature = BuildConstructorSignature(candidates[i]);
                bool better =
                    candidateFixed > bestFixed
                    || (candidateFixed == bestFixed && string.CompareOrdinal(candidateSignature, bestSignature) < 0);
                if (better)
                {
                    best = candidates[i];
                    bestSignature = candidateSignature;
                }
            }
            return best;
        }

        private static string BuildConstructorSignature(ConstructorInfo constructorInfo)
        {
            ParameterInfo[] parameters = constructorInfo.GetParameters();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(parameters[i].ParameterType.FullName ?? parameters[i].ParameterType.Name);
            }
            return sb.ToString();
        }

        // Non-params compatibility of exact arity. Replicates EXACTLY the historical logic of
        // IsConstructorCompatibleWithArguments (without the null/object guard of the params path) so as
        // not to alter which constructors are considered compatible in the non-params path.
        private bool IsConstructorExactCompatible(ConstructorInfo constructorInfo)
        {
            ParameterInfo[] parameters = constructorInfo.GetParameters();
            if (parameters.Length != arguments.Length) return false;

            for (int i = 0; i < parameters.Length; i++)
            {
                Type paramType = parameters[i].ParameterType;
                if (paramType.IsEnum && AstExpression.ClassifyEnumArg(arguments[i]) != AstExpression.EnumArgKind.NotEnumBindable)
                {
                    if (!AstExpression.IsEnumArgCompatible(paramType, arguments[i])) return false;
                }
                else
                {
                    Type argType = arguments[i].ComputeType();
                    if (!AreCompatible(argType, paramType)) return false;
                }
            }
            return true;
        }

        // Match of a params constructor: last parameter is params, at least N-1 arguments, the
        // fixed ones compatible, and the trailing ones compatible as elements of the element-type (expanded
        // form) or as the already-built array passed directly (direct form). Conservative.
        private bool IsConstructorParamsCompatible(ConstructorInfo constructorInfo)
        {
            ParameterInfo[] parameters = constructorInfo.GetParameters();
            if (parameters.Length == 0) return false;

            ParameterInfo last = parameters[parameters.Length - 1];
            if (!AstExpression.IsParamsParameter(last)) return false;

            int fixedCount = parameters.Length - 1;
            if (arguments.Length < fixedCount) return false;

            for (int i = 0; i < fixedCount; i++)
            {
                if (!ScalarArgMatchesConstructorParam(i, parameters[i].ParameterType)) return false;
            }

            Type elementType = last.ParameterType.GetElementType();
            int trailing = arguments.Length - fixedCount;

            if (trailing == 1)
            {
                Type argType = arguments[fixedCount].ComputeType();
                if (argType != null && argType != typeof(object) && AreCompatible(argType, last.ParameterType))
                {
                    return true;
                }
            }

            for (int i = fixedCount; i < arguments.Length; i++)
            {
                if (!ScalarArgMatchesConstructorParam(i, elementType)) return false;
            }
            return true;
        }

        private bool ScalarArgMatchesConstructorParam(int argIndex, Type paramType)
        {
            if (paramType.IsEnum && AstExpression.ClassifyEnumArg(arguments[argIndex]) != AstExpression.EnumArgKind.NotEnumBindable)
            {
                return AstExpression.IsEnumArgCompatible(paramType, arguments[argIndex]);
            }
            Type argType = arguments[argIndex].ComputeType();
            if (argType == null || argType == typeof(object)) return false;
            return AreCompatible(argType, paramType);
        }

        // Decides whether the constructor call must EXPAND the trailing ones into a new array, or whether
        // the array was passed directly. Same rule as in DotAccess.UsesParamsExpansion.
        private bool ConstructorUsesParamsExpansion(ParameterInfo[] parameters, out Type elementType, out int fixedCount)
        {
            elementType = null;
            fixedCount = 0;

            if (parameters.Length == 0) return false;

            ParameterInfo last = parameters[parameters.Length - 1];
            if (!AstExpression.IsParamsParameter(last)) return false;

            fixedCount = parameters.Length - 1;
            elementType = last.ParameterType.GetElementType();

            if (arguments.Length == parameters.Length)
            {
                Type lastArgType = arguments[arguments.Length - 1].ComputeType();
                if (lastArgType != null && lastArgType != typeof(object) && AreCompatible(lastArgType, last.ParameterType))
                {
                    return false;
                }
            }

            return true;
        }

        private string BuildConstructorMismatchMessage()
        {
            string errorMessage = "";
            foreach (ConstructorInfo constructorInfo in constructors)
            {
                ParameterInfo[] constructorSignature = constructorInfo.GetParameters();
                bool compatible = true;
                int describable = Math.Min(constructorSignature.Length, arguments.Length);
                for (int i = 0; compatible && i < describable; i++)
                {
                    Type paramType = constructorSignature[i].ParameterType;
                    if (paramType.IsEnum && AstExpression.ClassifyEnumArg(arguments[i]) != AstExpression.EnumArgKind.NotEnumBindable)
                    {
                        compatible = AstExpression.IsEnumArgCompatible(paramType, arguments[i]);
                        if (!compatible)
                        {
                            errorMessage = string.Format("You are trying to call the constructor of '{0}' at parameter #{1} with a value that is not a member of enum '{2}'. Expected one of: {3}.", className, i + 1, paramType.Name, string.Join(" or ", Enum.GetNames(paramType)));
                        }
                    }
                    else
                    {
                        Type argType = arguments[i].ComputeType();
                        compatible = AreCompatible(argType, paramType);

                        if (!compatible)
                        {
                            errorMessage = string.Format("You are trying to call the constructor of '{0}' with a value of type '{1}' at parameter #{2}, but a value of type '{3}' is expected. Please correct it.", className, argType.Name, i + 1, paramType.Name);
                        }
                    }
                }
            }

            if (errorMessage.Length == 0)
            {
                // The `constructors` list ended up empty (no constructor matches the
                // arity). That happens in TWO distinct situations that previously produced the
                // same misleading message. We separate them:
                if (!classRegisteredInLibrary)
                {
                    // (a) The class is NOT in the actor's library. We list the loaded
                    // assemblies — the datum that makes a bad library configuration obvious
                    // (e.g. a follower that passed the host assembly and not the domain one).
                    errorMessage = string.Format(
                        "Class '{0}' is not registered in the actor's library. Loaded assemblies: {1}. Known classes: {2}.",
                        className, FormatLoadedAssemblies(), FormatKnownClasses());
                }
                else
                {
                    // (b) The class IS registered, but no constructor accepts this
                    // arity. We list the available constructors instead of lying.
                    errorMessage = string.Format(
                        "Class '{0}' is registered, but no constructor takes {1} argument(s). Available constructors: {2}.",
                        className, arguments.Length, FormatAvailableConstructors());
                }
            }
            return errorMessage;
        }

        private string FormatLoadedAssemblies()
        {
            if (loadedAssemblyNames == null || loadedAssemblyNames.Length == 0) return "(none)";
            return string.Join(", ", loadedAssemblyNames);
        }

        // Bounded preview of the known classes: ordered and limited so the message
        // does not explode when the library loads large assemblies (e.g. the whole engine).
        private string FormatKnownClasses()
        {
            if (knownClassNames == null || knownClassNames.Length == 0) return "(none)";

            const int previewLimit = 25;
            string[] ordered = knownClassNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            if (ordered.Length <= previewLimit)
            {
                return string.Join(", ", ordered);
            }
            return string.Join(", ", ordered.Take(previewLimit)) + $", ... (+{ordered.Length - previewLimit} more)";
        }

        private string FormatAvailableConstructors()
        {
            if (registeredClasses == null || registeredClasses.Count == 0) return "(none)";

            List<string> signatures = new List<string>();
            foreach (var classInfo in registeredClasses)
            {
                foreach (ConstructorInfo constructorInfo in classInfo.Type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!(constructorInfo.IsPublic || constructorInfo.IsAssembly)) continue;
                    signatures.Add(DescribeConstructor(constructorInfo));
                }
            }

            if (signatures.Count == 0) return "(none accessible)";
            return string.Join("; ", signatures);
        }

        private string DescribeConstructor(ConstructorInfo constructorInfo)
        {
            ParameterInfo[] parameters = constructorInfo.GetParameters();
            StringBuilder sb = new StringBuilder();
            sb.Append(className);
            sb.Append('(');
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(parameters[i].ParameterType.Name);
            }
            sb.Append(')');
            return sb.ToString();
        }

    }
}
