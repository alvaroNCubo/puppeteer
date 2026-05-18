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
			if (constructors == null) throw new LanguageException($"Constructors have not been collected for class '{className}'. CollectConstructors must be called before ComputeType.");

			Type[] argumentTypes = arguments.Select(arg => arg.ComputeType()).ToArray();
			ConstructorInfo validConstructor = null;

			foreach (var constructor in constructors)
			{
				var parameters = constructor.GetParameters();
				if (parameters.Length != argumentTypes.Length)
					continue;

				bool match = true;
				for (int i = 0; i < parameters.Length; i++)
				{
					var paramType = parameters[i].ParameterType;
					var argType = argumentTypes[i];

					if (paramType.IsEnum && arguments[i] is Id)
					{
						string enumName = ((Id)arguments[i]).Name;
						if (!Enum.GetNames(paramType).Any(n => string.Equals(n, enumName, StringComparison.OrdinalIgnoreCase)))
						{
							match = false;
							break;
						}
					}
					else if (!AreCompatible(argType, paramType))
					{
						match = false;
						break;
					}
				}
				if (match && (constructor.IsPublic || constructor.IsAssembly))
				{
					validConstructor = constructor;
					break;
				}
			}

			if (validConstructor == null)
				throw new LanguageException($"No public or internal constructor compatible with the given arguments was found for class '{className}'. Argument types: [{string.Join(", ", argumentTypes.Select(t => t?.Name ?? "null"))}].");

			return validConstructor.DeclaringType;
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

			// Build the argument expressions.
			Expression[] args = new Expression[arguments.Length];
			for (int i = 0; i < arguments.Length; i++)
			{
				Type parameterType = parameters[i].ParameterType;
				Expression argExpr;

				// If the parameter is Enum and the argument is an Id, convert the symbolic name to the Enum value.
				// IMPORTANT: do NOT call arguments[i].ExecuteExpression(...) in this branch — the Id has no scope
				// (it is neither a local, global, nor parameter), and AllocateStorageExpression would throw
				// "Cannot generate an Expression for Id '...' because its scope is undefined".
				if (parameterType.IsEnum && arguments[i] is Id idArg)
				{
					// Build an expression that calls Enum.Parse(enumType, idArg.Name, true).
					Expression enumNameExpr = Expression.Constant(idArg.Name, typeof(string));
					Expression enumTypeExpr = Expression.Constant(parameterType, typeof(Type));
					Expression parseCall = Expression.Call(
						typeof(Enum).GetMethod(nameof(Enum.Parse), new[] { typeof(Type), typeof(string), typeof(bool) }),
						enumTypeExpr,
						enumNameExpr,
						Expression.Constant(true)
					);
					// Convert the result back to the enum type.
					argExpr = Expression.Convert(parseCall, parameterType);
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
				args[i] = argExpr;
			}

			// Build the new-instance expression.
			return Expression.New(constructor, args);
		}

        private object InstantiateObject()
        {
            ConstructorInfo constructor = FindMatchingConstructor();
            ParameterInfo[] constructorSignature = constructor.GetParameters();

            object[] evaluatedArguments = new object[this.arguments.Length];

            for (int i = 0; i < this.arguments.Length; i++)
            {
                // If the parameter is an enum and the argument is a bare Id (symbolic enum-value
                // reference such as 'Text' for InputType.Text), pass the AST node itself so
                // BindValuesToParameters can convert it via Enum.TryParse.
                // Calling Execute() on the Id would throw because the variable was never defined.
                ParameterInfo parameterInfo = constructorSignature[i];
                bool argIsEnumIdentifier = parameterInfo.ParameterType.IsEnum && arguments[i].GetType() == typeof(Id);
                if (argIsEnumIdentifier)
                {
                    evaluatedArguments[i] = this.arguments[i];
                }
                else
                {
                    evaluatedArguments[i] = this.arguments[i].Execute();
                }
            }

            object[] constructorArgValues = DotAccess.BindValuesToParameters(constructorSignature, evaluatedArguments);

            try
            {
                return (object)constructor.Invoke(constructorArgValues);
            }
            catch (Exception e)
            {
                throw new LanguageException($"Error while instantiating class '{className}'. Details: {e.Message}");
            }
        }

	private void CollectConstructors(DomainLibraries libraries)
	{
		constructors = new List<ConstructorInfo>();

		if (!libraries.TryFindClassesByName(className, out var classInfosEnumerable))
		{
			return;
		}

		var classInfos = classInfosEnumerable.ToList();

		if (classInfos.Count == 0)
		{
			return;
		}
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
			if (classInfos.Count > 1)
			{
				var namespaces = string.Join(", ", classInfos.Select(ci => ci.Namespace).Distinct());
				throw new LanguageException($"Ambiguous reference: class '{className}' exists in multiple namespaces: {namespaces}. Use the 'in' clause to specify the namespace (e.g. ClassName(...) in MyNamespace).");
			}
			candidateClasses = classInfos;
		}

		foreach (var classInfo in candidateClasses)
		{
			foreach (ConstructorInfo constructorInfo in classInfo.Type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				int parameterCount = constructorInfo.GetParameters().Length;
				bool sameArgumentCount = parameterCount == arguments.Length;

				bool isPublicOrInternal = constructorInfo.IsPublic || constructorInfo.IsAssembly;
				if (sameArgumentCount && isPublicOrInternal)
				{
					constructors.Add(constructorInfo);
				}
			}
		}
	}

        private ConstructorInfo FindMatchingConstructor()
        {
            foreach (ConstructorInfo constructorInfo in constructors)
            {
                ParameterInfo[] constructorSignature = constructorInfo.GetParameters();
                bool compatible = true;
                int parameterCount = arguments.Length;
                for (int i = 0; compatible && i < parameterCount; i++)
                {
                    Type paramType = constructorSignature[i].ParameterType;
                    bool argIsEnumIdentifier = paramType.IsEnum && arguments[i].GetType() == typeof(Id);
                    if (argIsEnumIdentifier)
                    {
                        bool enumCompatible = false;
                        try
                        {
                            Type enumType = paramType;
                            string enumName = ((Id)arguments[i]).Name;

                            foreach (string enumValueName in Enum.GetNames(enumType))
                            {
                                if (string.Equals(enumValueName, enumName, StringComparison.OrdinalIgnoreCase))
                                {
                                    enumCompatible = true;
                                    break;
                                }
                            }
                        }
                        catch (System.ArgumentException)
                        {
                            enumCompatible = false;
                        }
                        compatible = enumCompatible;
                    }
                    else
                    {
                        var argType = arguments[i].ComputeType();
                        compatible = AreCompatible(argType, paramType);
                    }
                }
                if (compatible)
                {
                    return constructorInfo;
                }
            }

            throw new LanguageException(BuildConstructorMismatchMessage());
        }

        private string BuildConstructorMismatchMessage()
        {
            string errorMessage = "";
            foreach (ConstructorInfo constructorInfo in constructors)
            {
                ParameterInfo[] constructorSignature = constructorInfo.GetParameters();
                bool compatible = true;
                for (int i = 0; compatible && i < constructorSignature.Length; i++)
                {
                    Type paramType = constructorSignature[i].ParameterType;
                    bool argIsEnumIdentifier = paramType.IsEnum && arguments[i].GetType() == typeof(Id);
                    if (argIsEnumIdentifier)
                    {
                        bool enumCompatible = false;
                        try
                        {
                            Type enumType = paramType;
                            string enumName = ((Id)arguments[i]).Name;

                            foreach (string enumValueName in Enum.GetNames(enumType))
                            {
                                if (string.Equals(enumValueName, enumName, StringComparison.OrdinalIgnoreCase))
                                {
                                    enumCompatible = true;
                                    break;
                                }
                            }
                        }
                        catch (System.ArgumentException)
                        {
                            enumCompatible = false;
                        }
                        compatible = enumCompatible;
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
                errorMessage = string.Format("You are trying to call the constructor of '{0}' but no class with that name (inheriting from Objeto) is registered in the library. Please verify that it exists.", className);
            }
            return errorMessage;
        }

    }
}
