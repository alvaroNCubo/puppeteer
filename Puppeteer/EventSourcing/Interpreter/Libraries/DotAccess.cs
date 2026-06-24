using System;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	using System.Collections.Concurrent;
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

		// --- Process-wide shared method resolution cache -----------------------------------
		// FindMethod scans Type.GetMethods(...) (1 to 3 passes) + overload matching on EVERY
		// invocation. During rehydration that is the bulk of the exec cost (~325us/event, the
		// bottleneck stage). Since each journal entry is parsed into a fresh AST and executed
		// once, caching the MethodInfo ON THE NODE does not help: the cache must be shared and
		// keyed stably across entries.
		//
		// FindMethod resolution depends EXCLUSIVELY on:
		//   (runtime objectClass, methodName, argument types, and for args that are Id: their
		//    name — the enum-by-Id branch matches by Enum.GetNames(idName), not by type).
		// The key captures those four components, so the cache is semantically exact (it does
		// not change which overload is chosen). It does NOT cache when some argType is
		// null/object: those are the ambiguous territory of static validation (overload
		// relaxation), rare in exec and safer to resolve fresh.
		private readonly struct MethodResolutionKey : IEquatable<MethodResolutionKey>
		{
			private readonly Type objectClass;
			private readonly string methodName;
			private readonly Type[] argTypes;
			private readonly string[] idArgNames; // null if no argument is an Id; idArgNames[i]!=null marks an Id

			internal MethodResolutionKey(Type objectClass, string methodName, Type[] argTypes, string[] idArgNames)
			{
				this.objectClass = objectClass;
				this.methodName = methodName;
				this.argTypes = argTypes;
				this.idArgNames = idArgNames;
			}

			public bool Equals(MethodResolutionKey other)
			{
				if (objectClass != other.objectClass) return false;
				if (!string.Equals(methodName, other.methodName, StringComparison.OrdinalIgnoreCase)) return false;
				if (argTypes.Length != other.argTypes.Length) return false;
				for (int i = 0; i < argTypes.Length; i++)
				{
					if (argTypes[i] != other.argTypes[i]) return false;
				}
				bool thisHasIds = idArgNames != null;
				bool otherHasIds = other.idArgNames != null;
				if (thisHasIds != otherHasIds) return false;
				if (thisHasIds)
				{
					for (int i = 0; i < idArgNames.Length; i++)
					{
						if (!string.Equals(idArgNames[i], other.idArgNames[i], StringComparison.OrdinalIgnoreCase)) return false;
					}
				}
				return true;
			}

			public override bool Equals(object obj) => obj is MethodResolutionKey other && Equals(other);

			public override int GetHashCode()
			{
				HashCode hc = new HashCode();
				hc.Add(objectClass);
				hc.Add(methodName, StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < argTypes.Length; i++) hc.Add(argTypes[i]);
				if (idArgNames != null)
				{
					for (int i = 0; i < idArgNames.Length; i++)
					{
						hc.Add(idArgNames[i] == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(idArgNames[i]));
					}
				}
				return hc.ToHashCode();
			}
		}

		private readonly struct ResolvedMethod
		{
			internal readonly MethodInfo Method;
			internal readonly bool IsExtension;
			internal ResolvedMethod(MethodInfo method, bool isExtension)
			{
				Method = method;
				IsExtension = isExtension;
			}
		}

		// Process-wide shared: resolution (type, name, signature) is global, it does not depend
		// on the Actor. Bounded by the number of distinct (type, method, signature) combos,
		// which is small compared to the millions of journal events.
		private static readonly ConcurrentDictionary<MethodResolutionKey, ResolvedMethod> methodResolutionCache
			= new ConcurrentDictionary<MethodResolutionKey, ResolvedMethod>();

		// Memo: does the method (by name) on this type have ANY overload with an enum parameter?
		// Only then can a string argument compete between foo(Enum) and foo(string), and the
		// cache key (which for non-Id args does not distinguish a 'Febrero' literal from
		// (string)'Febrero') is not enough to separate the resolutions. For types/methods with
		// no enum overload (the common case, hot path) it returns false instantly and affects
		// nothing.
		private static readonly ConcurrentDictionary<(Type, string), bool> methodHasEnumOverloadCache
			= new ConcurrentDictionary<(Type, string), bool>();

		private static bool TypeMethodHasEnumOverload(Type objectClass, string methodName)
		{
			var key = (objectClass, methodName.ToLowerInvariant());
			if (methodHasEnumOverloadCache.TryGetValue(key, out bool cached)) return cached;

			bool result = false;
			foreach (MethodInfo m in objectClass.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				if (!string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)) continue;
				foreach (ParameterInfo p in m.GetParameters())
				{
					if (p.ParameterType.IsEnum) { result = true; break; }
				}
				if (result) break;
			}
			methodHasEnumOverloadCache.TryAdd(key, result);
			return result;
		}

		protected internal MethodInfo FindMethodCached(Type objectClass, Type[] parameterTypes, out bool isExtensionMethod)
		{
			bool cacheable = true;
			for (int i = 0; i < parameterTypes.Length; i++)
			{
				if (parameterTypes[i] == null || parameterTypes[i] == typeof(object))
				{
					cacheable = false;
					break;
				}
			}

			// Enum carve-out: a NON-Id string argument (literal 'Febrero' or (string)'Febrero')
			// on a method with an enum overload is not cacheable — the key does not separate the
			// literal (which prefers enum) from the cast to string. String Ids stay distinguished
			// by idArgNames in the key, so they remain cacheable.
			if (cacheable && arguments != null && TypeMethodHasEnumOverload(objectClass, methodName))
			{
				for (int i = 0; i < arguments.Length && i < parameterTypes.Length; i++)
				{
					if (parameterTypes[i] == typeof(string) && !(arguments[i] is Id))
					{
						cacheable = false;
						break;
					}
				}
			}

			if (!cacheable)
			{
				if (Puppeteer.LabInstrumentation.StageTimingEnabled) Puppeteer.LabInstrumentation.IncrementMethodCacheUncacheable();
				return FindMethod(objectClass, parameterTypes, out isExtensionMethod);
			}

			string[] idArgNames = null;
			if (arguments != null)
			{
				for (int i = 0; i < arguments.Length && i < parameterTypes.Length; i++)
				{
					if (arguments[i] is Id idArg)
					{
						if (idArgNames == null) idArgNames = new string[parameterTypes.Length];
						idArgNames[i] = idArg.Name;
					}
				}
			}

			MethodResolutionKey key = new MethodResolutionKey(objectClass, methodName, parameterTypes, idArgNames);
			if (methodResolutionCache.TryGetValue(key, out ResolvedMethod cached))
			{
				if (Puppeteer.LabInstrumentation.StageTimingEnabled) Puppeteer.LabInstrumentation.IncrementMethodCacheHit();
				isExtensionMethod = cached.IsExtension;
				return cached.Method;
			}

			if (Puppeteer.LabInstrumentation.StageTimingEnabled) Puppeteer.LabInstrumentation.IncrementMethodCacheMiss();
			MethodInfo resolved = FindMethod(objectClass, parameterTypes, out isExtensionMethod);
			if (resolved != null)
			{
				methodResolutionCache.TryAdd(key, new ResolvedMethod(resolved, isExtensionMethod));
			}
			return resolved;
		}

		// B.3.1: include the method/property name + arg shape. The arguments'
		// AccumulatePromotionCandidateHash recurses into literal nodes (whose default
		// contribution is just their type name), so two calls with the same
		// shape but different literal arg values hash identically.
		internal override void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(this.GetType().Name);
			hc.Add(methodName ?? string.Empty);
			hc.Add(propertyName ?? string.Empty);
			if (arguments != null)
			{
				hc.Add(arguments.Length);
				foreach (AstExpression e in arguments)
				{
					e.AccumulatePromotionCandidateHash(ref hc);
				}
			}
			else
			{
				hc.Add(-1);
			}
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
			bool isMethodNotProperty = methodName != null;

			// Static method call: the receiver is a class, not a value. GetTargetExpression must
			// not be generated (it would try to assign storage to the class Id, whose scope is
			// undefined in compiled mode). It is resolved directly through the static path.
			if (isMethodNotProperty && ResolveStaticReceiverType() != null)
			{
				return InvokeMethodExpression(parametersParam);
			}

			var instanceExpr = GetTargetExpression(parametersParam);

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

			// Polymorphic resolution in compiled mode: when the declared type is
			// abstract/interface and the field or property lives on a concrete
			// subclass, cast the instance to that subclass before generating the
			// member access. If the runtime object is not actually of that subclass
			// Expression.Convert will throw InvalidCastException at runtime — same
			// behaviour the interpreted mode gives when looking up via instance.GetType().
			if (CanHaveConcreteSubclasses(instanceExpr.Type))
			{
				foreach (Type derived in EnumerateAssignableConcreteSubclasses(instanceExpr.Type))
				{
					FieldInfo derivedField = FindField(derived);
					if (derivedField != null)
					{
						return Expression.Field(Expression.Convert(instanceExpr, derived), derivedField);
					}
					PropertyInfo derivedProperty = FindProperty(derived);
					if (derivedProperty != null)
					{
						return Expression.Property(Expression.Convert(instanceExpr, derived), derivedProperty);
					}
				}
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
			// Guard for an untyped receiver during static validation. It shows up when the
			// chain's instance.ComputeType() returns null — a known case is journal rehydration
			// when the receiver is a global declared in a previous entry whose SymbolTable does
			// not yet have the type resolved. Without this guard FindMethod/FindField/FindProperty
			// fall into objectClass.GetMethods(...) with null and blow up with a
			// NullReferenceException with no usable context in the resolver task log. Converting it
			// into a LanguageException with the member name preserves the permissive flow of the
			// rehydration pipeline (log + continue with the next entry) with an actionable message.
			if (instanceClass == null)
			{
				throw new LanguageException($"Cannot resolve '{methodName ?? propertyName}' because the receiver's type could not be determined during static validation. The receiver expression resolves to a null type — likely a global variable used here whose declaration is in a previous journal entry that the rehydration pipeline has not finished resolving yet.");
			}

			bool isStatic = ResolveStaticReceiverType() != null;

			MethodInfo targetMethod = null;

			bool isMethodNotProperty = methodName != null;
			if (isMethodNotProperty)
			{
				targetMethod = FindMethod(instanceClass, GetArgumentSignature(), out _, staticReceiver: isStatic);
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

			// A static call only resolves static methods; it does not fall back to instance
			// fields/properties nor to the polymorphic resolution (concrete subclasses) below.
			if (isStatic)
			{
				throw new LanguageException($"Static method '{methodName}' with a compatible signature was not found on class '{instanceClass.Name}'. Please verify the method name, that it is declared 'static', and the argument types.");
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
			// the runtime object is always a concrete subclass. If the member does
			// not live on the declared type, search the assignable concrete
			// subclasses (same assembly as the declared type). Covers both the
			// "method Add(...) declared on an abstract base, returning the concrete
			// subclass that owns the specific method" pattern AND the symmetric
			// "property/field declared only on the concrete subclass, accessed via
			// a variable typed as the abstract base" pattern used by domain catalogs.
			if (CanHaveConcreteSubclasses(instanceClass))
			{
				Type[] argTypes = isMethodNotProperty ? GetArgumentSignature() : null;
				foreach (Type derived in EnumerateAssignableConcreteSubclasses(instanceClass))
				{
					if (isMethodNotProperty)
					{
						MethodInfo hit = FindMethod(derived, argTypes, out _);
						if (hit != null)
						{
							return hit.ReturnType.Equals(typeof(void)) ? typeof(void) : hit.ReturnType;
						}
					}
					else
					{
						FieldInfo derivedField = FindField(derived);
						if (derivedField != null) return derivedField.FieldType;

						PropertyInfo derivedProperty = FindProperty(derived);
						if (derivedProperty != null) return derivedProperty.PropertyType;
					}
				}
			}

			throw new LanguageException($"Unknown property or method '{methodName ?? propertyName}' on type '{instanceClass?.Name}'.");
		}

		// Support for polymorphic resolution in the DSL.
		// A variable whose declared type can be subclassed may reference a more
		// derived instance at runtime; the static validator must be able to find
		// members that only live on that concrete subclass. This holds not only for
		// abstract types and interfaces but also for a non-sealed concrete base:
		// classic polymorphism lets a base-typed variable point at a subclass that
		// owns the member. Only a sealed type (and object, whose subclass set is the
		// whole runtime) is excluded. We restrict the search to the assembly where
		// the declared type lives: this covers the domain-catalog pattern (same
		// assembly) without triggering a global scan.
		internal static bool CanHaveConcreteSubclasses(Type instanceClass)
		{
			return instanceClass != null
				&& !instanceClass.IsSealed
				&& instanceClass != typeof(object);
		}

		internal static IEnumerable<Type> EnumerateAssignableConcreteSubclasses(Type instanceClass)
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

		// Receiver static type used by HasOverrideReturnTypeAssignableTo to enumerate
		// covariant overrides during static validation. Returns null for shapes whose
		// receiver cannot be statically introspected.
		protected internal virtual Type ComputeInstanceType()
		{
			return null;
		}

		// When the receiver of a 'Clase.Metodo(args)' access is the name of a registered class
		// (and NOT a bound variable/parameter/global), this returns the Type of the class and
		// the call is resolved as a static method. Returns null for the normal instance-receiver
		// case. It is provided by DottedId applying the symbol-first / class-fallback rule; the
		// rest of DotAccess consults it to choose BindingFlags.Static and to invoke without an
		// instance.
		protected internal virtual Type ResolveStaticReceiverType()
		{
			return null;
		}

		// Permissive helper consumed by NewInstanceStatement.ValidateStatically when
		// `lValue.ForcedType.IsAssignableFrom(rValue.ComputeType())` fails. Mirrors
		// the covariant-return case: this DotAccess invokes a method declared on an
		// abstract or non-sealed receiver, and a concrete subclass overrides it with
		// a more refined return type that satisfies the lValue's ForcedType. Without
		// this check, the validator rejects legitimate assignments such as
		//   result = factory.Create(3, ctx)
		// where `result.ForcedType = Derived` (fixed by an earlier
		// `producer.GetOrCreate(...) : Derived`), `factory`'s
		// declared type is `FactoryBase` (abstract), and `FactoryBase.Create`
		// declares `Base` as the return type. Runtime dispatch picks the
		// `DerivedFactory.Create : Derived` covariant override, so the
		// assignment is sound dynamically.
		// Documented in BUG_StaticValidationBaseTypeReassign §6.1.
		//
		// The helper also covers the sibling case BUG_StaticValidationCovariantReturnReassign:
		// a CONCRETE receiver (no subclasses with an override) whose method declares a return
		// type that is the BASE class of ForcedType. The method body may legitimately return a
		// value of the subclass — the classic case is an identity-return / accumulator
		// pass-through where the body returns the same argument it received (which the caller
		// built as the subclass). For that case it is enough to verify that the declared return
		// type is a base of ForcedType: at runtime the produced value is still compatible.
		internal bool HasOverrideReturnTypeAssignableTo(Type forcedType)
		{
			if (forcedType == null) return false;
			if (methodName == null) return false;
			Type receiverType = ComputeInstanceType();
			if (receiverType == null) return false;
			Type[] signatures = GetArgumentSignature();

			// Covariant-return case: an abstract/interface receiver and some concrete subclass
			// declares an override with a more refined return type.
			if (CanHaveConcreteSubclasses(receiverType))
			{
				foreach (Type derived in EnumerateAssignableConcreteSubclasses(receiverType))
				{
					MethodInfo hit = FindMethod(derived, signatures, out _);
					if (hit == null) continue;
					if (hit.ReturnType == typeof(void)) continue;
					if (forcedType.IsAssignableFrom(hit.ReturnType)) return true;
				}
			}

			// Identity-return / declared-return-is-base case: the method resolved on the receiver
			// declares a return type of which ForcedType is a subclass. The runtime may
			// legitimately produce a value of ForcedType (for example when the body returns an
			// argument that the caller passed as the subclass). The strict IsAssignableFrom check
			// already failed above, so here we know that returnType and ForcedType are not equal —
			// the check only passes when returnType is a strict base of ForcedType, not when they
			// are unrelated siblings.
			MethodInfo declared = FindMethod(receiverType, signatures, out _);
			if (declared != null && declared.ReturnType != typeof(void))
			{
				if (declared.ReturnType.IsAssignableFrom(forcedType)) return true;
			}

			return false;
		}

		protected MemberInfo GetResolvedMemberInfo(Type instanceClass)
		{
			bool isStatic = ResolveStaticReceiverType() != null;

			MethodInfo targetMethod = null;

			bool isMethodNotProperty = methodName != null;
			if (isMethodNotProperty)
			{
				targetMethod = FindMethod(instanceClass, GetArgumentSignature(), out _, staticReceiver: isStatic);

				if (targetMethod != null) return targetMethod;
			}

			// For a static call only the static method counts; no instance field/property nor
			// concrete subclasses are searched.
			if (isStatic)
			{
				throw new LanguageException($"Static method '{methodName}' with a compatible signature was not found on class '{instanceClass.Name}'. Please verify the method name, that it is declared 'static', and the argument types.");
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
			// but the method, property or field only lives on the concrete subclass.
			if (CanHaveConcreteSubclasses(instanceClass))
			{
				Type[] argTypes = isMethodNotProperty ? GetArgumentSignature() : null;
				foreach (Type derived in EnumerateAssignableConcreteSubclasses(instanceClass))
				{
					if (isMethodNotProperty)
					{
						MethodInfo hit = FindMethod(derived, argTypes, out _);
						if (hit != null) return hit;
					}
					else
					{
						FieldInfo derivedField = FindField(derived);
						if (derivedField != null) return derivedField;

						PropertyInfo derivedProperty = FindProperty(derived);
						if (derivedProperty != null) return derivedProperty;
					}
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
			Type staticReceiverType = ResolveStaticReceiverType();
			if (staticReceiverType != null)
			{
				return InvokeStaticMethod(staticReceiverType);
			}

			object instance = GetTarget();

			bool isExtensionMethod;
			MethodInfo targetMethod = FindMethodCached(instance.GetType(), GetArgumentSignature(), out isExtensionMethod);
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

		// Interpreted call to a static method 'Clase.Metodo(args)'. Reuses the argument
		// binding/coercion (BindValuesForMethod, including numeric + enum + params) with a null
		// instance, and resolves the overload with BindingFlags.Static via FindMethod(staticReceiver).
		private object InvokeStaticMethod(Type receiverType)
		{
			MethodInfo targetMethod = FindMethod(receiverType, GetArgumentSignature(), out _, staticReceiver: true);
			if (targetMethod == null)
			{
				throw new LanguageException($"Static method '{methodName}' with a compatible signature was not found on class '{receiverType.Name}'. Please verify the method name, that it is declared 'static', and the argument types.");
			}

			object[] parameterValues = BindValuesForMethod(targetMethod, null, false);
			return targetMethod.Invoke(null, parameterValues);
		}

		// Replaces the per-argument loop inside InvokeMethodExpression with
		// BindValueExpressionsToParameters.
		private Expression InvokeMethodExpression(ParameterExpression parametersParam)
		{
			Type staticReceiverType = ResolveStaticReceiverType();
			if (staticReceiverType != null)
			{
				return InvokeStaticMethodExpression(staticReceiverType, parametersParam);
			}

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

			if (!isExtensionMethod && UsesParamsExpansion(methodParameters, out Type paramsElementType, out int paramsFixedCount))
			{
				return BuildParamsExpansionCall(instanceExpr, targetMethod, methodParameters, paramsElementType, paramsFixedCount, parametersParam);
			}

			if (isExtensionMethod)
			{
				argumentExprs.Add(instanceExpr);
			}
			for (int i = 0; i < arguments.Length; i++)
			{
				int paramIndex = isExtensionMethod ? i : i;
				var paramType = methodParameters[paramIndex].ParameterType;

				if (paramType.IsEnum && ClassifyEnumArg(arguments[i]) != EnumArgKind.NotEnumBindable)
				{
					argumentExprs.Add(ParseEnumArgExpression(paramType, arguments[i], parametersParam));
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

		// Compiled call to a static method 'Clase.Metodo(args)'. Same binding/coercion as the
		// instance path (enum + numeric + params via BuildParamsExpansionCall) but emitting
		// Expression.Call with a null instance. It does not apply polymorphic resolution nor
		// extension methods (they make no sense on a class-typed receiver).
		private Expression InvokeStaticMethodExpression(Type receiverType, ParameterExpression parametersParam)
		{
			Type[] signatures = GetArgumentSignature();

			MethodInfo targetMethod = FindMethod(receiverType, signatures, out _, staticReceiver: true);
			if (targetMethod == null)
			{
				throw new LanguageException($"Static method '{methodName}' with a compatible signature was not found on class '{receiverType.Name}'. Please verify the method name, that it is declared 'static', and the argument types.");
			}

			var methodParameters = targetMethod.GetParameters();

			if (UsesParamsExpansion(methodParameters, out Type paramsElementType, out int paramsFixedCount))
			{
				return BuildParamsExpansionCall(null, targetMethod, methodParameters, paramsElementType, paramsFixedCount, parametersParam);
			}

			var argumentExprs = new List<Expression>();
			for (int i = 0; i < arguments.Length; i++)
			{
				var paramType = methodParameters[i].ParameterType;

				if (paramType.IsEnum && ClassifyEnumArg(arguments[i]) != EnumArgKind.NotEnumBindable)
				{
					argumentExprs.Add(ParseEnumArgExpression(paramType, arguments[i], parametersParam));
				}
				else
				{
					argumentExprs.Add(arguments[i].ExecuteExpression(parametersParam));
				}
			}

			var convertedArguments = AstExpression.BindValueExpressionsToParameters(methodParameters, argumentExprs.ToArray());

			var finalArguments = new List<Expression>();
			foreach (var arg in convertedArguments)
			{
				if (arg is Expression expr)
					finalArguments.Add(expr);
				else
					finalArguments.Add(Expression.Constant(arg));
			}

			return Expression.Call(null, targetMethod, finalArguments);
		}

		// Compiled binding of an expanded params call: binds the fixed parameters through the
		// normal scalar path (BindValueExpressionsToParameters) and emits Expression.NewArrayInit
		// of the element-type, where each trailing element goes through the SAME scalar conversion
		// (numeric via Expression.Convert + enum parse via ParseEnumArgExpression).
		private Expression BuildParamsExpansionCall(Expression instanceExpr, MethodInfo targetMethod, ParameterInfo[] methodParameters, Type elementType, int fixedCount, ParameterExpression parametersParam)
		{
			var finalArguments = new List<Expression>();

			Expression[] fixedExprs = new Expression[fixedCount];
			ParameterInfo[] fixedParameters = new ParameterInfo[fixedCount];
			for (int i = 0; i < fixedCount; i++)
			{
				var paramType = methodParameters[i].ParameterType;
				fixedParameters[i] = methodParameters[i];
				if (paramType.IsEnum && ClassifyEnumArg(arguments[i]) != EnumArgKind.NotEnumBindable)
				{
					fixedExprs[i] = ParseEnumArgExpression(paramType, arguments[i], parametersParam);
				}
				else
				{
					fixedExprs[i] = arguments[i].ExecuteExpression(parametersParam);
				}
			}

			Expression[] boundFixed = AstExpression.BindValueExpressionsToParameters(fixedParameters, fixedExprs);
			for (int i = 0; i < boundFixed.Length; i++)
			{
				finalArguments.Add(boundFixed[i]);
			}

			var elementExprs = new List<Expression>();
			for (int j = fixedCount; j < arguments.Length; j++)
			{
				AstExpression argNode = arguments[j];
				Expression elementExpr;
				if (elementType.IsEnum && ClassifyEnumArg(argNode) != EnumArgKind.NotEnumBindable)
				{
					elementExpr = ParseEnumArgExpression(elementType, argNode, parametersParam);
				}
				else
				{
					elementExpr = CoerceScalarExpression(argNode.ExecuteExpression(parametersParam), elementType);
				}
				elementExprs.Add(elementExpr);
			}

			finalArguments.Add(Expression.NewArrayInit(elementType, elementExprs));

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
			return FindMethod(objectClass, parameterTypes, out isExtensionMethod, staticReceiver: false);
		}

		// Method resolution reused by instance and by static calls. The only change between the
		// two modes is the BindingFlags of the method sweep (Instance vs Static); all the
		// argument binding/coercion (numeric + enum + params) is identical. The extension-methods
		// branch over IEnumerable<T> applies only to instance receivers (a class used as a static
		// receiver is not IEnumerable of itself), so in static mode it is simply never entered.
		protected internal MethodInfo FindMethod(Type objectClass, Type[] parameterTypes, out bool isExtensionMethod, bool staticReceiver)
		{
			isExtensionMethod = false;
			MethodInfo foundMethod = null;

			BindingFlags memberFlags = staticReceiver
				? BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static
				: BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

			// 1. Look for a regular instance method.
			foreach (MethodInfo method in objectClass.GetMethods(memberFlags))
			{
				if (string.Equals(method.Name, this.methodName, StringComparison.OrdinalIgnoreCase))
				{
					var parameters = method.GetParameters();
					if (parameters.Length == parameterTypes.Length)
					{
						bool match = true;
						bool usesEnumBinding = false;
						for (int i = 0; i < parameters.Length; i++)
						{
							var paramType = parameters[i].ParameterType;
							var argType = parameterTypes[i];

							if (paramType.IsEnum && ClassifyEnumArg(arguments[i]) != EnumArgKind.NotEnumBindable)
							{
								if (!IsEnumArgCompatible(paramType, arguments[i]))
								{
									match = false;
									break;
								}
								usesEnumBinding = true;
							}
							else if (!AreCompatible(argType, paramType))
							{
								match = false;
								break;
							}
						}
						if (match)
						{
							// Prefer-enum: an overload that binds an argument as enum wins immediately;
							// those that do not use enum-binding are remembered as a fallback. This
							// way 'Febrero'/@mes deterministically chooses foo(Enum) over foo(string).
							if (usesEnumBinding)
							{
								foundMethod = method;
								return foundMethod;
							}
							if (foundMethod == null)
							{
								foundMethod = method;
							}
						}
					}
				}
			}

			// If the strict pass found a match (enum or string fallback), return it before the
			// permissive pass — this preserves the original "first match wins" semantics, but with
			// enum priority already applied above.
			if (foundMethod != null) return foundMethod;

			// 1a-bis. Params pass: only evaluated when NO exact-arity overload matched above. This
			// way an exact-arity non-params signature ALWAYS wins over a params variant. The pass
			// is conservative (it uses AreCompatible/enum over already-concrete types), so
			// indeterminate or base-typed arguments fall through to the permissive pass below
			// without params stealing them.
			MethodInfo paramsMethod = FindParamsMethod(objectClass, parameterTypes, memberFlags);
			if (paramsMethod != null) return paramsMethod;

			// 1b. Permissive overload resolution for static validation when some argument is
			// AMBIGUOUS with respect to the runtime — either because of an INDETERMINATE type
			// (null or typeof(object)), or because of a declared type that is STRICTLY a BASE of
			// the parameter type.
			//
			// Documented use cases:
			//
			//   (a) Argument with type null/object — journal rehydration where a global was
			//       synthesized by a nested Eval(...) in a previous entry (`Eval('y = x;')`
			//       inside the outer `Eval(producer.CreateVariables())`), and the resolver task
			//       does not yet have its type in the SymbolTable when it validates a later entry
			//       that consumes it. Documented in
			//       BUG_RehydrationStaticValidation_EvalSynthesizedArg §4.3.
			//
			//   (b) Argument whose declared type is strictly a base of the parameter —
			//       rehydration where a factory method with an abstract return type (e.g.
			//       `NewItem(Owner, int, SomeEnum) -> Base`) creates concrete subclass instances
			//       (`Derived`) that are later passed to a method declaring the subclass as a
			//       parameter (`Consume(..., Derived order, ...)`). The LValue symbol ends up with
			//       ForcedType = typeof(Base) (the base) due to the propagation in
			//       NewInstanceStatement.ValidateStatically; the next statement fails the strict
			//       pass because Derived.IsAssignableFrom(Base) is false (the base hierarchy is NOT
			//       assignable from a subclass). The runtime dispatch DOES match because FindMethod
			//       runs with the concrete value.GetType() = Derived. Documented in
			//       BUG_StaticValidationSubclassReassign §3.3-§3.4.
			//
			// In both cases the strict pass exhausts all overloads of a method that DOES exist and
			// ComputeCallExpressionType throws
			//   "Unknown property or method 'X' on type 'Y'"
			// — a false positive: the method exists, we just cannot pin the overload with the
			// available static information. The leniency is scoped to static validation; the
			// dispatch in the execution task is safe.
			bool anyArgRequiresRelaxation = false;
			for (int j = 0; j < parameterTypes.Length; j++)
			{
				if (parameterTypes[j] == null || parameterTypes[j] == typeof(object))
				{
					anyArgRequiresRelaxation = true;
					break;
				}
			}
			// Also activate the permissive pass when some argument has a determined type that is
			// STRICTLY a BASE of the parameter type in at least one candidate overload of the same
			// name and arity. This avoids indiscriminate activation — we only trigger the leniency
			// if there is reasonable evidence that a value.GetType()-style downcast can satisfy the
			// call at runtime.
			if (!anyArgRequiresRelaxation)
			{
				foreach (MethodInfo candidate in objectClass.GetMethods(memberFlags))
				{
					if (!string.Equals(candidate.Name, this.methodName, StringComparison.OrdinalIgnoreCase)) continue;
					ParameterInfo[] candidateParameters = candidate.GetParameters();
					if (candidateParameters.Length != parameterTypes.Length) continue;
					for (int j = 0; j < candidateParameters.Length; j++)
					{
						Type argT = parameterTypes[j];
						Type pT = candidateParameters[j].ParameterType;
						if (argT != null && argT != typeof(object) && argT != pT && argT.IsAssignableFrom(pT))
						{
							anyArgRequiresRelaxation = true;
							break;
						}
					}
					if (anyArgRequiresRelaxation) break;
				}
			}
			if (anyArgRequiresRelaxation)
			{
				foreach (MethodInfo method in objectClass.GetMethods(memberFlags))
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

								bool argIsIndeterminate = argType == null || argType == typeof(object);
								if (argIsIndeterminate) continue;

								// Relax when the argument has a declared type that is
								// STRICTLY a BASE of the parameter type: the runtime
								// dispatch sees the concrete value.GetType() and can
								// satisfy the call with the subclass declared by the
								// parameter.
								if (argType != paramType && argType.IsAssignableFrom(paramType))
								{
									continue;
								}

								if (paramType.IsEnum && ClassifyEnumArg(arguments[i]) != EnumArgKind.NotEnumBindable)
								{
									if (!IsEnumArgCompatible(paramType, arguments[i]))
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

		// Resolves a params overload (C# params T[]). Matches if there are at least N-1 arguments
		// (N = number of parameters) and the trailing ones are compatible, either as individual
		// elements of the element-type (expanded form: Foo(1,2,3)) or as the already-built array
		// passed directly (Foo(anArray)). Conservative: only already-concrete types.
		// Deterministic: among several candidates it picks the one with the most fixed parameters,
		// then exact element match, then lexicographic order of the signature — it never depends
		// on the CLR ordering.
		private MethodInfo FindParamsMethod(Type objectClass, Type[] parameterTypes, BindingFlags memberFlags)
		{
			MethodInfo best = null;
			int bestFixedCount = -1;
			bool bestExact = false;
			string bestSignature = null;

			foreach (MethodInfo method in objectClass.GetMethods(memberFlags))
			{
				if (!string.Equals(method.Name, this.methodName, StringComparison.OrdinalIgnoreCase)) continue;

				ParameterInfo[] parameters = method.GetParameters();
				if (parameters.Length == 0) continue;

				ParameterInfo last = parameters[parameters.Length - 1];
				if (!AstExpression.IsParamsParameter(last)) continue;

				int fixedCount = parameters.Length - 1;
				if (parameterTypes.Length < fixedCount) continue;

				bool match = true;
				for (int i = 0; i < fixedCount; i++)
				{
					if (!ScalarArgMatchesParam(parameterTypes[i], arguments[i], parameters[i].ParameterType))
					{
						match = false;
						break;
					}
				}
				if (!match) continue;

				Type elementType = last.ParameterType.GetElementType();
				if (!TrailingArgsMatchParams(parameterTypes, fixedCount, last.ParameterType, elementType, out bool exactElements)) continue;

				string signature = BuildParameterSignature(parameters);
				bool better =
					best == null
					|| fixedCount > bestFixedCount
					|| (fixedCount == bestFixedCount && exactElements && !bestExact)
					|| (fixedCount == bestFixedCount && exactElements == bestExact && string.CompareOrdinal(signature, bestSignature) < 0);

				if (better)
				{
					best = method;
					bestFixedCount = fixedCount;
					bestExact = exactElements;
					bestSignature = signature;
				}
			}

			return best;
		}

		private static string BuildParameterSignature(ParameterInfo[] parameters)
		{
			System.Text.StringBuilder sb = new System.Text.StringBuilder();
			for (int i = 0; i < parameters.Length; i++)
			{
				if (i > 0) sb.Append(',');
				sb.Append(parameters[i].ParameterType.FullName ?? parameters[i].ParameterType.Name);
			}
			return sb.ToString();
		}

		// Compatibility of ONE argument against ONE scalar parameter type, with the same rule as
		// the strict pass: if the parameter is enum and the argument is enum-bindable it is
		// validated by name; otherwise by AreCompatible. argType null/object => not compatible
		// (deferred).
		private bool ScalarArgMatchesParam(Type argType, AstExpression argNode, Type paramType)
		{
			if (paramType.IsEnum && ClassifyEnumArg(argNode) != EnumArgKind.NotEnumBindable)
			{
				return IsEnumArgCompatible(paramType, argNode);
			}
			if (argType == null || argType == typeof(object)) return false;
			return AreCompatible(argType, paramType);
		}

		// Decides whether the trailing arguments satisfy a params parameter. Two forms:
		//   direct  : exactly 1 trailing arg that is already the array (compatible with arrayType).
		//   expanded: each trailing arg is compatible with the element-type (0 or more).
		private bool TrailingArgsMatchParams(Type[] parameterTypes, int fixedCount, Type arrayType, Type elementType, out bool exactElements)
		{
			exactElements = false;

			int trailing = parameterTypes.Length - fixedCount;

			// Direct form: pass the already-built array to the params slot.
			if (trailing == 1)
			{
				Type argType = parameterTypes[fixedCount];
				if (argType != null && argType != typeof(object) && AreCompatible(argType, arrayType))
				{
					exactElements = true;
					return true;
				}
			}

			// Expanded form: each trailing arg binds as an element of the array.
			bool allExact = true;
			for (int i = fixedCount; i < parameterTypes.Length; i++)
			{
				if (!ScalarArgMatchesParam(parameterTypes[i], arguments[i], elementType)) return false;
				if (parameterTypes[i] != elementType) allExact = false;
			}
			exactElements = allExact;
			return true;
		}

		// Given a method already resolved with a last params parameter, decides whether this call
		// must EXPAND the trailing arguments into a new array, or whether the array was passed
		// directly. The same rule is used in binding (interpreted and compiled) as in
		// FindParamsMethod.
		private bool UsesParamsExpansion(ParameterInfo[] parameters, out Type elementType, out int fixedCount)
		{
			elementType = null;
			fixedCount = 0;

			if (parameters.Length == 0) return false;

			ParameterInfo last = parameters[parameters.Length - 1];
			if (!AstExpression.IsParamsParameter(last)) return false;

			fixedCount = parameters.Length - 1;
			elementType = last.ParameterType.GetElementType();

			int argCount = this.arguments == null ? 0 : this.arguments.Length;

			// A single trailing arg that is already the array => direct form (no expansion).
			if (argCount == parameters.Length)
			{
				Type lastArgType = this.arguments[argCount - 1].ComputeType();
				if (lastArgType != null && lastArgType != typeof(object) && AreCompatible(lastArgType, last.ParameterType))
				{
					return false;
				}
			}

			return true;
		}


		private object[] BindValuesForMethod(MethodInfo method, object instance, bool isExtensionMethod)
		{
			ParameterInfo[] methodParameters = method.GetParameters();

			if (!isExtensionMethod && UsesParamsExpansion(methodParameters, out Type paramsElementType, out int paramsFixedCount))
			{
				return BindParamsExpansionInterpreted(methodParameters, paramsElementType, paramsFixedCount);
			}

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
				// Enum slot + enum-bindable argument (bare symbol or string with value): resolved
				// here to the enum value. This way BindValuesToParameters receives it already typed.
				if (parameterInfo.ParameterType.IsEnum && ClassifyEnumArg(this.arguments[i]) != EnumArgKind.NotEnumBindable)
				{
					evaluatedArguments[startIndex++] = ParseEnumArgValue(parameterInfo.ParameterType, this.arguments[i]);
				}
				else
				{
					evaluatedArguments[startIndex++] = this.arguments[i].Execute();
				}
			}
			var result = BindValuesToParameters(method.GetParameters(), evaluatedArguments);

			return result;
		}

		// Interpreted binding of an expanded params call: binds the fixed parameters through the
		// normal scalar path (reusing BindValuesToParameters) and builds the element-type array,
		// coercing EACH trailing element through the SAME scalar path (numeric + enum).
		private object[] BindParamsExpansionInterpreted(ParameterInfo[] parameters, Type elementType, int fixedCount)
		{
			object[] result = new object[parameters.Length];

			object[] fixedEvaluated = new object[fixedCount];
			ParameterInfo[] fixedParameters = new ParameterInfo[fixedCount];
			for (int i = 0; i < fixedCount; i++)
			{
				ParameterInfo parameterInfo = parameters[i];
				fixedParameters[i] = parameterInfo;
				if (parameterInfo.ParameterType.IsEnum && ClassifyEnumArg(this.arguments[i]) != EnumArgKind.NotEnumBindable)
				{
					fixedEvaluated[i] = ParseEnumArgValue(parameterInfo.ParameterType, this.arguments[i]);
				}
				else
				{
					fixedEvaluated[i] = this.arguments[i].Execute();
				}
			}

			object[] boundFixed = BindValuesToParameters(fixedParameters, fixedEvaluated);
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
				if (elementType.IsEnum && ClassifyEnumArg(argNode) != EnumArgKind.NotEnumBindable)
				{
					element = ParseEnumArgValue(elementType, argNode);
				}
				else
				{
					element = CoerceScalarValue(argNode.Execute(), elementType);
				}
				array.SetValue(element, j);
			}
			result[fixedCount] = array;

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

				bool argIsEnumSymbol = paramType.IsEnum && ClassifyEnumArg(arguments[i]) == EnumArgKind.Symbol;

				if (argIsEnumSymbol)
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
				else if (paramType.IsEnum && ClassifyEnumArg(arguments[i]) == EnumArgKind.StringValue)
				{
					// String with value in an enum slot: literal validated statically (allows
					// fallback to a string overload if it is not a member); parameter/variable
					// deferred to runtime.
					compatible = IsEnumArgCompatible(paramType, arguments[i]);
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
					else
					{
						// Any other expression (e.g. a nested call as an argument, foo(goo(...)),
						// or a chained access): the value is not statically evaluable. We preserve
						// the argument COUNT with a typed placeholder — previously it was discarded
						// and broke the count-based match. The matcher matches the placeholder by
						// type (wildcard/typed) or, if the pattern uses a NestedCallParameterNode,
						// ignores it and matches the inner call against the registered
						// ScriptMethodCalls.
						Type argType = arg.ComputeType();
						values.Add(new Follower.TypedValuePlaceholder(argType));
					}
				}
			}
			return values;
		}

	}
}
