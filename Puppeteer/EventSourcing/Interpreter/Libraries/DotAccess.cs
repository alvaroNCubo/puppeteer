using System;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	using System.Collections.Generic;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;

	abstract class DotAccess : AstExpression
	{
		private readonly string methodName;
		private readonly string propertyName;
		private readonly AstExpression[] arguments;

		protected internal DotAccess(string method, AstExpression[] arguments)
		{
			this.methodName = method;
			this.arguments = arguments;
		}

		protected internal DotAccess(string property)
		{
			this.propertyName = property;
		}

		internal string Method()
		{
			return methodName;
		}

		internal string Property()
		{
			return propertyName;
		}

		internal AstExpression[] Arguments()
		{
			return arguments;
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
			bool isMethodNotProperty = methodName != null;
			if (isMethodNotProperty)
			{
				return InvokeMethod();
			}

			FieldInfo fieldInfo = FindField();
			if (fieldInfo != null)
			{
				return ReadField(fieldInfo);
			}

			PropertyInfo propertyInfo = FindProperty();
			if (propertyInfo != null)
			{
				return InvokeGetProperty(propertyInfo);
			}
			throw new LanguageException($"Unknown property or method '{(propertyName == null ? methodName : propertyName)}'.");
		}


		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			var instanceExpr = GetTargetExpression(parametersParam);

			bool isMethodNotProperty = methodName != null;
			if (isMethodNotProperty)
			{
				return InvokeMethodExpression(parametersParam);
			}
			FieldInfo fieldInfo = FindField(instanceExpr.Type);
			if (fieldInfo != null)
			{
				return ReadFieldExpression(parametersParam, fieldInfo);
			}
			PropertyInfo propertyInfo = FindProperty(instanceExpr.Type);
			if (propertyInfo != null)
			{
				return InvokeGetPropertyExpression(parametersParam, propertyInfo);
			}

			// If it's not a property but is "Count" and instance is IEnumerable<T>, use the extension method
			if (string.Equals(propertyName, "Count", StringComparison.OrdinalIgnoreCase) &&
				typeof(System.Collections.IEnumerable).IsAssignableFrom(instanceExpr.Type))
			{
				// Look up the Count<T>(IEnumerable<T>) extension method
				var enumerableType = typeof(System.Linq.Enumerable);
				var countMethod = enumerableType.GetMethods(BindingFlags.Static | BindingFlags.Public)
					.FirstOrDefault(m => m.Name == "Count" && m.GetParameters().Length == 1);
				if (countMethod != null && instanceExpr.Type.IsGenericType)
				{
					var elementType = instanceExpr.Type.GetGenericArguments()[0];
					var genericCount = countMethod.MakeGenericMethod(elementType);
					return Expression.Call(null, genericCount, instanceExpr);
				}
			}
			throw new LanguageException($"Unknown property or method '{(propertyName == null ? methodName : propertyName)}'.");
		}

		private FieldInfo FindField()
		{
			FieldInfo foundField = null;
			object instance = GetTarget();
			foreach (FieldInfo field in instance.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				bool settable = !field.IsInitOnly;
				if (settable && !field.IsPrivate && string.Equals(field.Name, propertyName, StringComparison.OrdinalIgnoreCase))
				{
					foundField = field;
					break;
				}
			}
			return foundField;
		}

		private PropertyInfo FindProperty()
		{
			PropertyInfo foundProperty = null;
			object instance = GetTarget();
			foreach (PropertyInfo property in instance.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				string currentPropertyName = property.Name;
				if (string.Equals(currentPropertyName, propertyName, StringComparison.OrdinalIgnoreCase) && property.GetGetMethod(true) != null)
				{
					ParameterInfo[] variables = property.GetGetMethod(true).GetParameters();

					bool sameArgumentCount =
						(variables.Length == 0 && arguments == null) ||
						(variables.Length == arguments.Length);

					if (sameArgumentCount)
					{
						bool validSignatures = ValidateArgumentSignature(variables);
						if (validSignatures)
						{
							foundProperty = property;
							break;
						}
					}
				}
			}
			return foundProperty;
		}

		private PropertyInfo FindProperty(Type objectClass)
		{
			PropertyInfo foundProperty = null;

			// 1. Look for a normal property.
			foreach (PropertyInfo property in objectClass.GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				string currentPropertyName = property.Name;
				if (string.Equals(currentPropertyName, propertyName, StringComparison.OrdinalIgnoreCase) && property.GetGetMethod(true) != null)
				{
					ParameterInfo[] variables = property.GetGetMethod(true).GetParameters();

					bool sameArgumentCount =
						(variables.Length == 0 && arguments == null) ||
						(arguments != null && variables.Length == arguments.Length);

					if (sameArgumentCount)
					{
						bool validSignatures = ValidateArgumentSignature(variables);
						if (validSignatures)
						{
							foundProperty = property;
							break;
						}
					}
				}
			}

			// 2. If not found, look for special properties on generic interfaces (e.g. Count on ICollection<T>).
			if (foundProperty == null && typeof(System.Collections.IEnumerable).IsAssignableFrom(objectClass))
			{
				// Look for the Count property on ICollection<T>.
				var genericInterfaces = objectClass.GetInterfaces();
				foreach (var iface in genericInterfaces)
				{
					if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(System.Collections.Generic.ICollection<>))
					{
						PropertyInfo prop = iface.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
						.FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));
						if (prop != null && prop.GetGetMethod(true) != null)
						{
							foundProperty = prop;
							break;
						}
					}
				}
			}

			// 3. If still not found, search the implemented interfaces (e.g. Count on ICollection).
			if (foundProperty == null && typeof(System.Collections.IEnumerable).IsAssignableFrom(objectClass))
			{
				foreach (var iface in objectClass.GetInterfaces())
				{
					PropertyInfo prop = iface.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
						.FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));
					if (prop != null && prop.GetGetMethod(true) != null)
					{
						foundProperty = prop;
						break;
					}
				}
			}

			// 4. If still not found and the property is "Count", look for the Count property on ICollection<T>.
			if (foundProperty == null && string.Equals(propertyName, "Count", StringComparison.OrdinalIgnoreCase))
			{
				if (objectClass.IsGenericType)
				{
					var genericDef = objectClass.GetGenericTypeDefinition();
					if (genericDef == typeof(IEnumerable<>))
					{
						var elementType = objectClass.GetGenericArguments()[0];
						var iCollectionType = typeof(ICollection<>).MakeGenericType(elementType);
						var prop = iCollectionType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
						if (prop != null)
						{
							return prop;
						}
					}
				}
				// If not found here, the method-resolution logic will look for the extension method.
				return null;
			}

			return foundProperty;
		}

		protected internal Type ComputeCallExpressionType(Type instanceClass)
		{
			MethodInfo targetMethod = null;

			bool isMethodNotProperty = methodName != null;
			if (isMethodNotProperty)
			{
				targetMethod = FindMethod(instanceClass, GetArgumentSignature(), out _);
			}

			bool methodFound = targetMethod != null;
			if (methodFound)
			{
				if (targetMethod.ReturnType.Equals(typeof(void)))
				{
					return typeof(void);
				}
				return (Type)targetMethod.ReturnType;
			}

			FieldInfo fieldInfo = FindField(instanceClass);
			if (fieldInfo != null)
			{
				return fieldInfo.FieldType;
			}

			PropertyInfo propertyInfo = FindProperty(instanceClass);
			if (propertyInfo != null)
			{
				return propertyInfo.PropertyType;
			}

			// Polymorphic resolution: when instanceClass is abstract or an interface
			// the runtime object is always a concrete subclass. If the method does
			// not live on the declared type, search the assignable concrete
			// subclasses (same assembly as the declared type). This covers the
			// "method Add(...) declared on an abstract base, returning an instance
			// of the concrete subclass where the specific method lives" pattern
			// used by domain catalogs.
			if (isMethodNotProperty && CanHaveConcreteSubclasses(instanceClass))
			{
				Type[] argTypes = GetArgumentSignature();
				foreach (Type derived in EnumerateAssignableConcreteSubclasses(instanceClass))
				{
					MethodInfo hit = FindMethod(derived, argTypes, out _);
					if (hit != null)
					{
						return hit.ReturnType.Equals(typeof(void)) ? typeof(void) : hit.ReturnType;
					}
				}
			}

			throw new LanguageException($"Unknown property or method '{methodName ?? propertyName}' on type '{instanceClass?.Name}'.");
		}

		// Support for polymorphic resolution in the DSL.
		// A variable whose declared type is abstract or an interface ALWAYS
		// references a concrete instance at runtime; the static validator must be
		// able to find members that only live on that concrete subclass. We
		// restrict the search to the assembly where the abstract type is declared:
		// this covers the domain-catalog pattern (same assembly) without
		// triggering a global scan.
		private static bool CanHaveConcreteSubclasses(Type instanceClass)
		{
			return instanceClass != null && (instanceClass.IsAbstract || instanceClass.IsInterface);
		}

		private static IEnumerable<Type> EnumerateAssignableConcreteSubclasses(Type instanceClass)
		{
			if (instanceClass == null) yield break;

			Type[] types;
			try
			{
				types = instanceClass.Assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException ex)
			{
				types = ex.Types.Where(t => t != null).ToArray();
			}
			foreach (Type t in types)
			{
				if (t == null) continue;
				if (t.IsAbstract || t.IsInterface) continue;
				if (!instanceClass.IsAssignableFrom(t)) continue;
				yield return t;
			}
		}

		protected MemberInfo GetResolvedMemberInfo(Type instanceClass)
		{
			MethodInfo targetMethod = null;

			bool isMethodNotProperty = methodName != null;
			if (isMethodNotProperty)
			{
				targetMethod = FindMethod(instanceClass, GetArgumentSignature(), out _);

				if (targetMethod != null) return targetMethod;
			}


			FieldInfo fieldInfo = FindField(instanceClass);
			if (fieldInfo != null)
			{
				return fieldInfo;
			}

			PropertyInfo propertyInfo = FindProperty(instanceClass);
			if (propertyInfo != null)
			{
				return propertyInfo;
			}

			// Same polymorphic fallback as ComputeCallExpressionType: covers the
			// pattern-matching case when the variable's declared type is abstract
			// but the method only lives on the concrete subclass.
			if (isMethodNotProperty && CanHaveConcreteSubclasses(instanceClass))
			{
				Type[] argTypes = GetArgumentSignature();
				foreach (Type derived in EnumerateAssignableConcreteSubclasses(instanceClass))
				{
					MethodInfo hit = FindMethod(derived, argTypes, out _);
					if (hit != null) return hit;
				}
			}

			throw new LanguageException($"Unknown property or method '{methodName ?? propertyName}' on type '{instanceClass?.Name}'.");
		}


		private Type[] GetArgumentSignature()
		{
			Type[] signatures;
			if (arguments != null && arguments.Length != 0)
			{
				signatures = new Type[arguments.Length];
				for (int i = 0; i < arguments.Length; i++)
				{
					AstExpression e = arguments[i];
					signatures[i] = e.ComputeType();
				}
			}
			else
			{
				signatures = new Type[0];
			}
			return signatures;
		}

		private object ReadField(FieldInfo fieldToRead)
		{
			object instance = GetTarget();
			bool fieldFound = fieldToRead != null;
			if (!fieldFound)
			{
				ParserValidation.validacionDeMetodo(instance.GetType(), methodName, GetArgumentSignature());
			}
			object result = fieldToRead.GetValue(instance);
			return result;
		}

		private object InvokeMethod()
		{
			object instance = GetTarget();

			bool isExtensionMethod;
			MethodInfo targetMethod = FindMethod(instance.GetType(), GetArgumentSignature(), out isExtensionMethod);
			bool methodFound = targetMethod != null;
			if (!methodFound)
			{
				ParserValidation.validacionDeMetodo(instance.GetType(), methodName, GetArgumentSignature());
			}
			var parameterValues = BindValuesForMethod(targetMethod, instance, isExtensionMethod);

			object result;

			if (isExtensionMethod)
			{
				Type elementType = instance.GetType().GetGenericArguments()[0];

				targetMethod = targetMethod.MakeGenericMethod(elementType);
				instance = null;
			}
			result = targetMethod.Invoke(instance, parameterValues);

			return result;
		}

		// Replaces the per-argument loop inside InvokeMethodExpression with
		// BindValueExpressionsToParameters.
		private Expression InvokeMethodExpression(ParameterExpression parametersParam)
		{
			var instanceExpr = GetTargetExpression(parametersParam);

			Type[] signatures = GetArgumentSignature();

			bool isExtensionMethod;
			MethodInfo targetMethod = FindMethod(instanceExpr.Type, signatures, out isExtensionMethod);

			// Polymorphic resolution in compiled mode: if the method is not on
			// the declared type and the declared type is abstract/interface,
			// look for it on the assignable concrete subclasses and cast the
			// instanceExpr to the found subclass so Expression.Call accepts the
			// call. If the runtime object is not actually of that concrete
			// subclass, the cast throws InvalidCastException (parallel to the
			// interpreted-mode behaviour, which would use instance.GetType()).
			if (targetMethod == null && CanHaveConcreteSubclasses(instanceExpr.Type))
			{
				foreach (Type derived in EnumerateAssignableConcreteSubclasses(instanceExpr.Type))
				{
					MethodInfo hit = FindMethod(derived, signatures, out isExtensionMethod);
					if (hit != null)
					{
						targetMethod = hit;
						instanceExpr = Expression.Convert(instanceExpr, derived);
						break;
					}
				}
			}

			if (targetMethod == null)
			{
				ParserValidation.validacionDeMetodo(instanceExpr.Type, methodName, signatures);
			}
			var argumentExprs = new List<Expression>();
			var methodParameters = targetMethod.GetParameters();

			if (isExtensionMethod)
			{
				argumentExprs.Add(instanceExpr);
			}
			for (int i = 0; i < arguments.Length; i++)
			{
				int paramIndex = isExtensionMethod ? i : i;
				var paramType = methodParameters[paramIndex].ParameterType;

				if (paramType.IsEnum && arguments[i] is Id idArg)
				{
					string enumName = idArg.Name;
					Expression enumNameExpr = Expression.Constant(enumName, typeof(string));
					Expression enumTypeExpr = Expression.Constant(paramType, typeof(Type));
					Expression parseCall = Expression.Call(
						typeof(Enum).GetMethod(nameof(Enum.Parse), new[] { typeof(Type), typeof(string), typeof(bool) }),
						enumTypeExpr,
						enumNameExpr,
						Expression.Constant(true, typeof(bool))
					);
					argumentExprs.Add(Expression.Convert(parseCall, paramType));
				}
				else
				{
					var argExpr = arguments[i].ExecuteExpression(parametersParam);
					argumentExprs.Add(argExpr);
				}
			}

			// Use BindValueExpressionsToParameters to obtain the converted arguments.
			var argumentExprArray = argumentExprs.ToArray();
			var convertedArguments = AstExpression.BindValueExpressionsToParameters(methodParameters, argumentExprArray);

			// Convert the returned objects to Expressions (the method may return object[]).
			var finalArguments = new List<Expression>();
			foreach (var arg in convertedArguments)
			{
				if (arg is Expression expr)
					finalArguments.Add(expr);
				else
					finalArguments.Add(Expression.Constant(arg));
			}

			if (isExtensionMethod)
			{
				Type elementType = instanceExpr.Type.GetGenericArguments()[0];
				targetMethod = targetMethod.MakeGenericMethod(elementType);
				return Expression.Call(null, targetMethod, finalArguments);
			}
			return Expression.Call(instanceExpr, targetMethod, finalArguments);
		}

		private object InvokeGetProperty(PropertyInfo propertyToInvoke)
		{
			object instance = GetTarget();
			bool methodFound = propertyToInvoke != null;
			if (!methodFound)
			{
				ParserValidation.validacionDeMetodo(instance.GetType(), methodName, GetArgumentSignature());
			}
			object result = propertyToInvoke.GetValue(instance, BindValuesForPropertyGet(propertyToInvoke));
			return result;
		}

		private bool CheckMethodNameAndParameters(MethodInfo method)
		{
			string methodName = method.Name;
			if (string.Equals(methodName, this.methodName, StringComparison.OrdinalIgnoreCase))
			{
				ParameterInfo[] variables = method.GetParameters();

				bool sameArgumentCount = variables.Length == arguments.Length;

				if (sameArgumentCount)
				{
					bool validSignatures = ValidateArgumentSignature(variables);
					if (validSignatures)
					{
						return true;
					}
				}
			}
			return false;
		}

		private bool CheckMethodNameAndParametersExtension(MethodInfo method)
		{
			string methodName = method.Name;
			if (string.Equals(methodName, this.methodName, StringComparison.OrdinalIgnoreCase))
			{
				ParameterInfo[] fullVariables = method.GetParameters();

				ParameterInfo[] variables = new ParameterInfo[fullVariables.Length - 1];

				Array.Copy(fullVariables, 1, variables, 0, variables.Length);


				bool sameArgumentCount = variables.Length == arguments.Length;

				if (sameArgumentCount)
				{
					bool validSignatures = ValidateArgumentSignature(variables);
					if (validSignatures)
					{
						return true;
					}
				}
			}
			return false;
		}

		protected internal PropertyInfo FindPropertyByName(Type objectClass, string property, bool lookupSetter = false)
		{
			PropertyInfo foundProperty = null;
			foreach (PropertyInfo prop in objectClass.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				MethodInfo accessorMethod = lookupSetter ? prop.GetSetMethod(true) : prop.GetGetMethod(true);
				if (accessorMethod != null && (accessorMethod.IsPublic || accessorMethod.IsAssembly))
				{
					if (string.Equals(prop.Name, property, StringComparison.OrdinalIgnoreCase))
					{
						ParameterInfo[] parameters = accessorMethod.GetParameters();
						bool validArgumentCount;

						if (lookupSetter)
						{
							validArgumentCount =
								(parameters.Length == 1 && (arguments == null || arguments.Length == 1));
						}
						else
						{
							validArgumentCount =
								(parameters.Length == 0 && arguments == null) ||
								(arguments != null && parameters.Length == arguments.Length);
						}

						if (validArgumentCount)
						{
							bool validSignatures = true;
							if (!lookupSetter)
							{
								validSignatures = ValidateArgumentSignature(parameters);
							}
							else if (arguments != null && arguments.Length == 1)
							{
								validSignatures = NewInstance.AreCompatible(arguments[0].ComputeType(), parameters[0].ParameterType);
							}
							if (validSignatures)
							{
								foundProperty = prop;
								break;
							}
						}
					}
				}
			}
			return foundProperty;
		}

		protected internal FieldInfo FindField(Type objectClass)
		{
			FieldInfo foundField = null;
			foreach (FieldInfo field in objectClass.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				bool settable = !field.IsInitOnly;
				if (settable && !field.IsPrivate && string.Equals(field.Name, propertyName, StringComparison.OrdinalIgnoreCase))
				{
					foundField = field;
					break;
				}
			}
			return foundField;
		}

		protected internal MethodInfo FindMethod(Type objectClass, Type[] parameterTypes, out bool isExtensionMethod)
		{
			isExtensionMethod = false;
			MethodInfo foundMethod = null;

			// 1. Look for a regular instance method.
			foreach (MethodInfo method in objectClass.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				if (string.Equals(method.Name, this.methodName, StringComparison.OrdinalIgnoreCase))
				{
					var parameters = method.GetParameters();
					if (parameters.Length == parameterTypes.Length)
					{
						bool match = true;
						for (int i = 0; i < parameters.Length; i++)
						{
							var paramType = parameters[i].ParameterType;
							var argType = parameterTypes[i];

							if (paramType.IsEnum && arguments[i] is Id idArg)
							{
								string enumName = idArg.Name;
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
						if (match)
						{
							foundMethod = method;
							return foundMethod;
						}
					}
				}
			}

			// 2. If the type is IEnumerable<T>, look for extension methods on Enumerable and for special properties.
			if (typeof(System.Collections.IEnumerable).IsAssignableFrom(objectClass))
			{
				// a) Map "Count" to the Count property on ICollection<T>.
				if (string.Equals(this.methodName, "Count", StringComparison.OrdinalIgnoreCase) && parameterTypes.Length == 0)
				{
					// Look for the Count property on ICollection<T>.
					var iCollectionType = objectClass.GetInterface("ICollection`1");
					if (iCollectionType != null)
					{
						var prop = iCollectionType.GetProperty("Count");
						if (prop != null)
						{
							// Return the get_Count method as MethodInfo.
							foundMethod = prop.GetGetMethod();
							return foundMethod;
						}
					}
					// If it is not ICollection<T>, look for the Count() extension method on Enumerable.
					foreach (var method in typeof(System.Linq.Enumerable).GetMethods(BindingFlags.Static | BindingFlags.Public))
					{
						if (method.Name == "Count" && method.GetParameters().Length == 1)
						{
							foundMethod = method;
							isExtensionMethod = true;
							return foundMethod;
						}
					}
				}

				// b) Look for other extension methods on Enumerable.
				foreach (var method in typeof(System.Linq.Enumerable).GetMethods(BindingFlags.Static | BindingFlags.Public))
				{
					if (string.Equals(method.Name, this.methodName, StringComparison.OrdinalIgnoreCase))
					{
						var parameters = method.GetParameters();
						if (parameters.Length == parameterTypes.Length + 1) // +1 for the 'this' parameter
						{
							bool match = true;
							for (int i = 1; i < parameters.Length; i++)
							{
								if (!parameters[i].ParameterType.IsAssignableFrom(parameterTypes[i - 1]))
								{
									match = false;
									break;
								}
							}
							if (match)
							{
								foundMethod = method;
								isExtensionMethod = true;
								return foundMethod;
							}
						}
					}
				}
			}

			return foundMethod;
		}


		private object[] BindValuesForMethod(MethodInfo method, object instance, bool isExtensionMethod)
		{
			object[] evaluatedArguments;
			int startIndex = 0;
			if (isExtensionMethod)
			{
				evaluatedArguments = new object[this.arguments.Length + 1];

				evaluatedArguments[0] = instance;

				startIndex = 1;
			}
			else
			{
				evaluatedArguments = new object[this.arguments.Length];
				startIndex = 0;
			}

			for (int i = 0; i < this.arguments.Length; i++)
			{
				ParameterInfo parameterInfo = method.GetParameters()[i];
				bool argIsEnumIdentifier = parameterInfo.ParameterType.IsEnum && arguments[i].GetType() == typeof(Id);
				if (!argIsEnumIdentifier)
				{
					evaluatedArguments[startIndex++] = this.arguments[i].Execute();
				}
				else
				{
					evaluatedArguments[startIndex++] = this.arguments[i];
				}
			}
			var result = BindValuesToParameters(method.GetParameters(), evaluatedArguments);

			return result;
		}

		private object[] BindValuesForPropertyGet(PropertyInfo property)
		{
			return BindValuesToParameters(property.GetGetMethod(true).GetParameters(), this.arguments);
		}

		internal bool ValidateArgumentSignature(ParameterInfo[] methodSignature)
		{
			bool compatible =
				(methodSignature.Length == 0 && arguments == null) ||
				(arguments.Length == methodSignature.Length);
			for (int i = 0; compatible && i < methodSignature.Length; i++)
			{
				Type paramType = methodSignature[i].ParameterType;

				bool argIsEnumIdentifier = paramType.IsEnum && arguments[i].GetType() == typeof(Id);

				if (argIsEnumIdentifier)
				{
					bool enumCompatible = true;
					try
					{
						Type enumType = paramType;
						string enumName = ((Id)arguments[i]).Name;
						bool enumNameFound = false;

						foreach (string enumValueName in Enum.GetNames(enumType))
						{
							if (string.Equals(enumValueName, enumName, StringComparison.OrdinalIgnoreCase))
							{
								enumNameFound = true;
								break;
							}
						}

						if (!enumNameFound)
						{
							string enumValues = "";
							foreach (string s in Enum.GetNames(enumType))
							{
								enumValues = enumValues + s + " or ";
							}

							enumValues = enumValues.Substring(0, enumValues.Length - 4);

							throw new LanguageException(string.Format("You are trying to assign a value of type '{0}' but wrote '{1}'; the expected value is one of: {2}.", enumType.Name, enumName, enumValues));
						}
					}
					catch (System.ArgumentException e)
					{
						throw new LanguageException(e.Message);
					}
					compatible = enumCompatible;
				}
				else
				{
					var argType = arguments[i].ComputeType();
					compatible = NewInstance.AreCompatible(argType, paramType);
				}
			}
			return compatible;
		}

		protected internal abstract object GetTarget();
		protected internal abstract Expression GetTargetExpression(ParameterExpression parametersParam);


		private Expression ReadFieldExpression(ParameterExpression parametersParam, FieldInfo fieldInfo)
		{
			var instanceExpr = GetTargetExpression(parametersParam);
			return Expression.Field(instanceExpr, fieldInfo);
		}

		private Expression InvokeGetPropertyExpression(ParameterExpression parametersParam, PropertyInfo propertyInfo)
		{
			var instanceExpr = GetTargetExpression(parametersParam);
			var argumentExprs = new List<Expression>();
			if (arguments != null)
			{
				foreach (var arg in arguments)
				{
					argumentExprs.Add(arg.ExecuteExpression(parametersParam));
				}
			}

			// Special handling for ICollection<T>.Count when the instance is IEnumerable<T>.
			if (string.Equals(propertyInfo.Name, "Count", StringComparison.OrdinalIgnoreCase))
			{
				var declaringType = propertyInfo.DeclaringType;
				var instanceType = instanceExpr.Type;

				// When the Count property lives on ICollection<T> but the instance is IEnumerable<T>.
				if (declaringType != null
					&& declaringType.IsGenericType
					&& declaringType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.ICollection<>)
					&& instanceType.IsGenericType
					&& instanceType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>))
				{
					// Use Enumerable.Count<T>(IEnumerable<T>) instead of accessing the property.
					var elementType = instanceType.GetGenericArguments()[0];
					var enumerableType = typeof(System.Linq.Enumerable);
					var countMethod = enumerableType.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
						.FirstOrDefault(m => m.Name == "Count" && m.GetParameters().Length == 1);
					if (countMethod != null)
					{
						var genericCount = countMethod.MakeGenericMethod(elementType);
						return Expression.Call(null, genericCount, instanceExpr);
					}
				}
			}

			return Expression.Property(instanceExpr, propertyInfo, argumentExprs);
		}
		protected List<object> GetArgumentValues()
		{
			List<object> values = new List<object>();
			if (arguments != null)
			{
				foreach (var arg in arguments)
				{
					if (arg is LiteralBoolean || arg is LiteralDecimal || arg is LiteralDouble || arg is LiteralDateTime || arg is LiteralString || arg is LiteralList || arg is LiteralNull || arg is LiteralNumber)
					{
						object value = arg.Execute();
						values.Add(value);
					}
					else if (arg is Id id)
					{
						// If it is a parameter, execute it to obtain the value.
						if (id.IsParameter)
						{
							// If it is an OUT parameter, use a special marker instead of the value.
							// Wildcards still match, but literal matches are blocked.
							if (id.Parameter != null && id.Parameter.ParameterModifier == Parameter.Out)
							{
								Type argType = arg.ComputeType();
								values.Add(new Follower.OutParameterMarker(argType, id.Name));
							}
							else
							{
								object value = arg.Execute();
								values.Add(value);
							}
						}
						else
						{
							// Not a parameter — only the type is known (no value), so use a placeholder.
							Type argType = arg.ComputeType();
							values.Add(new Follower.TypedValuePlaceholder(argType, id.Name));
						}
					}
				}
			}
			return values;
		}

	}
}
