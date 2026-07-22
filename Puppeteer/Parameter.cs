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
						object toStore = (parameterType == typeof(char) && value is string) ? result : value;
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
			if (parameterDeclaration == null) parameterDeclaration = Expression.Variable(typeof(VariableSymbol), $"_$_param_{name}_storage");
			if (LValueStorageExpression != null) throw new LanguageException($"Local storage for parameter '{name}' has already been created.");
			if (RValueReferenceExpression != null) throw new LanguageException($"Local storage for parameter '{name}' has already been created.");

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
			var simboloVariableExpression = Expression.Field(parameterExpr, instanceField);

			var assignExpr = Expression.Assign(parameterDeclaration, simboloVariableExpression);

			LValueStorageExpression = parameterDeclaration;

			{
				var objectField = typeof(VariableSymbol).GetField(nameof(VariableSymbol.value), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				var valueExpr = Expression.Field(parameterDeclaration, objectField);
				Expression convertedValueExpr = Expression.Convert(valueExpr, this.ParameterType);

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
		internal ParameterExpression ParameterDeclarationExpression()
		{
			if (parameterDeclaration == null)
			{
				parameterDeclaration = Expression.Variable(typeof(VariableSymbol), $"_$_param_{name}_storage");
			}
			return parameterDeclaration;
		}

		internal Expression ParameterInitializationExpression()
		{
			if (parameterDeclaration == null) throw new LanguageException($"Parameter '{name}' has not been declared yet.");

			Expression simboloVariableExpression = Expression.Constant(instance, typeof(VariableSymbol));
			var result = Expression.Assign(parameterDeclaration, simboloVariableExpression);

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
