using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using Puppeteer.EventSourcing.Interpreter.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer
{

	public sealed class Parameter
	{
		private readonly VariableSymbol instance = null;
		// The In parameter's caller-supplied value, preserved so Clear() can restore it after
		// execution (an In param dirtied by the body is reset to its input for the caller).
		private object previousInstanceValue = null;
		private readonly string name;
		private readonly Type parameterType;
		private readonly bool isNullableParameter;
		private readonly int parameterModifier = 0;
		private string evalScript;
		private /*readonly*/ Program program;

		public static int In = 1;
		public static int Out = 2;
		public static int InOut = 3;
		public static int Eval = 4;

		internal Parameter(string name, Type type) : this(In, name, type)
		{

		}

		internal Parameter(int parameterModifier, string name, Type type)
		{
			ArgumentNullException.ThrowIfNull(name);
			if (!IsValidParameterName(name)) throw new LanguageException($"Parameter name '{name}' is not valid");
			if (parameterModifier < 1) throw new LanguageException($"Modify '{parameterModifier}' is not valid");

			this.name = name;
			// The declared type is normalized BEFORE being stored: array -> IEnumerable<elem>
			// and Nullable<T> -> T. NormalizeParameterType is the single source of this rule; the
			// re-set guard in Parameters.SetParameter uses the same helper so that both
			// paths cannot diverge (a slot created from DateTime[] is stored as
			// IEnumerable<DateTime>, and a re-set with DateTime[] must normalize the same way before
			// comparing the type).
			this.isNullableParameter = !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
			this.parameterType = NormalizeParameterType(type);
			// See IsSupportedParameterType for the design reason: a domain is reachable only as a
			// SETTING of the puppet, so no caller may need to name one of its types.
			if (!IsSupportedParameterType(this.parameterType))
				throw new LanguageException($"Parameter '{name}' is declared as '{this.parameterType.Name}', which is not a valid parameter type. A parameter carries a primitive value: {ParameterTypeCatalog}. A domain type — class, struct or enum — is never a parameter type: naming one here means it was widened to public and the calling project took a reference on the domain assembly, when a domain is meant to reach the framework only as a setting of the puppet, by reflection. Pass a primitive instead (for example the value's name as a string, or value.ToString() in a format of your choosing) and let the script's call site coerce it, so that interpretation happens inside the actor and is journaled with the act.");
			this.parameterModifier = parameterModifier;
			this.instance = SymbolTable.IsolatedStorage(name, null, this.parameterType);
		}

		internal static Type NormalizeParameterType(Type type)
		{
			ArgumentNullException.ThrowIfNull(type);
			if (type.IsArray)
			{
				var elementType = type.GetElementType();
				type = typeof(IEnumerable<>).MakeGenericType(new[] { elementType });
			}
			var underlyingType = Nullable.GetUnderlyingType(type);
			return underlyingType ?? type;
		}

		// The parameter plane admits PRIMITIVE values only: int, long, double, decimal, char,
		// string, bool, datetime, plus a ONE-LEVEL collection of those (List<T>, IEnumerable<T>,
		// T[], where T is one of those scalars — never another collection). The admitted set is
		// closed and every type in it is owned by the LANGUAGE. Nothing owned by a domain is a
		// parameter type — not a class, not a struct, not an enum.
		//
		// WHY the set is closed, which is the whole design and not a serialization detail. A
		// domain enters Puppeteer exactly one way: as a SETTING of the puppet — the libraries are
		// handed to the actor at composition and reached by REFLECTION. That indirection is the
		// point: `internal` is the domain's fence against the host language, and reflection is how
		// the framework steps over it without anyone else being able to. A caller therefore never
		// needs to NAME a domain type, and must not be able to.
		//
		// Read the contrapositive, because it is the diagnostic. If a call site can write
		// `typeof(SomeDomainType)` in a parameter declaration, then two things already happened
		// upstream: that type was widened to `public`, and the calling project took a
		// compile-time reference on the domain assembly. The fence is breached before this method
		// ever runs, and the type is now a public identifier loose on the program's interface with
		// no reason to be there — rename it or move it and the caller, the journal, and the wire
		// all break at once. Admitting such a type here is what creates the pressure to breach the
		// fence, so refusing it is what keeps the domain reachable only as a setting.
		//
		// A domain enum is refused for that reason and NOT for lack of a wire form: an enum could
		// be journaled symbolically, which is precisely why it is the tempting case. What it
		// cannot do is be named by a caller without the breach above. The supported route keeps
		// the interpretation INSIDE the actor: pass the member name as a `string` parameter and let
		// the call site coerce it to the enum (see AstExpression.ClassifyEnumArg /
		// IsEnumArgCompatible). Then deciding that a given name denotes a given member is part of
		// the journaled, replayed act, instead of a decision the caller made off the record.
		//
		// Same reasoning refuses a container or an arbitrary struct: the author converts at the
		// boundary (value.ToString(), instant.ToString(format)) so the representation is CHOSEN
		// and no DTO enters dressed as a legitimate domain type.
		//
		// The type is checked where the slot is DECLARED, not where the operation is prepared.
		// Refusal used to come from freezing the canonical `define action` signature, which only a
		// COMMAND does: the identical declaration bound and RAN in a query, quietly yielding
		// whatever the value's ToString produced. An unsupported type was therefore half-legal,
		// working until the day that query became a command. Which operation consumes a slot is
		// not part of its type contract, so the refusal belongs at the declaration, where it reads
		// the same everywhere and under either compilation policy (declaring is not evaluating).
		//
		// Expects the NORMALIZED type (see NormalizeParameterType): an array already arrives as
		// IEnumerable<element> and a Nullable<T> as T, so only the element type is recursed on.
		// A char slot takes a char. A one-character string used to be coerced into one here, which is
		// how a DTO carrying a char as text got in — but that was decided when neither side could say
		// "char" properly: the language had no char literal, so a char was written as a string
		// everywhere. Both sides can say it now ('L' in the caller's C#, 'L'c in the DSL), so the
		// coercion no longer buys expressiveness; it only lets the declared type and the supplied
		// value disagree, and a slot whose stored value does not match what it promises is the shape
		// every reader downstream then has to special-case.
		//
		// Refused at the slot, where the disagreement is, rather than converted silently. This is the
		// rule the plane already applies to a container or an arbitrary struct — the author converts
		// at the boundary so the representation is CHOSEN — applied to the case that had been carved
		// out of it.
		private void RefuseStringWhereCharIsDeclared(object value)
		{
			if (value == null) return;
			if (DeclaredElementType() != typeof(char)) return;

			if (value is string suppliedText)
			{
				throw new LanguageException($"Parameter '{name}' is declared char, so its value must be a char{DescribeSuppliedText(suppliedText)}. A string was supplied. To pass text, declare the parameter string instead; there is no conversion from string to char (in the DSL, take one position of it: text[0]).");
			}

			if (value is System.Collections.IEnumerable sequence)
			{
				foreach (object element in sequence)
				{
					if (element is string)
					{
						throw new LanguageException($"Parameter '{name}' is declared as a collection of char, so every element must be a char — write 'L' rather than \"L\". A string element was supplied. To pass text, declare the parameter as a collection of string.");
					}
					break;
				}
			}
		}

		// The declared type's element type for a collection, or the type itself for a scalar. A
		// collection parameter may be stored either normalized (IEnumerable<T>) or as the array the
		// caller wrote, so both shapes are read.
		private Type DeclaredElementType()
		{
			if (parameterType == null) return null;
			if (parameterType.IsArray) return parameterType.GetElementType();
			if (parameterType.IsGenericType && parameterType.GenericTypeArguments.Length == 1)
				return parameterType.GenericTypeArguments[0];
			return parameterType;
		}

		private static string DescribeSuppliedText(string suppliedText)
		{
			// Echo the author's OWN value in the char form when it is one character long: that is the
			// case the coercion used to absorb, so showing '<their value>' is the whole remedy. For a
			// longer value there is no char form to suggest, so name the length instead — the value is
			// text and the declaration is what has to change.
			return suppliedText.Length == 1
				? $" — write '{suppliedText}' rather than \"{suppliedText}\""
				: $", but a string of length {suppliedText.Length} was supplied";
		}

		// The CLR type a slot of this declared type actually HOLDS. They coincide for every
		// primitive; the symbol marker is the one place they part, because `typeof(Enum)` declares a
		// READING (this string names an enum member) rather than a representation. Everything that
		// touches the stored object — the compiled slot read, the value writer, the blob reader —
		// must agree on this, or one of them casts to a type the value never had.
		internal static Type StorageTypeOf(Type declaredType)
		{
			ArgumentNullException.ThrowIfNull(declaredType);
			return declaredType == typeof(Enum) ? typeof(string) : declaredType;
		}

		internal static bool IsSupportedParameterType(Type normalizedType)
		{
			ArgumentNullException.ThrowIfNull(normalizedType);

			if (IsPrimitiveScalarParameterType(normalizedType)) return true;

			// A collection is EXACTLY ONE level deep over a primitive scalar, in the two shapes
			// the parameter machinery reads and writes (an array arrives here already normalized
			// to IEnumerable<element>). The element is deliberately NOT recursed on: a nested
			// collection has no wire form, because the writer dispatches on the element type
			// against the primitive scalars and the arguments blob has one nesting level of
			// braces to read back. Admitting one would repeat the half-legal pattern this guard
			// exists to end — a query would run and the journaled command would fail.
			if (normalizedType.IsGenericType)
			{
				Type genericDefinition = normalizedType.GetGenericTypeDefinition();
				if (genericDefinition != typeof(List<>) && genericDefinition != typeof(IEnumerable<>)) return false;
				return IsPrimitiveScalarParameterType(normalizedType.GenericTypeArguments[0]);
			}

			return false;
		}

		private static bool IsPrimitiveScalarParameterType(Type normalizedType)
		{
			if (normalizedType == typeof(int)) return true;
			if (normalizedType == typeof(long)) return true;
			if (normalizedType == typeof(double)) return true;
			if (normalizedType == typeof(decimal)) return true;
			// A single-character string coerces to char at the boundary (TypeConversion).
			if (normalizedType == typeof(char)) return true;
			if (normalizedType == typeof(string)) return true;
			if (normalizedType == typeof(bool)) return true;
			if (normalizedType == typeof(DateTime)) return true;
			// `typeof(Enum)` — the abstract base, never a domain's enum — declares that this
			// string value is a SYMBOL: a member name to be resolved against whatever enum the
			// invoked signature declares. It is admitted for the same reason a domain enum is
			// refused. The refusal is about the assembly boundary, not about symbols: naming
			// `typeof(SomeDomainEnum)` requires that type widened to public and the calling
			// project referencing the domain assembly. `System.Enum` is owned by the LANGUAGE, so
			// it names nothing of the domain and crosses no boundary, while still letting the
			// caller state at the border what a bare string cannot say about itself. The value
			// travels and is stored as a string; only its READING is fixed.
			if (normalizedType == typeof(Enum)) return true;
			return false;
		}

		// Spelled in the language's own type names, not the CLR's, because this is what the author
		// writes. Listing the admitted set is the actionable half of the refusal: the author needs
		// to know which shape to convert TO, not merely that the one they chose was wrong.
		private const string ParameterTypeCatalog =
			"int, long, double, decimal, char, string, bool, datetime, typeof(Enum) for a string that names an enum member, or a one-level collection of the primitives (List<T>, IEnumerable<T>, T[])";

		internal static bool IsValidParameterName(string name)
		{
			bool isFirst = true;
			foreach (char character in name)
			{
				if (char.IsLetter(character)) { }
				else if (character == '_' || character == '#' || character == '@') { }
				else if (char.IsDigit(character) && !isFirst) { }
				else
				{
					return false;
				}
				isFirst = false;
			}
			return true;
		}

		internal string Name => this.name;

		internal int ParameterModifier => this.parameterModifier;

		internal Type ParameterType => this.parameterType;

		internal bool IsNullable => this.isNullableParameter;

		internal bool IsEmpty => this.instance == null;

		internal Program Program
		{
			get
			{
				return program;
			}
			set
			{
				this.program = value;
			}
		}

		internal object Value
		{
			set
			{
				RefuseStringWhereCharIsDeclared(value);

				if (parameterModifier == In)
				{
					if (value == null)
					{
						if (!isNullableParameter)
							throw new LanguageException($"Parameter '{name}' can not be null");

						instance.value = null;
						return;
					}

					var result = TypeConversion.ImplicitCast(value, parameterType);

					if (result == null && value != null)
					{
						throw new ArgumentException($"Cannot convert from {parameterType.Name} to {value.GetType().Name} ");
					}

					var argumentType = result.GetType();

					if (argumentType.IsArray)
					{
						// Materialize the array into a List<elementType> so every collection
						// parameter holds the same shape whether the value arrived as a raw array
						// or a List (the deserialization path already stores List<T>). The array is
						// copied element-wise: Convert.ChangeType to IEnumerable<T> is not a valid
						// conversion (an array does not implement IConvertible) and threw for element
						// types without a dedicated array->List branch in TypeConversion (e.g. long).
						var elementType = argumentType.GetElementType();
						var listType = typeof(List<>).MakeGenericType(elementType);
						var list = (System.Collections.IList)Activator.CreateInstance(listType);
						foreach (var item in (System.Collections.IEnumerable)result)
						{
							list.Add(item);
						}
						instance.value = list;
					}
					else
					{
						// A char parameter fed a single-character string stores the coerced char (the
						// ImplicitCast result), so the stored value matches the declared type. Compiled
						// mode reads the slot as (char)value, which would fail to unbox a string; storing
						// the char keeps `typeof(char)` with a 1-char string value working in both modes.
						// Same reasoning for the symbol marker: a caller may hand over the member NAME
					// or the enum value itself, and both are stored as the NAME, so the stored
					// value always matches what the declared type promises its readers (a string).
					// Accepting the enum value is an ergonomic concession, not a second
					// representation — it is normalized on the way in, once.
					object toStore = ((parameterType == typeof(char) && value is string)
						|| (parameterType == typeof(Enum) && value is Enum)) ? result : value;
						instance.value = toStore;
						previousInstanceValue = toStore;
					}
				}
				else if (parameterModifier == InOut)
				{
					// InOut mirrors In: a nullable InOut (declared `int?`, or a reference type)
					// accepts null on both the input and the output side; a non-nullable InOut
					// (plain value type) still rejects null as a required-value guard.
					if (value == null)
					{
						if (!isNullableParameter)
							throw new LanguageException($"Parameter '{name}' can not be null");

						instance.value = null;
						return;
					}
					instance.value = value;
				}
				else if (parameterModifier == Out)
				{
					// `= null` is the legible "empty output slot" placeholder for ANY Out type
					// (including value types like int/bool/DateTime/decimal). It reads as "no input
					// here; the actor fills this slot". Guard before the unboxing casts below, which
					// would otherwise throw NullReferenceException on (int)null / (bool)null / etc.
					if (value == null) { instance.value = null; return; }

					// Existing non-default-value guard, preserved verbatim. NOTE: due to a
					// dangling-`else` the whole chain is nested under the int branch, so in practice
					// it only rejects a non-default value for an int Out; for string/bool/DateTime/
					// decimal it never runs (e.g. string Out is seeded `= ""` elsewhere, which is not
					// default(string)==null). Left as-is on purpose — callers rely on that leniency;
					// the readability win here is the `= null` placeholder above, not tightening this.
					if (parameterType == typeof(int)) if ((int)value != default(int)) throw new LanguageException($"Parameter '{name}' can not have a defaultdata");
						else if (parameterType == typeof(string)) if ((string)value != default(string)) throw new LanguageException($"Parameter '{name}' can not have a defaultdata");
							else if (parameterType == typeof(bool)) if ((bool)value != default(bool)) throw new LanguageException($"Parameter '{name}' can not have a defaultdata");
								else if (parameterType == typeof(DateTime)) if ((DateTime)value != default(DateTime)) throw new LanguageException($"Parameter '{name}' can not have a defaultdata");
									else if (parameterType == typeof(decimal)) if ((decimal)value != default(decimal)) throw new LanguageException($"Parameter '{name}' can not have a defaultdata");

					instance.value = value;
				}
				else if (parameterModifier == Eval)
				{
					if (string.IsNullOrEmpty(evalScript))
					{
						string type = "";
						if (parameterType == typeof(int))
						{
							type = "int";
						}
						else if (!parameterType.IsGenericType)
						{
							type = parameterType.Name;
						}

						if (parameterType.IsGenericType)
						{
							var parameterGenericType = parameterType.GenericTypeArguments[0];

							if (parameterGenericType == typeof(int))
							{
								type = "List<int>";
							}
							else if (parameterGenericType == typeof(string))
							{
								type = "List<string>";
							}
							else if (parameterGenericType == typeof(bool))
							{
								type = "List<bool>";
							}
							else if (parameterGenericType == typeof(DateTime))
							{
								type = "List<DateTime>";
							}
							else if (parameterGenericType == typeof(decimal))
							{
								type = "List<decimal>";
							}
							else if (parameterGenericType == typeof(double))
							{
								type = "List<double>";
							}
							else
							{
								throw new LanguageException($"Parameter '{name}' can not have a type");
							}
						}

						if (string.IsNullOrEmpty(type)) throw new LanguageException($"Parameter '{name}' can not have a type");

						StringBuilder sb = new StringBuilder();
						sb.Append(this.Name);
						sb.Append($" = ({type})(");
						sb.Append(value);
						sb.Append(");");
						evalScript = sb.ToString();
					}
					else
					{
						instance.value = value;
					}
				}
			}
		}

		public string EvalScript
		{

			get
			{
				return evalScript;
			}
			set
			{
				if (parameterModifier == Eval)
				{
					if (value == null) throw new LanguageException($"Parameter '{name}' can not be null");
					evalScript = value;
				}
				else
				{
					throw new LanguageException($"Parameter '{name}' can not be Eval");
				}
			}
		}

		public object GetValue()
		{
			if (parameterModifier != Out && parameterModifier != Eval && instance == null) throw new LanguageException($"Parameter {this.name} has not been set");
			return instance.value;
		}

		public T GetValue<T>()
		{
			if (parameterModifier != Out && parameterModifier != Eval && instance == null)
				throw new LanguageException($"Parameter {this.name} has not been set");

			if (instance.type != typeof(T))
				throw new InvalidCastException($"Parameter '{name}' type mismatch: expected {instance.type.Name}, got {typeof(T).Name}");

			// An Out slot seeded with the `= null` placeholder and never assigned by the body
			// reads back as default(T). This preserves the pre-existing "unassigned Out reads as
			// default(T)" behavior from when the seed was `= 0` / default(T): a null value type
			// would otherwise throw on the (T)null unboxing below. Reference-type T handles null
			// natively (returns null), so only value types need the coalesce.
			if (instance.value == null && typeof(T).IsValueType)
				return default(T);

			return (T)instance.value;
		}

		internal VariableSymbol AssociateSimbol()
		{
			return instance;
		}

		internal ParameterExpression LValueStorageExpression { get; private set; } = null;
		internal Expression RValueReferenceExpression { get; private set; } = null;

		internal Expression AllocateParameterStorageExpression(ParameterExpression parametersParam, bool isLValue)
		{
			// Claim the storage for THIS lambda before building it; see storageOwner.
			ParameterDeclarationExpression(parametersParam);
			if (LValueStorageExpression != null) throw new LanguageException($"Local storage for parameter '{name}' has already been created.");
			if (RValueReferenceExpression != null) throw new LanguageException($"Local storage for parameter '{name}' has already been created.");

			var assignExpr = Expression.Assign(parameterDeclaration, RuntimeSymbolLookupExpression(parametersParam));

			LValueStorageExpression = parameterDeclaration;

			{
				var objectField = typeof(VariableSymbol).GetField(nameof(VariableSymbol.value), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				var valueExpr = Expression.Field(parameterDeclaration, objectField);
				// Read at the STORAGE type, not the declared one. For every primitive the two
				// coincide; for the symbol marker they do not — `typeof(Enum)` declares how the
				// value must be READ at a call site while the slot holds the member name as a
				// string. Casting the slot to the declared abstract base would fail on the very
				// value the boundary put there.
				Expression convertedValueExpr = Expression.Convert(valueExpr, StorageTypeOf(this.ParameterType));

				// A value-type parameter slot can legitimately hold null: an Out slot carries the
				// `= null` empty '?' placeholder until (and unless) the body assigns it, and a
				// nullable In/InOut slot may be null too. Reading such a slot as an r-value must
				// yield default(T), matching Parameter.GetValue<T>() and the interpreted path
				// (which returns the raw null and lets reflection coalesce it to default(T) for a
				// value-type target). Without this guard, Expression.Convert(null, T) unboxes null
				// and throws NullReferenceException inside the compiled delegate — an NRE where
				// interpreted execution succeeds. Reference-type parameters need no guard: Convert
				// leaves null as null.
				if (this.ParameterType.IsValueType)
				{
					var slotIsNull = Expression.Equal(valueExpr, Expression.Constant(null, typeof(object)));
					convertedValueExpr = Expression.Condition(slotIsNull, Expression.Default(this.ParameterType), convertedValueExpr);
				}

				RValueReferenceExpression = convertedValueExpr;
			}

			var block = Expression.Block(
				assignExpr,
				isLValue ? parameterDeclaration : RValueReferenceExpression
			);

			return block;
		}

		private ParameterExpression parameterDeclaration;

		// The lambda whose Parameters argument the cached storage above was built against. The
		// storage local is per-LAMBDA: it is declared in that lambda's block and (on the first
		// reference) assigned from the Parameters that lambda RECEIVES. Two Programs can
		// reference the very same Parameter object — an Action's body and the sub-program of one
		// of its own Eval parameters do, because the sub-program is resolved against the Action's
		// parameter set — and each must bind from its own argument. Without this key the second
		// Program to compile reused the first one's storage and therefore its
		// ParameterInitializationExpression constant, reading and writing a VariableSymbol
		// captured at the FIRST compilation instead of the set handed to it at run time.
		private ParameterExpression storageOwner;

		internal ParameterExpression ParameterDeclarationExpression(ParameterExpression parametersParam)
		{
			ArgumentNullException.ThrowIfNull(parametersParam);

			if (!ReferenceEquals(storageOwner, parametersParam))
			{
				storageOwner = parametersParam;
				parameterDeclaration = Expression.Variable(typeof(VariableSymbol), $"_$_param_{name}_storage");
				LValueStorageExpression = null;
				RValueReferenceExpression = null;
			}
			return parameterDeclaration;
		}

		// The `parameters[name].instance` lookup against the Parameters instance the compiled
		// lambda RECEIVES. Every initialization of the per-lambda storage local must go through
		// this runtime lookup: capturing the VariableSymbol current at COMPILE time as an
		// Expression.Constant pins the lambda to one specific Parameters instance for its
		// lifetime. For a rehydrated cached Action that instance is the Program's own set —
		// left loaded by replay with the LAST journaled invocation's arguments — so any read
		// that fell back to the constant executed those stale arguments while the journal
		// recorded the fresh ones.
		private Expression RuntimeSymbolLookupExpression(ParameterExpression parametersParam)
		{
			var getItemMethod = typeof(Parameters).GetMethod(
				"get_Item",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy,
				null,
				new[] { typeof(string) },
				null
			);
			var parameterNameExpr = Expression.Constant(this.Name, typeof(string));
			var parameterExpr = Expression.Call(parametersParam, getItemMethod, parameterNameExpr);

			var instanceField = typeof(Parameter).GetField(nameof(Parameter.instance), BindingFlags.NonPublic | BindingFlags.Instance);
			return Expression.Field(parameterExpr, instanceField);
		}

		// Top-of-lambda initialization of the storage local, emitted once per referenced
		// parameter. The inline initialization at the parameter's first compiled reference
		// (AllocateParameterStorageExpression) does NOT necessarily execute: when that first
		// reference sits inside one branch of a conditional and execution takes another
		// branch that also references the parameter, the read reaches the storage local with
		// only THIS initialization applied. It must therefore perform the same per-invocation
		// runtime binding, never a compile-time constant capture (see
		// RuntimeSymbolLookupExpression for why the constant went stale).
		internal Expression ParameterInitializationExpression()
		{
			if (parameterDeclaration == null) throw new LanguageException($"Parameter '{name}' has not been declared yet.");

			var result = Expression.Assign(parameterDeclaration, RuntimeSymbolLookupExpression(storageOwner));

			return result;
		}

		internal void Clear()
		{
			if (parameterModifier == Parameter.In)
			{
				instance.value = previousInstanceValue;
			}
		}

		private object originalSnapshot = null;
		private bool hasOriginalSnapshot = false;

		// Two-phase (check-then-command) support: record the parameter's CURRENT value as
		// its "original" so it can be restored before each check run. A check script runs
		// twice (ReadLock pre-check, WriteLock re-check) and the author does not know that,
		// so any parameter the check dirties must be reset to the caller-supplied value
		// before each run — otherwise the ReadLock run's writes would bleed into the
		// WriteLock run. Independent of Clear()'s post-execution In-restore role.
		internal void SnapshotOriginal()
		{
			originalSnapshot = instance.value;
			hasOriginalSnapshot = true;
		}

		// Restore the value captured by SnapshotOriginal. Eval is excluded: its value is
		// re-evaluated fresh at command-time (WriteLock), not restored from a snapshot.
		internal void RestoreOriginal()
		{
			if (parameterModifier == Eval) return;
			if (!hasOriginalSnapshot) return;
			instance.value = originalSnapshot;
		}

		// Post-execution write-back: store a computed result into this Out/InOut
		// parameter's symbol, bypassing the Value setter's declaration guards. The Out
		// setter forbids assigning a non-default value (it enforces the declaration
		// contract `[Parameter.Out, name, type] = default(...)`); that guard does not
		// apply here, where the framework is propagating a result back to the caller
		// after Perform. Used by the V2 fluent WithParameters(Parameters) path, whose
		// pool-rented copy holds the computed value the caller's instance must receive.
		internal void WriteBackComputedValue(object value)
		{
			instance.value = value;
		}

		// Perf improvement B: fast path for LoadArguments. The value already arrives boxed
		// EXACTLY as ParameterType (the journal parser produces the exact type:
		// int.Parse->int, etc.), so ImplicitCast (a value.GetType() + chain of
		// comparisons) and the In setter's array detection are pure overhead on the
		// hottest replay path. Only applies to In/InOut scalars; Eval/Out fall back to the
		// normal setter to preserve their validation. Collections do NOT use this path:
		// the In setter has array<->IEnumerable conversion logic that is preserved.
		internal void SetParsedScalar(object value)
		{
			if (parameterModifier == In)
			{
				instance.value = value;
				previousInstanceValue = value;
			}
			else if (parameterModifier == InOut)
			{
				instance.value = value;
			}
			else
			{
				Value = value;
			}
		}
	}

}
