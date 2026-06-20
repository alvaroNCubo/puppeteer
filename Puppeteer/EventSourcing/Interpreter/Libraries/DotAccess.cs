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

		// --- Caché de resolución de método compartido a nivel de proceso -------------------
		// FindMethod escanea Type.GetMethods(...) (1 a 3 pasadas) + matching de overloads por
		// CADA invocacion. En rehidratacion eso es el grueso del costo de exec (~325us/evento,
		// la etapa cuello de botella). Como cada entry del journal se parsea a un AST fresco y
		// se ejecuta una sola vez, cachear el MethodInfo EN EL NODO no sirve: hay que cachear
		// compartido y con clave estable entre entries.
		//
		// La resolucion de FindMethod depende EXCLUSIVAMENTE de:
		//   (objectClass runtime, methodName, tipos de argumento, y para args que son Id: su
		//    nombre — el branch enum-por-Id matchea por Enum.GetNames(idName), no por tipo).
		// La clave captura esos cuatro componentes, asi que el caché es semanticamente exacto
		// (no cambia que overload se elige). NO se cachea cuando algun argType es null/object:
		// son el territorio ambiguo de la static validation (relajacion de overloads), poco
		// frecuente en exec y mas seguro resolver fresco.
		private readonly struct MethodResolutionKey : IEquatable<MethodResolutionKey>
		{
			private readonly Type objectClass;
			private readonly string methodName;
			private readonly Type[] argTypes;
			private readonly string[] idArgNames; // null si ningun argumento es Id; idArgNames[i]!=null marca un Id

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

		// Compartido a nivel de proceso: la resolucion (tipo, nombre, firma) es global, no
		// depende del actor. Acotado por la cantidad de combos distintos (tipo, metodo, firma),
		// que es pequeña frente a los millones de eventos del journal.
		private static readonly ConcurrentDictionary<MethodResolutionKey, ResolvedMethod> methodResolutionCache
			= new ConcurrentDictionary<MethodResolutionKey, ResolvedMethod>();

		// Memo: ¿el metodo (por nombre) sobre este tipo tiene ALGUNA sobrecarga con parametro
		// enum? Solo entonces un argumento string puede competir entre foo(Enum) y foo(string),
		// y la llave del cache (que para args no-Id no distingue 'Febrero' literal de
		// (string)'Febrero') no alcanza para separar las resoluciones. Para tipos/metodos sin
		// sobrecarga enum (el caso comun, hot path) retorna false al instante y no afecta nada.
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

			// Carve-out enum: un argumento string NO-Id (literal 'Febrero' o (string)'Febrero')
			// en un metodo con sobrecarga enum no es cacheable — la llave no separa el literal
			// (que prefiere enum) del cast a string. Los Id string si quedan distinguidos por
			// idArgNames en la llave, asi que se mantienen cacheables.
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

			// Llamada a metodo static: el receptor es una clase, no un valor. No se debe generar
			// GetTargetExpression (que intentaria asignar storage al Id de la clase, cuyo scope es
			// undefined en modo compilado). Se resuelve directo por el camino static.
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
			// Guard del receptor sin tipo durante static validation. Aparece cuando el
			// instance.ComputeType() de la cadena retorna null — un caso conocido es la
			// rehidratacion del journal cuando el receiver es un global declarado en
			// una entry previa cuyo SymbolTable todavia no tiene el tipo resuelto
			// (vease BUG_RehydrationStaticValidation_LiquidityUpgrader_LiquidityAPI §4.bis
			// y §10.4). Sin este guard FindMethod/FindField/FindProperty caen en
			// objectClass.GetMethods(...) con null y revientan con NullReferenceException
			// sin contexto utilizable en el log del resolverTask. Convertir en
			// LanguageException con el nombre del member preserva el flujo permisivo
			// del pipeline de rehidratacion (loguea + sigue con la siguiente entry)
			// con un mensaje accionable.
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

			// Una llamada static solo resuelve metodos static; no cae a campos/propiedades de
			// instancia ni a la resolucion polimorfica (subclases concretas) de abajo.
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
		// A variable whose declared type is abstract or an interface ALWAYS
		// references a concrete instance at runtime; the static validator must be
		// able to find members that only live on that concrete subclass. We
		// restrict the search to the assembly where the abstract type is declared:
		// this covers the domain-catalog pattern (same assembly) without
		// triggering a global scan.
		internal static bool CanHaveConcreteSubclasses(Type instanceClass)
		{
			return instanceClass != null && (instanceClass.IsAbstract || instanceClass.IsInterface);
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

		// Cuando el receptor de un acceso 'Clase.Metodo(args)' es el nombre de una clase
		// registrada (y NO una variable/parametro/global ligado), esto retorna el Type de
		// la clase y la llamada se resuelve como metodo static. Retorna null para el caso
		// normal de receptor-instancia. Lo provee DottedId aplicando la regla
		// simbolo-primero / clase-fallback; el resto de DotAccess lo consulta para elegir
		// BindingFlags.Static y para invocar sin instancia.
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
		// Documentado en BUG_StaticValidationBaseTypeReassign §6.1.
		//
		// El helper tambien cubre el caso hermano BUG_StaticValidationCovariantReturnReassign:
		// receptor CONCRETO (sin subclases con override) cuyo metodo declara un
		// tipo de retorno que es la clase BASE de ForcedType. El body del metodo
		// puede legitimamente retornar un valor de la subclase — el caso clasico
		// es un identity-return / accumulator pass-through donde el body devuelve
		// el mismo argumento que recibio (que el caller construyo como la
		// subclase). Para ese caso, basta con verificar que el declared return
		// type sea base de ForcedType: a runtime, el valor producido sigue siendo
		// compatible.
		internal bool HasOverrideReturnTypeAssignableTo(Type forcedType)
		{
			if (forcedType == null) return false;
			if (methodName == null) return false;
			Type receiverType = ComputeInstanceType();
			if (receiverType == null) return false;
			Type[] signatures = GetArgumentSignature();

			// Caso covariant-return: receptor abstracto/interfaz y alguna subclase
			// concreta declara un override con tipo de retorno mas refinado.
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

			// Caso identity-return / declared-return-is-base: el metodo resuelto
			// sobre el receptor declara un tipo de retorno del que ForcedType es
			// una subclase. El runtime puede legitimamente producir un valor de
			// ForcedType (por ejemplo cuando el body retorna un argumento que el
			// caller paso como subclase). La verificacion estricta IsAssignableFrom
			// ya fallo arriba, asi que aqui sabemos que returnType y ForcedType
			// no son iguales — el chequeo solo pasa cuando returnType es base
			// estricta de ForcedType, no cuando son hermanos no relacionados.
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

			// Para una llamada static solo cuenta el metodo static; no se busca campo/propiedad de
			// instancia ni subclases concretas.
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

		// Llamada interpretada a un metodo static 'Clase.Metodo(args)'. Reusa el binding/coercion
		// de argumentos (BindValuesForMethod, incluyendo numerico + enum + params) con instancia
		// null, y resuelve la sobrecarga con BindingFlags.Static via FindMethod(staticReceiver).
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

		// Llamada compilada a un metodo static 'Clase.Metodo(args)'. Mismo binding/coercion que la
		// ruta instancia (enum + numerico + params via BuildParamsExpansionCall) pero emitiendo
		// Expression.Call con instancia null. No aplica resolucion polimorfica ni extension methods
		// (no tienen sentido sobre un receptor de tipo clase).
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

		// Binding compilado de una llamada params expandida: liga los parametros fijos por la ruta
		// escalar normal (BindValueExpressionsToParameters) y emite Expression.NewArrayInit del
		// element-type, donde cada elemento trailing pasa por la MISMA conversion escalar (numerica
		// via Expression.Convert + parse de enum via ParseEnumArgExpression).
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

		// Resolucion de metodo reusada por instancia y por llamada static. El unico cambio entre
		// ambos modos son los BindingFlags del barrido de metodos (Instance vs Static); todo el
		// binding/coercion de argumentos (numerico + enum + params) es identico. La rama de
		// extension methods sobre IEnumerable<T> aplica solo a receptores-instancia (una clase
		// usada como receptor static no es IEnumerable de si misma), de modo que en modo static
		// simplemente no se entra.
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
							// Prefer-enum: una sobrecarga que liga un argumento como enum gana de
							// inmediato; las que no usan enum-binding se recuerdan como fallback. Asi
							// 'Febrero'/@mes elige foo(Enum) sobre foo(string) deterministicamente.
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

			// Si el strict pass hallo un match (enum o fallback string), retornarlo antes de
			// la pasada permisiva — preserva la semantica original de "primer match gana", pero
			// con prioridad enum ya aplicada arriba.
			if (foundMethod != null) return foundMethod;

			// 1a-bis. Pasada params: solo se evalua cuando NINGUNA sobrecarga de aridad exacta
			// matcheo arriba. Asi una firma no-params de aridad exacta SIEMPRE gana sobre una
			// variante params. La pasada es conservadora (usa AreCompatible/enum sobre tipos ya
			// concretos), de modo que argumentos indeterminados o de tipo base caen a la pasada
			// permisiva de abajo sin que params se los robe.
			MethodInfo paramsMethod = FindParamsMethod(objectClass, parameterTypes, memberFlags);
			if (paramsMethod != null) return paramsMethod;

			// 1b. Permissive overload resolution para static validation cuando
			// algun argumento es AMBIGUO respecto al runtime — bien sea por
			// tipo INDETERMINADO (null o typeof(object)), bien sea por tipo
			// declarado que es ESTRICTAMENTE BASE del tipo del parametro.
			//
			// Casos de uso documentados:
			//
			//   (a) Argumento con tipo null/object — rehidratacion del journal
			//       donde un global fue sintetizado por un Eval(...) anidado
			//       en una entry previa (`Eval('y = x;')` dentro
			//       del outer `Eval(producer.CreateVariables())`),
			//       el resolverTask aun no tiene su tipo en el SymbolTable
			//       cuando valida una entry posterior que lo consume.
			//       Documentado en
			//       BUG_RehydrationStaticValidation_EvalSynthesizedArg §4.3.
			//
			//   (b) Argumento con tipo declarado estrictamente base del
			//       parametro — rehidratacion donde un metodo factoria con
			//       return type abstracto (p.ej.
			//       `NewItem(Owner, int, SomeEnum) -> Base`) crea
			//       instancias concretas de subclases (`Derived`) que luego
			//       se pasan a un metodo que declara la subclase como
			//       parametro (`Consume(...,
			//       Derived order, ...)`). El symbol del LValue queda con
			//       ForcedType = typeof(Base) (la base) por la propagacion
			//       en NewInstanceStatement.ValidateStatically; la siguiente
			//       statement falla la pasada estricta porque
			//       Derived.IsAssignableFrom(Order) es false (la jerarquia
			//       base NO es asignable desde una subclase). El dispatch
			//       runtime SI matchea porque FindMethod corre con
			//       value.GetType() concreto = Derived. Documentado en
			//       BUG_StaticValidationSubclassReassign §3.3-§3.4.
			//
			// En ambos casos la pasada estricta agota todos los overloads de
			// un metodo que SI existe y ComputeCallExpressionType lanza
			//   "Unknown property or method 'X' on type 'Y'"
			// — falso positivo: el metodo existe, solo no podemos cuadrar el
			// overload con la informacion estatica disponible. La leniencia
			// esta acotada a static validation; el dispatch en executionTask
			// es seguro.
			bool anyArgRequiresRelaxation = false;
			for (int j = 0; j < parameterTypes.Length; j++)
			{
				if (parameterTypes[j] == null || parameterTypes[j] == typeof(object))
				{
					anyArgRequiresRelaxation = true;
					break;
				}
			}
			// Tambien activar la pasada permisiva cuando algun argumento
			// tiene tipo determinado que es ESTRICTAMENTE BASE del tipo del
			// parametro en al menos un overload candidato del mismo nombre y
			// aridad. Esto evita la activacion indiscriminada — solo
			// disparamos la leniencia si hay evidencia razonable de que un
			// downcast estilo value.GetType() puede satisfacer la llamada
			// en runtime.
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

								// Relaja cuando el argumento tiene tipo declarado
								// ESTRICTAMENTE BASE del tipo del parametro: el
								// dispatch runtime ve value.GetType() concreto y
								// puede satisfacer la llamada con la subclase
								// declarada por el parametro.
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

		// Resuelve una sobrecarga params (C# params T[]). Matchea si hay al menos N-1 argumentos
		// (N = numero de parametros) y los trailing son compatibles, ya sea como elementos
		// individuales del element-type (forma expandida: Foo(1,2,3)) o como el arreglo ya armado
		// pasado directo (Foo(unArreglo)). Conservadora: solo tipos ya concretos. Determinista:
		// ante varias candidatas elige la de mayor numero de parametros fijos, luego match exacto
		// de elementos, luego orden lexicografico de la firma — nunca depende del orden del CLR.
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

		// Compatibilidad de UN argumento contra UN tipo de parametro escalar, con la misma regla
		// que la pasada estricta: si el parametro es enum y el argumento es enum-bindable se valida
		// por nombre; si no, por AreCompatible. argType null/object => no compatible (se difiere).
		private bool ScalarArgMatchesParam(Type argType, AstExpression argNode, Type paramType)
		{
			if (paramType.IsEnum && ClassifyEnumArg(argNode) != EnumArgKind.NotEnumBindable)
			{
				return IsEnumArgCompatible(paramType, argNode);
			}
			if (argType == null || argType == typeof(object)) return false;
			return AreCompatible(argType, paramType);
		}

		// Decide si los argumentos trailing satisfacen un parametro params. Dos formas:
		//   directa : exactamente 1 trailing arg que ya es el arreglo (compatible con arrayType).
		//   expandida: cada trailing arg es compatible con el element-type (0 o mas).
		private bool TrailingArgsMatchParams(Type[] parameterTypes, int fixedCount, Type arrayType, Type elementType, out bool exactElements)
		{
			exactElements = false;

			int trailing = parameterTypes.Length - fixedCount;

			// Forma directa: pasar el arreglo ya armado a la ranura params.
			if (trailing == 1)
			{
				Type argType = parameterTypes[fixedCount];
				if (argType != null && argType != typeof(object) && AreCompatible(argType, arrayType))
				{
					exactElements = true;
					return true;
				}
			}

			// Forma expandida: cada trailing arg liga como un elemento del arreglo.
			bool allExact = true;
			for (int i = fixedCount; i < parameterTypes.Length; i++)
			{
				if (!ScalarArgMatchesParam(parameterTypes[i], arguments[i], elementType)) return false;
				if (parameterTypes[i] != elementType) allExact = false;
			}
			exactElements = allExact;
			return true;
		}

		// Dado un metodo ya resuelto con un ultimo parametro params, decide si esta llamada debe
		// EXPANDIR los argumentos trailing en un arreglo nuevo, o si el arreglo se paso directo.
		// Se usa identica regla en binding (interpretado y compilado) que en FindParamsMethod.
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

			// Un unico trailing arg que ya es el arreglo => forma directa (sin expandir).
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
				// Slot enum + argumento enum-bindable (simbolo pelado o string con valor): se
				// resuelve aqui al valor del enum. Asi BindValuesToParameters lo recibe ya tipado.
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

		// Binding interpretado de una llamada params expandida: liga los parametros fijos por la
		// ruta escalar normal (reusando BindValuesToParameters) y arma el arreglo del element-type
		// coercionando CADA elemento trailing por la MISMA ruta escalar (numerica + enum).
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
					// String con valor en slot enum: literal validado estatico (permite fallback a
					// una sobrecarga string si no es miembro); parametro/variable diferido a runtime.
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
						// Cualquier otra expresion (p.ej. una llamada anidada como argumento,
						// foo(goo(...)), o un acceso encadenado): el valor no es evaluable
						// estaticamente. Preservamos el CONTEO de argumentos con un placeholder
						// tipado — antes se descartaba y rompia el match por conteo. El matcher
						// casa el placeholder por tipo (wildcard/typed) o, si el patron usa un
						// NestedCallParameterNode, lo ignora y casa la llamada interna contra
						// las ScriptMethodCalls registradas.
						Type argType = arg.ComputeType();
						values.Add(new Follower.TypedValuePlaceholder(argType));
					}
				}
			}
			return values;
		}

	}
}
