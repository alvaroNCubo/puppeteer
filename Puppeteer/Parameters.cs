using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer
{

	public sealed class Parameters : IEnumerable<Parameter>
	{
		private readonly List<Parameter> parameters = new List<Parameter>();
		private bool _hasUserParameter;
		internal static readonly Parameters EMPTY = new Parameters();

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		public Parameters() { }

		internal Parameters(string parameters)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			if (this == EMPTY) throw new LanguageException("Parameters can not be modified for empty instance");

			int position = 0;
			while (position < parameters.Length)
			{
				Blanks(parameters, ref position);
				var parameterModifier = ParameterModify(StringAsParameterModifier(parameters, ref position));

				if (parameterModifier != Parameter.Eval)
				{
					if (parameters[position] != ',') throw new LanguageException("A separator is expected ',' between the name and type of the parameter");
					position++;
					var parameterName = ParameterName(parameters, ref position).ToString();
					Blanks(parameters, ref position);
					Separator(parameters, ref position);
					Blanks(parameters, ref position);
					var parameterType = ParameterType(parameters, ref position);
					Blanks(parameters, ref position);

					if (this.ContainsParameter(parameterName)) throw new LanguageException($"Parameter {parameterName} already exist");
					Parameter parameter = new Parameter(parameterModifier, parameterName, parameterType, ParameterKind.User);
					this.parameters.Add(parameter);
					_hasUserParameter = true;
					if (position >= parameters.Length || parameters[position] != ',') break;
					position++;
				}
				else
				{
					if (parameters[position] != ',') throw new LanguageException("A separator is expected ',' between the name and type of the parameter");
					position++;
					var parameterName = ParameterName(parameters, ref position).ToString();
					Blanks(parameters, ref position);
					Separator(parameters, ref position);
					Blanks(parameters, ref position);
					var parameterType = ParameterType(parameters, ref position);
					Separator(parameters, ref position);
					var evalScript = EvalScript(parameters, ref position);
					Blanks(parameters, ref position);
					if (this.ContainsParameter(parameterName)) throw new LanguageException($"Parameter {parameterName} already exist");
					Parameter parameter = new Parameter(parameterModifier, parameterName, parameterType, ParameterKind.User);
					parameter.EvalScript = evalScript.ToString();
					this.parameters.Add(parameter);
					_hasUserParameter = true;
					if (position >= parameters.Length || parameters[position] != ',') break;
					position++;
				}
			}
			if (position != parameters.Length) throw new LanguageException("Parameter definition is not valid");
		}

		private ReadOnlySpan<char> EvalScript(string parameters, ref int position)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(parameters);
			if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));

			int posicionInicial = position;
			while (position < parameters.Length)
			{
				char currentChar = parameters[position];
				if (currentChar == ',')
				{
					break;
				}
				position++;
			}

			if (posicionInicial == position)
			{
				throw new LanguageException("Script Eval is not valid");
			}
			return parameters.AsSpan(posicionInicial, position - posicionInicial);
		}

		private static int ParameterModify(ReadOnlySpan<char> parameterModify)
		{
			if (parameterModify.Equals("In", StringComparison.Ordinal))
				return Parameter.In;
			if (parameterModify.Equals("Out", StringComparison.Ordinal))
				return Parameter.Out;
			if (parameterModify.Equals("InOut", StringComparison.Ordinal))
				return Parameter.InOut;
			if (parameterModify.Equals("Eval", StringComparison.Ordinal))
				return Parameter.Eval;

			throw new LanguageException($"Parameter modifier '{parameterModify.ToString()}' is not valid");
		}

		internal bool ContainsParameter(string parameterName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(parameterName);

			foreach (Parameter parameter in parameters)
			{
				if (string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		internal bool ParameterHasValue(string parameterName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(parameterName);

			foreach (Parameter parameter in parameters)
			{
				if (string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase))
				{
					return !parameter.IsEmpty;
				}
			}
			return false;
		}

		public List<Parameter>.Enumerator GetEnumerator() => parameters.GetEnumerator();

		IEnumerator<Parameter> IEnumerable<Parameter>.GetEnumerator() => parameters.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => parameters.GetEnumerator();

		public object this[string parameterName, Type parameterType]
		{
			set
			{
				ArgumentNullException.ThrowIfNullOrWhiteSpace(parameterName);
				ArgumentNullException.ThrowIfNull(parameterType);

				this[Parameter.In, parameterName, parameterType] = value;
			}
		}

		public object this[int tipoDeParametro, string parameterName, Type parameterType]
		{
			set
			{
				ArgumentNullException.ThrowIfNullOrWhiteSpace(parameterName);
				ArgumentNullException.ThrowIfNull(parameterType);

				SetParameter(value, tipoDeParametro, parameterName, parameterType, ParameterKind.User);
			}
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		private void SetParameter(object value, int tipoDeParametro, string parameterName, Type parameterType, ParameterKind kind)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(parameterName);
			if (value == null && tipoDeParametro == Parameter.In)
			{
				bool esNullable = !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null;
				if (!esNullable)
					throw new LanguageException($"Parameter '{parameterName}' can not be null");
			}
			else if (value == null && tipoDeParametro != Parameter.Out)
			{
				ArgumentNullException.ThrowIfNull(value);
			}
			if (tipoDeParametro < 0) throw new LanguageException("Parameter Type can not be negative");
			ArgumentNullException.ThrowIfNull(parameterType);
			if (kind < 0) throw new ArgumentOutOfRangeException(nameof(kind));
			if (this == EMPTY) throw new LanguageException("Parameters can not be modified for empty instance");

			Parameter parameter = null;
			foreach (Parameter param in parameters)
			{
				if (string.Equals(param.Name, parameterName, StringComparison.OrdinalIgnoreCase))
				{
					parameter = param;
					break;
				}
			}
			if (parameter == null)
			{
				parameter = new Parameter(tipoDeParametro, parameterName, parameterType, kind);
				parameters.Add(parameter);
				if (kind == ParameterKind.User) _hasUserParameter = true;
			}
			else
			{
				if (parameter.ParameterType != parameterType)
				{
					throw new LanguageException($"Parameter type can not be converted from {parameter.ParameterType.Name} to {parameterType.Name}");
				}
			}
			parameter.Value = value;
		}

		internal Parameter this[string parameterName]
		{
			get
			{
				ArgumentNullException.ThrowIfNullOrWhiteSpace(parameterName);

				Parameter parameter = null;
				foreach (Parameter param in parameters)
				{
					if (string.Equals(param.Name, parameterName, StringComparison.OrdinalIgnoreCase))
					{
						parameter = param;
					}
				}
				if (parameter == null)
				{
					throw new LanguageException($"Unknown parameter {parameterName}");
				}
				return parameter;
			}
		}

		internal void PurgeUserParameters()
		{
			for (int i = parameters.Count - 1; i >= 0; i--)
			{
				if (parameters[i].Kind == ParameterKind.User)
					parameters.RemoveAt(i);
			}
			_hasUserParameter = false;
		}

		internal void Clear()
		{
			foreach (Parameter parameter in parameters)
			{
				parameter.Clear();
			}
		}

		internal void ClearAndPurgeUserParameters()
		{
			for (int i = parameters.Count - 1; i >= 0; i--)
			{
				if (parameters[i].Kind == ParameterKind.User)
					parameters.RemoveAt(i);
				else
					parameters[i].Clear();
			}
			_hasUserParameter = false;
		}

		// Phase 4 of the Action refactor: convert the canonical parameter declaration
		// text (`name:type, name:type`) used in Define statements back to the legacy
		// `In,name:type,In,name:type` format that the Parameters(string) constructor
		// understands. Used by ActorHandler when populating the action cache from a
		// Define journal entry during replay.
		internal static string CanonicalDeclarationsToLegacyFormat(string canonicalText)
		{
			if (string.IsNullOrEmpty(canonicalText)) return string.Empty;

			var sb = new StringBuilder();
			var parts = canonicalText.Split(',');
			bool first = true;
			foreach (var p in parts)
			{
				string trimmed = p.Trim();
				if (trimmed.Length == 0) continue;
				if (!first) sb.Append(',');
				first = false;
				sb.Append("In,");
				sb.Append(trimmed);
			}
			return sb.ToString();
		}

		// Phase 4 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// canonical user-parameter text for the `define action <id> (...)` header.
		// Produces `name:type, name:type` separated by `, ` — same format that Phase 1's
		// parser reads back. Modifiers (In/Out/InOut/Eval) are out of scope for Phase 1's
		// Define statement and are not emitted here. Type names are lowercase to match
		// CanonicalTypeName in Parser.ParseDefineActionParameterList. Empty parameter
		// set (no user parameters) returns the empty string.
		internal string UserParametersAsCanonicalText()
		{
			var sb = new StringBuilder();
			bool first = true;
			foreach (var parameter in parameters)
			{
				if (parameter.Kind != ParameterKind.User) continue;
				if (!first) sb.Append(", ");
				first = false;

				sb.Append(parameter.Name);
				sb.Append(':');
				sb.Append(CanonicalTypeName(parameter.ParameterType));
			}
			return sb.ToString();
		}

		private static string CanonicalTypeName(Type type)
		{
			if (type == typeof(int)) return "int";
			if (type == typeof(string)) return "string";
			if (type == typeof(bool)) return "bool";
			if (type == typeof(double)) return "double";
			if (type == typeof(DateTime)) return "datetime";
			if (type == typeof(decimal)) return "decimal";
			if (type.IsArray)
			{
				return CanonicalTypeName(type.GetElementType()) + "[]";
			}
			if (type.IsGenericType)
			{
				return CanonicalTypeName(type.GenericTypeArguments[0]) + "[]";
			}
			throw new LanguageException($"Type '{type.Name}' is not a valid primitive in 'define action' parameter lists.");
		}

		internal string ParametersAsString()
		{
			var sb = new StringBuilder();
			bool esElprimero = true;
			foreach (var parameter in parameters)
			{
				if (parameter.Kind == ParameterKind.User)
				{
					if (parameter.ParameterModifier != Parameter.Eval)
					{
						if (!esElprimero) sb.Append(',');
						ParameterModifierAsString(parameter.ParameterModifier, sb);
						sb.Append(',');
						sb.Append(parameter.Name);
						sb.Append(':');
						WriteParameterType(parameter.ParameterType, sb);
						esElprimero = false;
					}
					else
					{
						if (!esElprimero) sb.Append(',');
						ParameterModifierAsString(parameter.ParameterModifier, sb);
						sb.Append(',');
						sb.Append(parameter.Name);
						sb.Append(':');
						WriteParameterType(parameter.ParameterType, sb);
						sb.Append(':');
						sb.Append(parameter.EvalScript);
						esElprimero = false;
					}
				}
			}
			return sb.ToString();
		}

		private void ParameterModifierAsString(int type, StringBuilder sb)
		{
			switch (type)
			{
				case 1:
					sb.Append("In");
					break;
				case 2:
					sb.Append("Out");
					break;
				case 3:
					sb.Append("InOut");
					break;
				case 4:
					sb.Append("Eval");
					break;
			}
		}

		private void WriteParameterType(Type type, StringBuilder sb)
		{
			if (type.IsGenericType || type.IsArray)
			{
				WriteSingleParameterType(type.GenericTypeArguments[0], sb);
				sb.Append('[').Append(']');
			}
			else
			{
				WriteSingleParameterType(type, sb);
			}
		}

		private void WriteSingleParameterType(Type type, StringBuilder sb)
		{
			if (type == typeof(string))
			{
				sb.Append("string");
			}
			else if (type == typeof(int))
			{
				sb.Append("int");
			}
			else if (type == typeof(bool))
			{
				sb.Append("bool");
			}
			else if (type == typeof(DateTime))
			{
				sb.Append("DateTime");
			}
			else if (type == typeof(decimal))
			{
				sb.Append("Decimal");
			}
			else if (type == typeof(double))
			{
				sb.Append("double");
			}
			else
			{
				throw new LanguageException("Parameter type not valid");
			}
		}

		private ReadOnlySpan<char> StringAsParameterModifier(string parameters, ref int position)
		{
			int posicionInicial = position;
			while (position < parameters.Length)
			{
				char currentChar = parameters[position];
				if (char.IsLetter(currentChar))
				{
					position++;
				}
				else
				{
					break;
				}
			}

			if (posicionInicial == position)
			{
				throw new LanguageException("Parameter name is not valid");
			}
			return parameters.AsSpan(posicionInicial, position - posicionInicial);
		}

		private ReadOnlySpan<char> ParameterName(string parameters, ref int position)
		{
			bool esElPrimero = true;
			int posicionInicial = position;
			while (position < parameters.Length)
			{
				char currentChar = parameters[position];
				if (char.IsLetter(currentChar))
				{
					position++;
				}
				else if (currentChar == '_' || currentChar == '#' || currentChar == '@')
				{
					position++;
				}
				else if (char.IsDigit(currentChar) && !esElPrimero)
				{
					position++;
				}
				else
				{
					break;
				}

				esElPrimero = false;
			}

			if (posicionInicial == position)
			{
				throw new LanguageException("Parameter name is not valid");
			}
			return parameters.AsSpan(posicionInicial, position - posicionInicial);
		}

		private void Separator(string parameters, ref int position)
		{
			if (parameters[position] == ':')
			{
				position++;
			}
			else
			{
				throw new LanguageException("A separator is expected ':' between the name and type of the parameter");
			}
		}

		private void Blanks(string parameters, ref int position)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(parameters);
			while (position < parameters.Length)
			{
				char currentChar = parameters[position];
				if ((Char.IsWhiteSpace(currentChar) || currentChar == '\t' || currentChar == '\r' || currentChar == '\n'))
				{
					position++;
				}
				else
				{
					break;
				}
			}
		}

		private Type ParameterType(string parameters, ref int position)
		{
			switch (parameters[position])
			{
				case 's':
				case 'S':
					return StringType(parameters, ref position);
				case 'i':
				case 'I':
					return IntType(parameters, ref position);
				case 'b':
				case 'B':
					return BooleanType(parameters, ref position);
				case 'd':
				case 'D':
					if (parameters[position + 1] == 'e' || parameters[position + 1] == 'E')
					{
						return DecimalType(parameters, ref position);
					}
					else if (parameters[position + 1] == 'a' || parameters[position + 1] == 'A')
					{
						return DateTimeType(parameters, ref position);
					}
					else if (parameters[position + 1] == 'o' || parameters[position + 1] == 'O')
					{
						return DoubleType(parameters, ref position);
					}
					break;
			}
			throw new LanguageException($"Unexpected type {parameters.Substring(position)}");
		}

		private bool IsArray(string parameters, ref int position)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(parameters);

			bool resut = false;
			Blanks(parameters, ref position);
			if (parameters.Length <= position) return resut;
			if (parameters[position] == '[')
			{
				position++;
				Blanks(parameters, ref position);
				if (parameters[position] == ']')
				{
					position++;
					resut = true;
				}
				else
				{
					throw new LanguageException($"Unexpected type {parameters.Substring(position)}");
				}
			}
			return resut;
		}
		private Type DateTimeType(string parameters, ref int position)
		{
			if (parameters.Length < position + 8)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 8 - position)} is not a known type");
			}

			bool valid =
				(parameters[position + 0] == 'd' || parameters[position + 0] == 'D') &&
				(parameters[position + 1] == 'a' || parameters[position + 1] == 'A') &&
				(parameters[position + 2] == 't' || parameters[position + 2] == 'T') &&
				(parameters[position + 3] == 'e' || parameters[position + 3] == 'E') &&
				(parameters[position + 4] == 't' || parameters[position + 4] == 'T') &&
				(parameters[position + 5] == 'i' || parameters[position + 5] == 'I') &&
				(parameters[position + 6] == 'm' || parameters[position + 6] == 'M') &&
				(parameters[position + 7] == 'e' || parameters[position + 7] == 'E');

			if (!valid)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 8 - position)} is not a known type");
			}

			position += 8;
			if (IsArray(parameters, ref position))
			{
				return typeof(DateTime[]);
			}
			return typeof(DateTime);
		}

		private Type DoubleType(string parameters, ref int position)
		{
			if (parameters.Length < position + 6)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 6 - position)} is not a known type");
			}

			bool valid =
				(parameters[position + 0] == 'd' || parameters[position + 0] == 'D') &&
				(parameters[position + 1] == 'o' || parameters[position + 1] == 'O') &&
				(parameters[position + 2] == 'u' || parameters[position + 2] == 'U') &&
				(parameters[position + 3] == 'b' || parameters[position + 3] == 'B') &&
				(parameters[position + 4] == 'l' || parameters[position + 4] == 'L') &&
				(parameters[position + 5] == 'e' || parameters[position + 5] == 'E');

			if (!valid)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 6 - position)} is not a known type");
			}

			position += 6;
			if (IsArray(parameters, ref position))
			{
				return typeof(double[]);
			}
			return typeof(double);
		}

		private Type DecimalType(string parameters, ref int position)
		{
			if (parameters.Length < position + 7)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 7 - position)} is not a known type");
			}

			bool valid =
				(parameters[position + 0] == 'd' || parameters[position + 0] == 'D') &&
				(parameters[position + 1] == 'e' || parameters[position + 1] == 'E') &&
				(parameters[position + 2] == 'c' || parameters[position + 2] == 'C') &&
				(parameters[position + 3] == 'i' || parameters[position + 3] == 'I') &&
				(parameters[position + 4] == 'm' || parameters[position + 4] == 'M') &&
				(parameters[position + 5] == 'a' || parameters[position + 5] == 'A') &&
				(parameters[position + 6] == 'l' || parameters[position + 6] == 'L');

			if (!valid)
			{
				throw new LanguageException($"'{parameters.Substring(position, position + 7 - position)}' is not a known type");
			}

			position += 7;
			if (IsArray(parameters, ref position))
			{
				return typeof(decimal[]);
			}
			return typeof(decimal);
		}

		private Type BooleanType(string parameters, ref int position)
		{
			if (parameters.Length < position + 4)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 4 - position)} is not a known type");
			}

			bool valid =
				(parameters[position + 0] == 'b' || parameters[position + 0] == 'B') &&
				(parameters[position + 1] == 'o' || parameters[position + 1] == 'O') &&
				(parameters[position + 2] == 'o' || parameters[position + 2] == 'O') &&
				(parameters[position + 3] == 'l' || parameters[position + 3] == 'L');

			if (!valid)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 4 - position)} is not a known type");
			}
			position += 4;
			if (IsArray(parameters, ref position))
			{
				return typeof(bool[]);
			}
			return typeof(bool);
		}

		private Type IntType(string parameters, ref int position)
		{
			if (parameters.Length < position + 3)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 5 - position)} is not a known type");
			}

			bool valid =
				(parameters[position + 0] == 'i' || parameters[position + 0] == 'I') &&
				(parameters[position + 1] == 'n' || parameters[position + 1] == 'N') &&
				(parameters[position + 2] == 't' || parameters[position + 2] == 'T');

			if (!valid)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 3 - position)} is not a known type");
			}
			position += 3;

			if (IsArray(parameters, ref position))
			{
				return typeof(int[]);
			}

			return typeof(int);
		}

		private Type StringType(string parameters, ref int position)
		{
			if (parameters.Length < position + 6)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 6 - position)} is not a known type");
			}

			bool valid =
				(parameters[position + 0] == 's' || parameters[position + 0] == 'S') &&
				(parameters[position + 1] == 't' || parameters[position + 1] == 'T') &&
				(parameters[position + 2] == 'r' || parameters[position + 2] == 'R') &&
				(parameters[position + 3] == 'i' || parameters[position + 3] == 'I') &&
				(parameters[position + 4] == 'n' || parameters[position + 4] == 'N') &&
				(parameters[position + 5] == 'g' || parameters[position + 5] == 'G');

			if (!valid)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 6 - position)} is not a known type");
			}
			position += 6;
			if (IsArray(parameters, ref position))
			{
				return typeof(string[]);
			}
			return typeof(string);
		}

		internal string ArgumentsAsString(DatabaseType databaseType)
		{
			if (databaseType < 0) throw new ArgumentOutOfRangeException(nameof(databaseType));
			var sb = new StringBuilder();
			bool esElprimero = true;
			foreach (var parameter in parameters)
			{
				if (parameter.Kind == ParameterKind.User)
				{
					if (!esElprimero) sb.Append(',');
					Type parameterType = parameter.ParameterType;
					if (parameterType.IsGenericType || parameterType.IsArray)
					{

						WriteSingleValueCollection(parameter, sb, databaseType);
					}
					else if (parameter.ParameterModifier == Parameter.Out)
					{
						sb.Append('?');
					}
					else
					{
						WriteSingleValuePrimitive(parameter, sb, databaseType);
					}
					esElprimero = false;
				}
			}
			return sb.ToString();
		}

		internal void LoadArguments(string agumentsAsString)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(agumentsAsString);

			int position = 0;
			for (int p = 0; p < parameters.Count; p++)
			{
				var parameter = parameters[p];
				if (parameter.Kind == ParameterKind.User)
				{
					Blanks(agumentsAsString, ref position);
					if (parameter.ParameterModifier == Parameter.Out)
					{
						if (agumentsAsString[position] != '?') throw new LanguageException("Parameter definition is not valid");
						position++;

						object dummyValue = DefaultValueForType(parameter.ParameterType);
						if (dummyValue != null)
						{
							this[parameter.ParameterModifier, parameter.Name, parameter.ParameterType] = dummyValue;
						}
					}
					else if (parameter.ParameterType.IsGenericType || parameter.ParameterType.IsArray)
					{
						ArgumentsValueCollection(parameter, agumentsAsString, ref position);
					}
					else
					{
						ArgumentsValue(parameter, agumentsAsString, ref position);
					}
					Blanks(agumentsAsString, ref position);
					if (position < agumentsAsString.Length && p != (parameters.Count - 1))
					{
						if (agumentsAsString[position] == ',')
							position++;
						else
							throw new LanguageException("Parameter definition is not valid");
					}
				}
			}
			if (position != agumentsAsString.Length) throw new LanguageException("Parameter definition is not valid");
		}

		private static object DefaultValueForType(Type type)
		{
			if (type == typeof(int)) return default(int);
			if (type == typeof(bool)) return default(bool);
			if (type == typeof(DateTime)) return default(DateTime);
			if (type == typeof(decimal)) return default(decimal);
			if (type == typeof(double)) return default(double);
			if (type == typeof(string)) return "";
			return null;
		}

		private void ArgumentsValueCollection(Parameter parameter, string agumentsAsString, ref int position)
		{
			Type parameterType = parameter.ParameterType;

			if (parameterType.GenericTypeArguments[0] == typeof(int))
			{
				parameter.Value = ValueCollectionInt(agumentsAsString, ref position);
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(string))
			{
				parameter.Value = ValueCollectionString(agumentsAsString, ref position);
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(bool))
			{
				parameter.Value = ValueCollectionBool(agumentsAsString, ref position);
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(DateTime))
			{
				parameter.Value = ValueCollectionDateTime(agumentsAsString, ref position);
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(decimal))
			{
				parameter.Value = ValueCollectionDecimal(agumentsAsString, ref position);
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(double))
			{
				parameter.Value = ValueCollectionDouble(agumentsAsString, ref position);
			}
			else
			{
				throw new LanguageException("Parameter type is not valid");
			}
		}

		private object ValueCollectionInt(string agumentsAsString, ref int position)
		{
			List<int> list = new List<int>();
			if (agumentsAsString[position] != '{') throw new LanguageException("Parameter definition is not valid");
			position++;
			int startPosition = position;
			if (agumentsAsString[position] == '}')
			{
				position++;
				return Enumerable.Empty<int>();
			}

			while (position < agumentsAsString.Length)
			{
				if (agumentsAsString[position] == ',')
				{
					list.Add(int.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(int.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					position++;
					break;
				}
				else if (position >= agumentsAsString.Length)
				{
					throw new LanguageException("Parameter definition is not valid");
				}
				position++;
			}

			return list;
		}

		private object ValueCollectionString(string agumentsAsString, ref int position)
		{
			List<string> list = new List<string>();
			bool esLaPrimeraLetra = true;
			if (agumentsAsString[position] != '{') throw new LanguageException("Parameter definition is not valid");
			position++;
			int startPosition = position;
			if (agumentsAsString[position] == '}')
			{
				position++;
				return Enumerable.Empty<string>();
			}

			while (position < agumentsAsString.Length)
			{
				if (esLaPrimeraLetra)
				{
					if (agumentsAsString[position] != '\'') throw new LanguageException("Parameter definition is not valid");
					esLaPrimeraLetra = false;
				}
				else if (agumentsAsString[position] == ',' && agumentsAsString[position - 1] == '\'')
				{
					list.Add(agumentsAsString.AsSpan(startPosition + 1, position - startPosition - 2).ToString());
					startPosition = position + 1;
					esLaPrimeraLetra = true;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(agumentsAsString.AsSpan(startPosition + 1, position - startPosition - 2).ToString());
					position++;
					break;
				}
				else if (position >= agumentsAsString.Length)
				{
					throw new LanguageException("Parameter definition is not valid");
				}
				position++;
			}
			return list;
		}

		private object ValueCollectionBool(string agumentsAsString, ref int position)
		{
			List<bool> list = new List<bool>();
			if (agumentsAsString[position] != '{') throw new LanguageException("Parameter definition is not valid");
			position++;
			int startPosition = position;
			if (agumentsAsString[position] == '}')
			{
				position++;
				return Enumerable.Empty<bool>();
			}

			while (position < agumentsAsString.Length)
			{
				if (agumentsAsString[position] == ',')
				{
					list.Add(bool.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(bool.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					position++;
					break;
				}
				else if (position >= agumentsAsString.Length)
				{
					throw new LanguageException("Parameter definition is not valid");
				}
				position++;
			}
			return list;
		}

		private object ValueCollectionDecimal(string agumentsAsString, ref int position)
		{
			List<decimal> list = new List<decimal>();
			if (agumentsAsString[position] != '{') throw new LanguageException("Parameter definition is not valid");
			position++;
			int startPosition = position;

			if (agumentsAsString[position] == '}')
			{
				position++;
				return Enumerable.Empty<decimal>();
			}

			while (position < agumentsAsString.Length)
			{
				if (agumentsAsString[position] == ',')
				{
					list.Add(decimal.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(decimal.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					position++;
					break;
				}
				else if (position >= agumentsAsString.Length)
				{
					throw new LanguageException("Parameter definition is not valid");
				}
				position++;
			}
			return list;
		}

		private object ValueCollectionDouble(string agumentsAsString, ref int position)
		{
			List<double> list = new List<double>();
			if (agumentsAsString[position] != '{') throw new LanguageException("Parameter definition is not valid");
			position++;
			int startPosition = position;

			if (agumentsAsString[position] == '}')
			{
				position++;
				return Enumerable.Empty<double>();
			}

			while (position < agumentsAsString.Length)
			{
				if (agumentsAsString[position] == ',')
				{
					list.Add(double.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(double.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					position++;
					break;
				}
				else if (position >= agumentsAsString.Length)
				{
					throw new LanguageException("Parameter definition is not valid");
				}
				position++;
			}
			return list;
		}

		private object ValueCollectionDateTime(string agumentsAsString, ref int position)
		{
			List<DateTime> list = new List<DateTime>();
			if (agumentsAsString[position] != '{') throw new LanguageException("Parameter definition is not valid");
			position++;
			int startPosition = position;

			if (agumentsAsString[position] == '}')
			{
				position++;
				return Enumerable.Empty<DateTime>();
			}

			while (position < agumentsAsString.Length)
			{
				if (agumentsAsString[position] == ',')
				{
					list.Add(DateTime.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(DateTime.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition)));
					position++;
					break;
				}
				else if (position >= agumentsAsString.Length)
				{
					throw new LanguageException("Parameter definition is not valid");
				}
				position++;
			}
			return list;
		}

		private void ArgumentsValue(Parameter parameter, string agumentsAsString, ref int position)
		{
			Type parameterType = parameter.ParameterType;

			int startPosition = position;

			if (parameterType == typeof(string))
			{
				parameter.Value = ValueString(agumentsAsString, ref position).ToString();
			}
			else if (parameterType == typeof(int))
			{
				parameter.Value = int.Parse(Value(agumentsAsString, ref position));
			}
			else if (parameterType == typeof(bool))
			{
				parameter.Value = bool.Parse(Value(agumentsAsString, ref position));
			}
			else if (parameterType == typeof(DateTime))
			{
				parameter.Value = DateTime.Parse(Value(agumentsAsString, ref position));
			}
			else if (parameterType == typeof(decimal))
			{
				parameter.Value = Decimal.Parse(Value(agumentsAsString, ref position));
			}
			else if (parameterType == typeof(double))
			{
				parameter.Value = Double.Parse(Value(agumentsAsString, ref position));
			}
			else
			{
				throw new LanguageException("type no valido");
			}
		}

		private ReadOnlySpan<char> Value(string agumentsAsString, ref int position)
		{
			int startPosition = position;
			while (position < agumentsAsString.Length && agumentsAsString[position] != ',')
			{
				position++;
			}
			return agumentsAsString.AsSpan(startPosition, position - startPosition);
		}

		private ReadOnlySpan<char> ValueString(string agumentsAsString, ref int position)
		{
			int startPosition = position;
			if (agumentsAsString[position++] != '\'') throw new LanguageException("Parameter definition is not valid");
			int inicioDelString = position;
			while (position < agumentsAsString.Length)
			{
				if (agumentsAsString[position] == '\'' && agumentsAsString.Length == position + 1) break;
				if ((agumentsAsString[position] == '\'' && agumentsAsString[position + 1] == ',')) break;
				position++;
			}
			int finDelString = position;
			if (agumentsAsString[position++] != '\'') throw new LanguageException("Parameter definition is not valid");
			return agumentsAsString.AsSpan(inicioDelString, finDelString - inicioDelString);
		}

		private void WriteSingleValueCollection(Parameter parameter, StringBuilder sb, DatabaseType databaseType)
		{
			Type parameterType = parameter.ParameterType;
			object value = parameter.GetValue();

			if (parameterType.GenericTypeArguments[0] == typeof(int))
			{
				if (parameterType == typeof(List<int>))
				{
					Append((List<int>)value, sb);
				}
				else if (parameterType == typeof(IEnumerable<int>))
				{
					Append((IEnumerable<int>)value, sb);
				}
				else
				{
					Append((int[])value, sb);
				}
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(string))
			{
				if (parameterType == typeof(List<string>))
				{
					Append((List<string>)value, sb, databaseType);
				}
				else if (parameterType == typeof(IEnumerable<string>))
				{
					Append((IEnumerable<string>)value, sb, databaseType);
				}
				else
				{
					Append((string[])value, sb, databaseType);
				}
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(bool))
			{
				if (parameterType == typeof(List<bool>))
				{
					Append((List<bool>)value, sb);
				}
				else if (parameterType == typeof(IEnumerable<bool>))
				{
					Append((IEnumerable<bool>)value, sb);
				}
				else
				{
					Append((bool[])value, sb);
				}
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(DateTime))
			{
				if (parameterType == typeof(List<DateTime>))
				{
					Append((List<DateTime>)value, sb);
				}
				else if (parameterType == typeof(IEnumerable<DateTime>))
				{
					Append((IEnumerable<DateTime>)value, sb);
				}
				else
				{
					Append((DateTime[])value, sb);
				}
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(decimal))
			{
				if (parameterType == typeof(List<decimal>))
				{
					Append((List<decimal>)value, sb);
				}
				else if (parameterType == typeof(IEnumerable<decimal>))
				{
					Append((IEnumerable<decimal>)value, sb);
				}
				else
				{
					Append((decimal[])value, sb);
				}
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(double))
			{
				if (parameterType == typeof(List<double>))
				{
					Append((List<double>)value, sb);
				}
				else if (parameterType == typeof(IEnumerable<double>))
				{
					Append((IEnumerable<double>)value, sb);
				}
				else
				{
					Append((double[])value, sb);
				}
			}
			else
			{
				throw new LanguageException("type no valido");
			}
		}

		private void WriteSingleValuePrimitive(Parameter parameter, StringBuilder sb, DatabaseType databaseType)
		{
			Type parameterType = parameter.ParameterType;
			if (parameterType == typeof(string))
			{
				Append((string)parameter.GetValue(), sb, databaseType);
			}
			else if (parameterType == typeof(int))
			{
				Append((int)parameter.GetValue(), sb);
			}
			else if (parameterType == typeof(bool))
			{
				Append((bool)parameter.GetValue(), sb);
			}
			else if (parameterType == typeof(DateTime))
			{
				Append((DateTime)parameter.GetValue(), sb);
			}
			else if (parameterType == typeof(decimal))
			{
				Append((decimal)parameter.GetValue(), sb);
			}
			else if (parameterType == typeof(double))
			{
				Append((double)parameter.GetValue(), sb);
			}
			else
			{
				throw new LanguageException("type no valido");
			}
		}

		private void Append(double[] values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(DateTime[] values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				if (value.Hour == 00 && value.Minute == 00 && value.Second == 00)
					sb.Append(value.ToString("MM/dd/yyyy"));
				else
					sb.Append(value.ToString("MM/dd/yyyy HH:mm:ss"));
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(decimal[] values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value.ToString("0.######################"));
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(bool[] values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(int[] values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(string[] values, StringBuilder sb, DatabaseType databaseType)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				LiteralString.Write(sb, value, databaseType);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(List<double> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(List<DateTime> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				if (value.Hour == 00 && value.Minute == 00 && value.Second == 00)
					sb.Append(value.ToString("MM/dd/yyyy"));
				else
					sb.Append(value.ToString("MM/dd/yyyy HH:mm:ss"));
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(List<decimal> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value.ToString("0.######################"));
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(List<bool> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(List<int> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(List<string> values, StringBuilder sb, DatabaseType databaseType)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				LiteralString.Write(sb, value, databaseType);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<double> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<DateTime> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				if (value.Hour == 00 && value.Minute == 00 && value.Second == 00)
					sb.Append(value.ToString("MM/dd/yyyy"));
				else
					sb.Append(value.ToString("MM/dd/yyyy HH:mm:ss"));
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<decimal> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value.ToString("0.######################"));
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<bool> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<int> values, StringBuilder sb)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				sb.Append(value);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<string> values, StringBuilder sb, DatabaseType databaseType)
		{
			var esElPrimero = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!esElPrimero) sb.Append(',');
				LiteralString.Write(sb, value, databaseType);
				esElPrimero = false;
			}
			sb.Append('}');
		}

		private void Append(double value, StringBuilder sb)
		{
			sb.Append(value);
		}

		private void Append(decimal value, StringBuilder sb)
		{
			sb.Append(value.ToString("0.######################"));
		}

		private void Append(DateTime value, StringBuilder sb)
		{
			if (value.Hour == 00 && value.Minute == 00 && value.Second == 00)
				sb.Append(value.ToString("MM/dd/yyyy"));
			else
				sb.Append(value.ToString("MM/dd/yyyy HH:mm:ss"));
		}

		private void Append(bool value, StringBuilder sb)
		{
			sb.Append(value);
		}

		private void Append(int value, StringBuilder sb)
		{
			sb.Append(value);
		}

		private void Append(string value, StringBuilder sb, DatabaseType databaseType)
		{
			LiteralString.Write(sb, value, databaseType);
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		public void SystemParameter<T>(string parameterName, T value)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			ArgumentNullException.ThrowIfNull(parameterName);
			if (value != null && !(value is T))
				throw new ArgumentException($"Value is not of type {typeof(T).FullName}");

			SetParameter(value, Parameter.In, parameterName, typeof(T), ParameterKind.System);
		}

		// Paper 5 Lab 1: public counterpart to SystemParameter so the V2 fluent API
		// can declare USER parameters. Triggers the JournalEntry.IsNewAction /
		// IsExistingAction persistence path (compact ActionEventData entries), as
		// opposed to System parameters which always route to IsScript (literal
		// script with substituted canonical body). Lab-policy visibility bump —
		// the live indexer that took this role was internal-only.
		public void UserParameter<T>(string parameterName, T value)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			ArgumentNullException.ThrowIfNull(parameterName);
			if (value != null && !(value is T))
				throw new ArgumentException($"Value is not of type {typeof(T).FullName}");

			SetParameter(value, Parameter.In, parameterName, typeof(T), ParameterKind.User);
		}

		internal bool HasUserParameter()
		{
			return _hasUserParameter;
		}

		public string SerializeForTransport(DatabaseType databaseType)
		{
			if (databaseType < 0) throw new ArgumentOutOfRangeException(nameof(databaseType));
			if (!HasUserParameter()) return string.Empty;

			var declarations = ParametersAsString();
			var arguments = ArgumentsAsString(databaseType);

			var sb = new StringBuilder(declarations.Length + 1 + arguments.Length);
			sb.Append(declarations);
			sb.Append('|');
			sb.Append(arguments);
			return sb.ToString();
		}

		public static Parameters DeserializeFromTransport(string serialized)
		{
			if (string.IsNullOrEmpty(serialized)) return null;

			int separatorIndex = serialized.IndexOf('|');
			if (separatorIndex < 0) throw new LanguageException("Invalid transport format: missing separator '|'");

			string declarations = serialized.Substring(0, separatorIndex);
			string arguments = serialized.Substring(separatorIndex + 1);

			var parameters = new Parameters(declarations);
			if (!string.IsNullOrEmpty(arguments))
			{
				parameters.LoadArguments(arguments);
			}
			return parameters;
		}

		internal bool IsStructuralEquivalentTo(Parameters other)
		{
			if (other == null) return false;

			int thisUserParamCount = 0;
			int otherUserParamCount = 0;

			foreach (var p in this.parameters)
				if (p.Kind == ParameterKind.User) thisUserParamCount++;

			foreach (var p in other.parameters)
				if (p.Kind == ParameterKind.User) otherUserParamCount++;

			if (thisUserParamCount != otherUserParamCount)
				return false;

			int index = 0;
			foreach (var thisParam in this.parameters)
			{
				if (thisParam.Kind == ParameterKind.User)
				{
					Parameter otherParam = null;
					int otherIndex = 0;
					foreach (var p in other.parameters)
					{
						if (p.Kind == ParameterKind.User)
						{
							if (otherIndex == index)
							{
								otherParam = p;
								break;
							}
							otherIndex++;
						}
					}

					if (otherParam == null)
						return false;

					if (!string.Equals(thisParam.Name, otherParam.Name, StringComparison.OrdinalIgnoreCase))
						return false;

					if (thisParam.ParameterType != otherParam.ParameterType)
						return false;

					if (thisParam.ParameterModifier != otherParam.ParameterModifier)
						return false;

					index++;
				}
			}

			return true;
		}

	}

}
