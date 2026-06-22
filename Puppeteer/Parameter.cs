using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using Puppeteer.EventSourcing.Interpreter.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer
{

	public sealed class Parameter
	{
		private readonly VariableSymbol instance = null;
		private object instanceViejo = null;
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

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
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

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		internal static bool IsValidParameterName(string name)
		{
			bool esElPrimero = true;
			foreach (char character in name)
			{
				if (char.IsLetter(character)) { }
				else if (character == '_' || character == '#' || character == '@') { }
				else if (char.IsDigit(character) && !esElPrimero) { }
				else
				{
					return false;
				}
				esElPrimero = false;
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
						var elementType = argumentType.GetElementType();
						var CastArregloType = typeof(IEnumerable<>).MakeGenericType(new[] { elementType });
						instance.value = Convert.ChangeType(value, CastArregloType);
					}
					else
					{
						instance.value = value;
						instanceViejo = value;
					}
				}
				else if (parameterModifier == InOut)
				{
					if (value == null) throw new LanguageException($"Parameter '{name}' can not be null");
					instance.value = value;
				}
				else if (parameterModifier == Out)
				{
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
				var objetoField = typeof(VariableSymbol).GetField(nameof(VariableSymbol.value), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				var valueExpr = Expression.Field(parameterDeclaration, objetoField);
				var convertedValueExpr = Expression.Convert(valueExpr, this.ParameterType);
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
				instance.value = instanceViejo;
			}
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
				instanceViejo = value;
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
