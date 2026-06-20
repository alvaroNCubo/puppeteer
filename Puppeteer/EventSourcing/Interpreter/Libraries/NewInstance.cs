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

		// Estado capturado en CollectConstructors solo para el diagnostico de
		// BuildConstructorMismatchMessage: distingue "clase ausente de la libreria"
		// de "clase presente pero sin constructor de esa aridad", y lista lo que la
		// libreria SI cargo. Sin esto ambas causas emitian el mismo mensaje enganoso.
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
				// Simbolo pelado -> Enum.Parse(Name) sin ExecuteExpression (no tiene scope);
				// string con valor (parametro/variable/literal) -> Enum.Parse(valor). El helper
				// elige la fuente del nombre segun el tipo del argumento.
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

		// Binding compilado de un constructor params expandido: liga los fijos por la ruta normal y
		// emite Expression.NewArrayInit del element-type con cada trailing convertido por la ruta
		// escalar (numerica via CoerceScalarExpression + enum via ParseEnumArgExpression).
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

        // Binding interpretado de un constructor params expandido: liga los parametros fijos por la
        // ruta escalar normal y arma el arreglo del element-type coercionando cada elemento trailing
        // por la misma ruta escalar (numerica + enum). Paralelo a DotAccess.BindParamsExpansionInterpreted.
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

		// Capturamos el contexto de la libreria para el diagnostico de clase ausente.
		// Es barato (lista de nombres) y solo se usa si la resolucion falla.
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
			// Sin clausula 'in': aceptamos todos los candidatos por nombre simple. La
			// ambiguedad por homonimia entre namespaces NO se decide aqui — se difiere
			// a ResolveConstructor (via ComputeType/FindMatchingConstructor), donde se
			// filtra por compatibilidad de la firma del constructor. Cuando los tipos
			// de los argumentos identifican una sola clase, no hay ambiguedad real y
			// la instanciacion procede; si la firma matchea constructores de varios
			// namespaces, alli se lanza el error de "Ambiguous reference" con el detalle
			// correcto (firma + namespaces que la satisfacen).
			candidateClasses = classInfos;
		}

		// Conservamos las clases candidatas (por nombre/namespace) para que, si NINGUN
		// constructor coincide con la aridad, el diagnostico pueda listar los constructores
		// realmente disponibles en lugar de mentir con "no class with that name is registered".
		registeredClasses = candidateClasses;

		foreach (var classInfo in candidateClasses)
		{
			foreach (ConstructorInfo constructorInfo in classInfo.Type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				ParameterInfo[] constructorParameters = constructorInfo.GetParameters();
				int parameterCount = constructorParameters.Length;
				bool sameArgumentCount = parameterCount == arguments.Length;

				// Candidato params: ultimo parametro params y al menos N-1 argumentos. Asi una
				// llamada de aridad variable tambien colecciona el constructor params aunque su
				// numero de parametros no coincida exactamente con el numero de argumentos.
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

            // Seleccion determinista: primero los constructores no-params de aridad exacta. Solo
            // si ninguno matchea se consideran los params. Asi un constructor no-params de aridad
            // exacta SIEMPRE gana sobre la variante params (paralelo a FindMethod en DotAccess).
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

            // Ruta no-params: se conserva la seleccion historica "primer compatible en orden del
            // CLR" para no alterar la desambiguacion numerica ya establecida entre sobrecargas de
            // coleccion (p.ej. List<double> liga tanto a List<double> como a List<decimal>).
            // Ruta params: pick determinista (no depende del orden del CLR).
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

        // Compatibilidad no-params de aridad exacta. Replica EXACTAMENTE la logica historica de
        // IsConstructorCompatibleWithArguments (sin el guard de null/object de la ruta params) para
        // no alterar que constructores se consideran compatibles en la ruta no-params.
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

        // Match de un constructor params: ultimo parametro params, al menos N-1 argumentos, los
        // fijos compatibles, y los trailing compatibles como elementos del element-type (forma
        // expandida) o como el arreglo ya armado pasado directo (forma directa). Conservadora.
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

        // Decide si la llamada al constructor debe EXPANDIR los trailing en un arreglo nuevo, o si
        // el arreglo se paso directo. Misma regla que en DotAccess.UsesParamsExpansion.
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
                // La lista `constructors` quedo vacia (ningun constructor coincide con la
                // aridad). Eso ocurre en DOS situaciones distintas que antes producian el
                // mismo mensaje enganoso. Las separamos:
                if (!classRegisteredInLibrary)
                {
                    // (a) La clase NO esta en la libreria del actor. Listamos los assemblies
                    // cargados — el dato que hace obvia una mala configuracion de librerias
                    // (p.ej. un follower que paso el assembly de la app y no el del dominio).
                    errorMessage = string.Format(
                        "Class '{0}' is not registered in the actor's library. Loaded assemblies: {1}. Known classes: {2}.",
                        className, FormatLoadedAssemblies(), FormatKnownClasses());
                }
                else
                {
                    // (b) La clase SI esta registrada, pero ningun constructor acepta esta
                    // aridad. Listamos los constructores disponibles en vez de mentir.
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

        // Preview acotado de las clases conocidas: ordenado y limitado para que el mensaje
        // no explote cuando la libreria carga assemblies grandes (p.ej. el motor completo).
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
