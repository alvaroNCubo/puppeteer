using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using Puppeteer.EventSourcing.Interpreter.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer
{

	public sealed class Parameters : IEnumerable<Parameter>
	{
		private readonly List<Parameter> parameters = new List<Parameter>();
		internal static readonly Parameters EMPTY = new Parameters();

		// Lever 3 of the Now optimization: direct reference to the slot of the SYSTEM
		// parameter Now, cached on the first SetNow. The parameter shape is invariant
		// per operation (same script => same slots) and the pool-by-shape reuses the
		// instance without purging, so after the first SetNow the Now injection is O(1):
		// without the linear search by name nor the ImplicitCast of the object indexer. The
		// keyless pool (which DOES purge the whole list on Rent) resets this reference in
		// PurgeUserParameters so it is not left dangling toward an already-removed slot.
		private Parameter nowSlot;

		public Parameters() { }

		// No type resolver: a parameter declaration is read against the LANGUAGE's primitive
		// types alone. Re-parsing never consults the actor's libraries because no domain type
		// can appear in a declaration to begin with (see Parameter.IsSupportedParameterType).
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
					Parameter parameter = new Parameter(parameterModifier, parameterName, parameterType);
					this.parameters.Add(parameter);
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
					Parameter parameter = new Parameter(parameterModifier, parameterName, parameterType);
					parameter.EvalScript = evalScript.ToString();
					this.parameters.Add(parameter);
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

			int initialPosition = position;
			while (position < parameters.Length)
			{
				char currentChar = parameters[position];
				if (currentChar == ',')
				{
					break;
				}
				position++;
			}

			if (initialPosition == position)
			{
				throw new LanguageException("Script Eval is not valid");
			}
			return parameters.AsSpan(initialPosition, position - initialPosition);
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

		public object this[int parameterKind, string parameterName, Type parameterType]
		{
			set
			{
				ArgumentNullException.ThrowIfNullOrWhiteSpace(parameterName);
				ArgumentNullException.ThrowIfNull(parameterType);

				SetParameter(value, parameterKind, parameterName, parameterType);
			}
		}

		private void SetParameter(object value, int parameterKind, string parameterName, Type parameterType)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(parameterName);
			if (value == null && (parameterKind == Parameter.In || parameterKind == Parameter.InOut))
			{
				bool isNullable = !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null;
				if (!isNullable)
					throw new LanguageException($"Parameter '{parameterName}' can not be null");
			}
			else if (value == null && parameterKind != Parameter.Out)
			{
				// Remaining null-accepting kind here is Eval, which requires a script value.
				ArgumentNullException.ThrowIfNull(value);
			}
			if (parameterKind < 0) throw new LanguageException("Parameter Type can not be negative");
			ArgumentNullException.ThrowIfNull(parameterType);
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
				parameter = new Parameter(parameterKind, parameterName, parameterType);
				parameters.Add(parameter);
			}
			else
			{
				// The slot already exists (e.g. a Parameters instance reused from the pool-by-shape).
				// parameter.ParameterType is NORMALIZED (array -> IEnumerable<elem>, Nullable<T> -> T)
				// by the Parameter ctor, but the type incoming through the indexer is raw. It must be
				// normalized with the SAME helper before comparing; otherwise a re-set of an
				// array @parameter (DateTime[], string[], ...) over the reused slot would trip the
				// guard because IEnumerable<DateTime> != DateTime[].
				Type normalizedIncoming = Parameter.NormalizeParameterType(parameterType);
				if (parameter.ParameterType != normalizedIncoming)
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

		// Playbill final refactor: after removing the SystemParameter notion (Now/Ip/User),
		// every parameter is a user parameter. Purges ALL parameters — the name is kept
		// so as not to break the callsites in MatchTree / Pattern / Reaction / pool.Return.
		internal void PurgeUserParameters()
		{
			parameters.Clear();
			nowSlot = null;
		}

		// Lever 3 of the Now optimization: typed setter for the SYSTEM parameter Now.
		// Now is always DateTime, so the object indexer route is avoided
		// (this[string,Type]) which does a linear search by name and then ImplicitCast. The
		// slot is located or created only once and cached in nowSlot; in operations that
		// reuse the instance from the pool-by-shape, the following SetNow calls are O(1). The only
		// unavoidable box (DateTime -> the object field of VariableSymbol, which both the
		// interpreter and the codegen read) happens inside SetParsedScalar, just as in the
		// original indexer path. Semantically it is equivalent to `this["Now", typeof(DateTime)]
		// = now` but without the search + conversion overhead.
		internal void SetNow(DateTime now)
		{
			if (this == EMPTY) throw new LanguageException("Parameters can not be modified for empty instance");

			if (nowSlot == null)
			{
				foreach (Parameter parameter in parameters)
				{
					if (IsSystemNow(parameter))
					{
						nowSlot = parameter;
						break;
					}
				}
				if (nowSlot == null)
				{
					nowSlot = new Parameter(Parameter.In, SystemNowName, typeof(DateTime));
					parameters.Add(nowSlot);
				}
			}
			nowSlot.SetParsedScalar(now);
		}

		// Ensure the SYSTEM parameter Now slot EXISTS without assigning a value. Used when
		// reconstructing a `define action` whose body references Now: the canonical header
		// excludes Now (UserParametersAsCanonicalText / ArgumentsAsString drop it), so the
		// rebuilt Parameters must re-add the slot before interpreted reference resolution can
		// bind the body's Now reference as a parameter. The value is injected later, per
		// replayed invocation, from the journaled OccurredAt. Idempotent: a pre-existing Now
		// slot (or a prior SetNow) is reused, never duplicated.
		internal void EnsureNowSlot()
		{
			if (this == EMPTY) throw new LanguageException("Parameters can not be modified for empty instance");

			if (nowSlot != null) return;
			foreach (Parameter parameter in parameters)
			{
				if (IsSystemNow(parameter))
				{
					nowSlot = parameter;
					return;
				}
			}
			nowSlot = new Parameter(Parameter.In, SystemNowName, typeof(DateTime));
			parameters.Add(nowSlot);
		}

		internal void Clear()
		{
			foreach (Parameter parameter in parameters)
			{
				parameter.Clear();
			}
		}

		// Two-phase (check-then-command) support: snapshot every parameter's current value
		// as its "original", and restore them, so the double-executed check always evaluates
		// from clean caller inputs. See Parameter.SnapshotOriginal / RestoreOriginal.
		internal void SnapshotOriginals()
		{
			foreach (Parameter parameter in parameters)
			{
				// The system Now is re-injected per Perform (SetNow); it is not a caller
				// input, so it is neither snapshotted nor restored.
				if (IsSystemNow(parameter)) continue;
				parameter.SnapshotOriginal();
			}
		}

		internal void RestoreOriginals()
		{
			foreach (Parameter parameter in parameters)
			{
				if (IsSystemNow(parameter)) continue;
				parameter.RestoreOriginal();
			}
		}

		// V2 fluent write-back: after a pool-rented copy has executed, propagate the
		// computed values of THIS instance's Out/InOut parameters from `executed`,
		// matched by name. THIS (the caller's original instance) is the source of truth
		// for which parameters are Out/InOut — that decision does not depend on the copy's
		// modifiers, so it stays correct even if the pooled slot's kind ever differs.
		// WithParameters(Parameters) preserves the Out/InOut modifier when copying in, so
		// Program's post-execution Parameters.Clear() (which resets only In parameters)
		// leaves the computed Out/InOut value intact in `executed` for this read. The write
		// goes straight to the caller's symbol, bypassing the Out declaration guard
		// (see Parameter.WriteBackComputedValue).
		internal void WriteBackOutputsFrom(Parameters executed)
		{
			if (executed == null || executed == EMPTY || this == EMPTY) return;

			foreach (Parameter destination in parameters)
			{
				int kind = destination.ParameterModifier;
				if (kind != Parameter.Out && kind != Parameter.InOut) continue;
				if (!executed.ContainsParameter(destination.Name)) continue;

				destination.WriteBackComputedValue(executed[destination.Name].GetValue());
			}
		}

		// Phase 4 of the Action refactor: convert the canonical parameter declaration
		// text (`[out|inout ]name:type, ...`) used in Define statements into the
		// modifier-prefixed `In,name:type,Out,name:type,...` form that the Parameters(string)
		// constructor parses (the same form ParametersAsString produces). Used by
		// ActorHandler when populating the action cache from a Define journal entry
		// during replay.
		//
		// The header carries the modifier as a lowercase keyword prefix on the entries that
		// need it (Out/InOut); an entry with no prefix is In. This translation lifts that
		// prefix into the exact-cased modifier token the ctor grammar expects, so the
		// rebuilt Parameters carry the true modifier and LoadArguments takes the matching
		// branch on replay. A header without any prefix (pre-existing journals) maps every
		// entry to In, exactly as before.
		internal static string CanonicalDeclarationsToParametersString(string canonicalText)
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

				// A leading `out `/`inout ` keyword (space-separated from the name) selects
				// the modifier; anything else is a bare `name:type` and stays In. The name
				// itself may legitimately be `out`/`inout` — that case has no space before
				// the ':' so it is not mistaken for a prefix.
				string declaration = trimmed;
				string modifierToken = "In";
				int space = trimmed.IndexOf(' ');
				if (space > 0)
				{
					string word = trimmed.Substring(0, space);
					if (string.Equals(word, OutModifierKeyword, StringComparison.OrdinalIgnoreCase))
					{
						modifierToken = "Out";
						declaration = trimmed.Substring(space + 1).Trim();
					}
					else if (string.Equals(word, InOutModifierKeyword, StringComparison.OrdinalIgnoreCase))
					{
						modifierToken = "InOut";
						declaration = trimmed.Substring(space + 1).Trim();
					}
				}

				sb.Append(modifierToken);
				sb.Append(',');
				sb.Append(declaration);
			}
			return sb.ToString();
		}

		// Phase 4 of the Action refactor (project_puppeteer_action_refactor_plan.md):
		// canonical user-parameter text for the `define action <id> (...)` header.
		// Produces `[out|inout ]name:type` entries separated by `, ` — the same format
		// Phase 1's parser reads back. Type names are lowercase to match CanonicalTypeName
		// in Parser.ParseDefineActionParameterList. Empty parameter set (no user
		// parameters) returns the empty string.
		//
		// The In/Out/InOut modifier is preserved because it is not recoverable from
		// name:type alone, yet it governs replay: an Out (and a null-valued InOut) argument
		// is journaled as the '?' placeholder, and LoadArguments only reads '?' for a slot
		// whose modifier is Out/InOut. If the header dropped the modifier, every slot was
		// rebuilt as In and LoadArguments took the null branch on '?', tripping the In
		// non-nullable guard and blocking replay of any Out-parametric Action. The modifier
		// is spelled as a lowercase KEYWORD PREFIX (`out name:type`, `inout name:type`); In
		// is the default and emits no prefix, so a headerless entry still means In. That
		// keeps older journals (written before the modifier was carried) readable as-is.
		// Eval is deliberately left prefix-less too: its computed value travels in the
		// arguments blob and is reconstructed as a value-in argument, so it round-trips as
		// In (see ParameterSignature.ModifiersAreCompatible for the In/Eval equivalence).
		// Now is a SYSTEM parameter: the framework injects it on every Perform with the
		// OccurredAt value (DateTime.Now live, OccurredAt on replay). It is kept
		// as a per-call parameter — thread-safe and visible to static pattern matching as
		// id.IsParameter == true — but it is EXCLUDED from the canonical `define action`
		// signature and from the journal's arguments blob; on replay it is re-injected from OccurredAt. It is
		// a SystemParameter distinction scoped to Now (Ip/User remain out of the journal via
		// Playbill). Exclusion by name: the codebase already reserves 'Now'/'User'/'Ip' by
		// name (ReservedSeekNames, bindings filters in Reaction). The exclusion is
		// SYMMETRIC in ArgumentsAsString and LoadArguments to preserve the positional
		// alignment of the serialization.
		internal const string SystemNowName = "Now";
		internal static bool IsSystemNow(Parameter parameter)
		{
			return string.Equals(parameter.Name, SystemNowName, StringComparison.OrdinalIgnoreCase);
		}

		internal string UserParametersAsCanonicalText()
		{
			var sb = new StringBuilder();
			bool first = true;
			foreach (var parameter in parameters)
			{
				if (IsSystemNow(parameter)) continue;

				if (!first) sb.Append(", ");
				first = false;

				sb.Append(CanonicalModifierPrefix(parameter.ParameterModifier));
				sb.Append(parameter.Name);
				sb.Append(':');
				sb.Append(CanonicalTypeName(parameter.ParameterType));
			}
			return sb.ToString();
		}

		// Lowercase keyword that prefixes a `define action` header parameter to carry its
		// modifier. Only Out and InOut are emitted; In (and Eval, which round-trips as a
		// value-in argument) default to no prefix so headerless entries and pre-existing
		// journals keep meaning In. The keywords are chosen to lex as plain identifiers
		// (unlike `in`/`eval`, which are reserved tokens), so the parser can read them back
		// as a contextual prefix. Symmetric with ParseDefineActionParameterList (read) and
		// CanonicalDeclarationsToParametersString (translation to the ctor grammar).
		internal const string OutModifierKeyword = "out";
		internal const string InOutModifierKeyword = "inout";
		private static string CanonicalModifierPrefix(int parameterModifier)
		{
			if (parameterModifier == Parameter.Out) return OutModifierKeyword + " ";
			if (parameterModifier == Parameter.InOut) return InOutModifierKeyword + " ";
			return string.Empty;
		}

		private static string CanonicalTypeName(Type type)
		{
			if (type == typeof(int)) return "int";
			if (type == typeof(long)) return "long";
			if (type == typeof(string)) return "string";
			if (type == typeof(char)) return "char";
			if (type == typeof(bool)) return "bool";
			if (type == typeof(double)) return "double";
			if (type == typeof(DateTime)) return "datetime";
			if (type == typeof(decimal)) return "decimal";
			// The symbol marker. Written as its own keyword rather than as `string` because the
			// header is what replay reads to decide how the value binds: `rule:string` and
			// `rule:enum` carry the same characters in the blob and different intent, and the
			// intent is the half that must survive.
			if (type == typeof(Enum)) return "enum";
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
			bool isFirst = true;
			foreach (var parameter in parameters)
			{
				if (parameter.ParameterModifier != Parameter.Eval)
				{
					if (!isFirst) sb.Append(',');
					ParameterModifierAsString(parameter.ParameterModifier, sb);
					sb.Append(',');
					sb.Append(parameter.Name);
					sb.Append(':');
					WriteParameterType(parameter, sb);
					isFirst = false;
				}
				else
				{
					if (!isFirst) sb.Append(',');
					ParameterModifierAsString(parameter.ParameterModifier, sb);
					sb.Append(',');
					sb.Append(parameter.Name);
					sb.Append(':');
					WriteParameterType(parameter, sb);
					sb.Append(':');
					sb.Append(parameter.EvalScript);
					isFirst = false;
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

		private void WriteParameterType(Parameter parameter, StringBuilder sb)
		{
			Type type = parameter.ParameterType;
			if (type.IsGenericType || type.IsArray)
			{
				WriteSingleParameterType(type.GenericTypeArguments[0], sb);
				sb.Append('[').Append(']');
			}
			else
			{
				WriteSingleParameterType(type, sb);
				// A nullable value-type parameter (declared `int?`, stored normalized to `int`
				// with IsNullable=true) round-trips its nullability via a trailing '?', so that
				// re-parsing reconstructs it as nullable and can accept the '?'/null argument.
				// Reference types are inherently nullable and need no marker.
				if (parameter.IsNullable && type.IsValueType)
					sb.Append('?');
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
			else if (type == typeof(long))
			{
				sb.Append("long");
			}
			else if (type == typeof(char))
			{
				sb.Append("char");
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
			else if (type == typeof(Enum))
			{
				sb.Append("enum");
			}
			else
			{
				throw new LanguageException("Parameter type not valid");
			}
		}

		private ReadOnlySpan<char> StringAsParameterModifier(string parameters, ref int position)
		{
			int initialPosition = position;
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

			if (initialPosition == position)
			{
				throw new LanguageException("Parameter name is not valid");
			}
			return parameters.AsSpan(initialPosition, position - initialPosition);
		}

		private ReadOnlySpan<char> ParameterName(string parameters, ref int position)
		{
			bool isFirst = true;
			int initialPosition = position;
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
				else if (char.IsDigit(currentChar) && !isFirst)
				{
					position++;
				}
				else
				{
					break;
				}

				isFirst = false;
			}

			if (initialPosition == position)
			{
				throw new LanguageException("Parameter name is not valid");
			}
			return parameters.AsSpan(initialPosition, position - initialPosition);
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
			// Only a primitive type keyword is a legal declaration. Anything else is refused by
			// name: a declaration is written against the language's types, never a domain's.
			Type baseType = null;
			if (IsPrimitiveTypeKeyword(parameters, position))
			{
				switch (parameters[position])
				{
					case 's':
					case 'S':
						baseType = StringType(parameters, ref position);
						break;
					case 'i':
					case 'I':
						baseType = IntType(parameters, ref position);
						break;
					case 'l':
					case 'L':
						baseType = LongType(parameters, ref position);
						break;
					case 'c':
					case 'C':
						baseType = CharType(parameters, ref position);
						break;
					case 'b':
					case 'B':
						baseType = BooleanType(parameters, ref position);
						break;
					case 'e':
					case 'E':
						baseType = EnumMarkerType(parameters, ref position);
						break;
					case 'd':
					case 'D':
						if (parameters[position + 1] == 'e' || parameters[position + 1] == 'E')
						{
							baseType = DecimalType(parameters, ref position);
						}
						else if (parameters[position + 1] == 'a' || parameters[position + 1] == 'A')
						{
							baseType = DateTimeType(parameters, ref position);
						}
						else if (parameters[position + 1] == 'o' || parameters[position + 1] == 'O')
						{
							baseType = DoubleType(parameters, ref position);
						}
						break;
				}
			}

			if (baseType == null) RefuseNonPrimitiveDeclaredType(parameters, ref position);

			// A trailing '?' marks a nullable value type (symmetric with WriteParameterType).
			// Re-wrap into Nullable<T> so the Parameter ctor normalizes it back to T with
			// IsNullable=true. Reference types carry no '?' and are unaffected.
			if (position < parameters.Length && parameters[position] == '?')
			{
				position++;
				if (baseType.IsValueType && Nullable.GetUnderlyingType(baseType) == null)
				{
					baseType = typeof(Nullable<>).MakeGenericType(baseType);
				}
			}

			return baseType;
		}

		// True if the type token at `position` is exactly one of the primitives
		// (int/long/string/bool/datetime/decimal/double), case-insensitive. The token is delimited
		// by the first non-alphanumeric character (':' separates name:type; '[' starts the array
		// suffix; ',' separates parameters). Only in that case is the primitive route used.
		private static bool IsPrimitiveTypeKeyword(string parameters, int position)
		{
			int end = position;
			while (end < parameters.Length)
			{
				char c = parameters[end];
				if (char.IsLetterOrDigit(c) || c == '_') end++;
				else break;
			}
			ReadOnlySpan<char> token = parameters.AsSpan(position, end - position);
			return token.Equals("int".AsSpan(), StringComparison.OrdinalIgnoreCase)
				|| token.Equals("long".AsSpan(), StringComparison.OrdinalIgnoreCase)
				|| token.Equals("string".AsSpan(), StringComparison.OrdinalIgnoreCase)
				|| token.Equals("char".AsSpan(), StringComparison.OrdinalIgnoreCase)
				|| token.Equals("bool".AsSpan(), StringComparison.OrdinalIgnoreCase)
				|| token.Equals("datetime".AsSpan(), StringComparison.OrdinalIgnoreCase)
				|| token.Equals("decimal".AsSpan(), StringComparison.OrdinalIgnoreCase)
				|| token.Equals("double".AsSpan(), StringComparison.OrdinalIgnoreCase)
				|| token.Equals("enum".AsSpan(), StringComparison.OrdinalIgnoreCase);
		}

		// A declared type that is not a primitive keyword is refused by NAME, so the message can
		// point at the offending token instead of failing anonymously further along. Reading the
		// identifier first is the whole purpose of this method.
		private static void RefuseNonPrimitiveDeclaredType(string parameters, ref int position)
		{
			int start = position;
			while (position < parameters.Length)
			{
				char c = parameters[position];
				if (char.IsLetterOrDigit(c) || c == '_') position++;
				else break;
			}
			if (start == position) throw new LanguageException($"Unexpected type {parameters.Substring(start)}");
			string typeName = parameters.Substring(start, position - start);

			throw new LanguageException($"Type '{typeName}' is not a valid parameter type. A parameter declaration is written against the language's primitives: int, long, double, decimal, char, string, bool, datetime, or a one-level collection of those. A domain type never appears in a declaration.");
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

		// The symbol marker `enum`. Scalar only: a COLLECTION of symbols has no reading of its own
		// at a call site (each element would have to be resolved against a signature that takes a
		// collection of one specific enum), so the array suffix is deliberately not consumed here
		// and `enum[]` is refused by name like any other non-primitive declaration.
		private Type EnumMarkerType(string parameters, ref int position)
		{
			if (parameters.Length < position + 4)
			{
				throw new LanguageException($"{parameters.Substring(position)} is not a known type");
			}

			bool valid =
				(parameters[position + 0] == 'e' || parameters[position + 0] == 'E') &&
				(parameters[position + 1] == 'n' || parameters[position + 1] == 'N') &&
				(parameters[position + 2] == 'u' || parameters[position + 2] == 'U') &&
				(parameters[position + 3] == 'm' || parameters[position + 3] == 'M');

			if (!valid)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 4 - position)} is not a known type");
			}
			position += 4;

			return typeof(Enum);
		}

		private Type CharType(string parameters, ref int position)
		{
			if (parameters.Length < position + 4)
			{
				throw new LanguageException($"{parameters.Substring(position)} is not a known type");
			}

			bool valid =
				(parameters[position + 0] == 'c' || parameters[position + 0] == 'C') &&
				(parameters[position + 1] == 'h' || parameters[position + 1] == 'H') &&
				(parameters[position + 2] == 'a' || parameters[position + 2] == 'A') &&
				(parameters[position + 3] == 'r' || parameters[position + 3] == 'R');

			if (!valid)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 4 - position)} is not a known type");
			}
			position += 4;

			if (IsArray(parameters, ref position))
			{
				return typeof(char[]);
			}

			return typeof(char);
		}

		private Type LongType(string parameters, ref int position)
		{
			if (parameters.Length < position + 4)
			{
				throw new LanguageException($"{parameters.Substring(position)} is not a known type");
			}

			bool valid =
				(parameters[position + 0] == 'l' || parameters[position + 0] == 'L') &&
				(parameters[position + 1] == 'o' || parameters[position + 1] == 'O') &&
				(parameters[position + 2] == 'n' || parameters[position + 2] == 'N') &&
				(parameters[position + 3] == 'g' || parameters[position + 3] == 'G');

			if (!valid)
			{
				throw new LanguageException($"{parameters.Substring(position, position + 4 - position)} is not a known type");
			}
			position += 4;

			if (IsArray(parameters, ref position))
			{
				return typeof(long[]);
			}

			return typeof(long);
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
			bool isFirst = true;
			foreach (var parameter in parameters)
			{
				// System Now: excluded from the journal's arguments blob (symmetric with
				// LoadArguments). On replay Now is re-injected from OccurredAt.
				if (IsSystemNow(parameter)) continue;

				if (!isFirst) sb.Append(',');
				Type parameterType = parameter.ParameterType;
				if (parameter.ParameterModifier == Parameter.Out)
				{
					// Out is always a placeholder, scalar OR collection: its value is not persisted
					// and is recomputed by re-execution on replay. This must precede the collection
					// check below so a collection-typed Out also writes '?', staying symmetric with
					// LoadArguments (which reads '?' for any Out regardless of type).
					sb.Append('?');
				}
				else if (parameter.GetValue() == null)
				{
					// A null argument (nullable In/InOut) is journaled as '?' and restored to
					// null by LoadArguments; without this it would fall into the primitive writer
					// and unbox (int)null.
					//
					// The marker is only readable back into a NULLABLE slot: LoadArguments assigns
					// null, and Parameter's In/InOut guard rejects null on a non-nullable declared
					// type. Emitting it into a non-nullable slot would therefore write a row the
					// reader cannot accept — and because rehydration is permissive, the act would
					// silently DISAPPEAR from rebuilt state one restart later instead of failing.
					// An empty non-nullable slot at write time means the value never reached this
					// set (an Eval whose computed value was deposited elsewhere is the shape that
					// exposed this), so fail HERE, where the defect is, naming the parameter.
					if (!parameter.IsNullable)
					{
						throw new LanguageException($"Parameter '{parameter.Name}' of type '{parameterType.Name}' has no value; " +
							"a non-nullable argument cannot be journaled as the null placeholder because it could not be read back.");
					}
					sb.Append('?');
				}
				else if (parameterType.IsGenericType || parameterType.IsArray)
				{
					WriteSingleValueCollection(parameter, sb, databaseType);
				}
				else
				{
					WriteSingleValuePrimitive(parameter, sb, databaseType);
				}
				isFirst = false;
			}
			return sb.ToString();
		}

		internal void LoadArguments(string agumentsAsString)
		{
			// A ZERO-argument invocation is journaled as an EMPTY arguments blob, symmetric with
			// ArgumentsAsString: it writes nothing when the list declares no USER parameter (a
			// list holding only system ones writes nothing either, since those are excluded).
			// "No arguments" is therefore a legal ENCODING, not a missing value. Rejecting it
			// here made a record the write path produces unreadable by the read path: every
			// invocation of a parameterless Action failed on replay, and because rehydration is
			// permissive the actor came back MISSING those acts instead of failing loudly.
			if (!HasAnyUserParameter() && string.IsNullOrWhiteSpace(agumentsAsString)) return;

			ArgumentNullException.ThrowIfNullOrWhiteSpace(agumentsAsString);

			int position = 0;
			for (int p = 0; p < parameters.Count; p++)
			{
				var parameter = parameters[p];
				// System Now: excluded from the blob (symmetric with ArgumentsAsString). It does
				// not consume from the string; its value is re-injected from OccurredAt on replay.
				if (IsSystemNow(parameter)) continue;
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
				else if (agumentsAsString[position] == '?')
				{
					// '?' outside an Out slot is the null marker for a nullable In/InOut argument
					// (symmetric with ArgumentsAsString): consume it and restore the null value.
					position++;
					parameter.Value = null;
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
			if (position != agumentsAsString.Length) throw new LanguageException("Parameter definition is not valid");
		}

		private static object DefaultValueForType(Type type)
		{
			// Improvement A: Out parameter defaults served from BoxCache (singletons),
			// instead of boxing a new default(T) on each LoadArguments.
			if (type == typeof(int)) return BoxCache.IntZero;
			if (type == typeof(char)) return default(char);
			if (type == typeof(bool)) return BoxCache.False;
			if (type == typeof(DateTime)) return BoxCache.DateTimeDefault;
			if (type == typeof(decimal)) return BoxCache.DecimalDefault;
			if (type == typeof(double)) return BoxCache.DoubleDefault;
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
			else if (parameterType.GenericTypeArguments[0] == typeof(long))
			{
				parameter.Value = ValueCollectionLong(agumentsAsString, ref position);
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(string))
			{
				parameter.Value = ValueCollectionString(agumentsAsString, ref position);
			}
			else if (parameterType.GenericTypeArguments[0] == typeof(char))
			{
				parameter.Value = ValueCollectionChar(agumentsAsString, ref position);
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

		private object ValueCollectionLong(string agumentsAsString, ref int position)
		{
			List<long> list = new List<long>();
			if (agumentsAsString[position] != '{') throw new LanguageException("Parameter definition is not valid");
			position++;
			int startPosition = position;
			if (agumentsAsString[position] == '}')
			{
				position++;
				return Enumerable.Empty<long>();
			}

			while (position < agumentsAsString.Length)
			{
				if (agumentsAsString[position] == ',')
				{
					list.Add(long.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(long.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
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

			// B1: range comprehension {start..end} (see docs/rfc/foreach-range-literal.md).
			// A '..' before the first ',' or '}' selects the range form; a '.' can only be
			// the range operator here because this blob holds integers (never doubles).
			int scan = position;
			bool isRange = false;
			while (scan < agumentsAsString.Length && agumentsAsString[scan] != ',' && agumentsAsString[scan] != '}')
			{
				if (agumentsAsString[scan] == '.' && scan + 1 < agumentsAsString.Length && agumentsAsString[scan + 1] == '.')
				{
					isRange = true;
					break;
				}
				scan++;
			}
			if (isRange)
			{
				int rangeStart = int.Parse(agumentsAsString.AsSpan(position, scan - position), CultureInfo.InvariantCulture);
				position = scan + 2; // skip the '..'
				int endStart = position;
				while (position < agumentsAsString.Length && agumentsAsString[position] != '}') position++;
				if (position >= agumentsAsString.Length) throw new LanguageException("Parameter definition is not valid");
				int rangeEnd = int.Parse(agumentsAsString.AsSpan(endStart, position - endStart), CultureInfo.InvariantCulture);
				position++; // skip the '}'
				return new IntRangeList(rangeStart, rangeEnd);
			}

			while (position < agumentsAsString.Length)
			{
				if (agumentsAsString[position] == ',')
				{
					list.Add(int.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(int.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
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

		// Delegates to the string reader so the quoting rules cannot drift between the two: on
		// the wire a char collection IS a collection of one-character quoted literals, narrowed
		// here. A member of any other length means a corrupt blob, not a surprising value.
		private object ValueCollectionChar(string agumentsAsString, ref int position)
		{
			object asStrings = ValueCollectionString(agumentsAsString, ref position);
			List<char> list = new List<char>();
			foreach (string written in (IEnumerable<string>)asStrings)
			{
				if (written.Length != 1) throw new LanguageException("Parameter definition is not valid");
				list.Add(written[0]);
			}
			return list;
		}

		private object ValueCollectionString(string agumentsAsString, ref int position)
		{
			List<string> list = new List<string>();
			bool isFirstLetter = true;
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
				if (isFirstLetter)
				{
					if (agumentsAsString[position] != '\'') throw new LanguageException("Parameter definition is not valid");
					isFirstLetter = false;
				}
				else if (agumentsAsString[position] == ',' && agumentsAsString[position - 1] == '\'')
				{
					list.Add(agumentsAsString.AsSpan(startPosition + 1, position - startPosition - 2).ToString());
					startPosition = position + 1;
					isFirstLetter = true;
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
					list.Add(decimal.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(decimal.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
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
					list.Add(double.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(double.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
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
					list.Add(DateTime.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
					startPosition = position + 1;
				}
				else if (agumentsAsString[position] == '}')
				{
					list.Add(DateTime.Parse(agumentsAsString.AsSpan(startPosition, position - startPosition), CultureInfo.InvariantCulture));
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

			// Improvement B: SetParsedScalar avoids ImplicitCast (the value already arrives with the
			// exact type). Improvement A: int/bool take the box from BoxCache when cacheable.
			// Symmetric with the writer: the symbol marker reads back through the string reader.
			if (parameterType == typeof(string) || parameterType == typeof(Enum))
			{
				parameter.SetParsedScalar(ValueString(agumentsAsString, ref position).ToString());
			}
			else if (parameterType == typeof(char))
			{
				// Symmetric with the writer: read the quoted literal and take its single
				// character. A blob that carries anything other than one character at a char
				// position is corrupt, not merely surprising, so it fails loudly.
				ReadOnlySpan<char> written = ValueString(agumentsAsString, ref position);
				if (written.Length != 1) throw new LanguageException("Parameter definition is not valid");
				parameter.SetParsedScalar(written[0]);
			}
			else if (parameterType == typeof(int))
			{
				parameter.SetParsedScalar(BoxCache.Box(int.Parse(Value(agumentsAsString, ref position), CultureInfo.InvariantCulture)));
			}
			else if (parameterType == typeof(long))
			{
				parameter.SetParsedScalar(long.Parse(Value(agumentsAsString, ref position), CultureInfo.InvariantCulture));
			}
			else if (parameterType == typeof(bool))
			{
				parameter.SetParsedScalar(BoxCache.Box(bool.Parse(Value(agumentsAsString, ref position))));
			}
			else if (parameterType == typeof(DateTime))
			{
				parameter.SetParsedScalar(DateTime.Parse(Value(agumentsAsString, ref position), CultureInfo.InvariantCulture));
			}
			else if (parameterType == typeof(decimal))
			{
				parameter.SetParsedScalar(Decimal.Parse(Value(agumentsAsString, ref position), CultureInfo.InvariantCulture));
			}
			else if (parameterType == typeof(double))
			{
				parameter.SetParsedScalar(Double.Parse(Value(agumentsAsString, ref position), CultureInfo.InvariantCulture));
			}
			else
			{
				throw new LanguageException("invalid type");
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
			int stringStart = position;
			while (position < agumentsAsString.Length)
			{
				if (agumentsAsString[position] == '\'' && agumentsAsString.Length == position + 1) break;
				if ((agumentsAsString[position] == '\'' && agumentsAsString[position + 1] == ',')) break;
				position++;
			}
			int stringEnd = position;
			if (agumentsAsString[position++] != '\'') throw new LanguageException("Parameter definition is not valid");
			return agumentsAsString.AsSpan(stringStart, stringEnd - stringStart);
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
			else if (parameterType.GenericTypeArguments[0] == typeof(long))
			{
				if (parameterType == typeof(List<long>))
				{
					Append((List<long>)value, sb);
				}
				else if (parameterType == typeof(IEnumerable<long>))
				{
					Append((IEnumerable<long>)value, sb);
				}
				else
				{
					Append((long[])value, sb);
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
			else if (parameterType.GenericTypeArguments[0] == typeof(char))
			{
				// One overload covers all three shapes: List<char> and char[] both ARE an
				// IEnumerable<char>, and the element writer is identical for each, so there is
				// nothing for a per-shape overload to decide.
				Append((IEnumerable<char>)value, sb, databaseType);
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
				throw new LanguageException("invalid type");
			}
		}

		private void WriteSingleValuePrimitive(Parameter parameter, StringBuilder sb, DatabaseType databaseType)
		{
			Type parameterType = parameter.ParameterType;
			// The symbol marker rides the string wire: its value IS a member name. Reusing the
			// string writer rather than adding a branch keeps the quoting rules from drifting
			// between them, and the declared type at this position is what tells the reader which
			// of the two it is looking at.
			if (parameterType == typeof(string) || parameterType == typeof(Enum))
			{
				Append((string)parameter.GetValue(), sb, databaseType);
			}
			else if (parameterType == typeof(char))
			{
				// A char is journaled as a one-character quoted literal ('L'), exactly like a
				// string, so the backend's escaping covers a value that is itself a quote, a
				// comma or a brace — any of which would otherwise break the blob. No suffix is
				// needed to tell it apart from a string (as `1L` does for long): the blob is
				// positional and the declared type at that position says which one to read.
				Append(((char)parameter.GetValue()).ToString(), sb, databaseType);
			}
			else if (parameterType == typeof(int))
			{
				Append((int)parameter.GetValue(), sb);
			}
			else if (parameterType == typeof(long))
			{
				Append((long)parameter.GetValue(), sb);
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
				throw new LanguageException("invalid type");
			}
		}

		private void Append(double[] values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value.ToString(CultureInfo.InvariantCulture));
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(DateTime[] values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				if (value.Hour == 00 && value.Minute == 00 && value.Second == 00)
					sb.Append(value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
				else
					sb.Append(value.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(decimal[] values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value.ToString("0.######################", CultureInfo.InvariantCulture));
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(bool[] values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(int[] values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(long[] values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(List<long> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<long> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(string[] values, StringBuilder sb, DatabaseType databaseType)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				LiteralString.Write(sb, value, databaseType);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(List<double> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value.ToString(CultureInfo.InvariantCulture));
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(List<DateTime> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				if (value.Hour == 00 && value.Minute == 00 && value.Second == 00)
					sb.Append(value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
				else
					sb.Append(value.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(List<decimal> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value.ToString("0.######################", CultureInfo.InvariantCulture));
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(List<bool> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(List<int> values, StringBuilder sb)
		{
			if (TryAppendIntRange(values, sb)) return;
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value);
				isFirst = false;
			}
			sb.Append('}');
		}

		// B1 (see docs/rfc/foreach-range-literal.md): an integer collection that is an
		// IntRangeList — produced by a {start..end} range literal — serializes by
		// comprehension so the journal stays O(1) in the range length instead of
		// enumerating every element. Any other integer collection enumerates as before;
		// runs are never inferred by scanning an arbitrary value (that is the dropped B2).
		private static bool TryAppendIntRange(IEnumerable<int> values, StringBuilder sb)
		{
			if (values is IntRangeList range && range.StillDescribesBounds())
			{
				sb.Append('{').Append(range.Start).Append("..").Append(range.End).Append('}');
				return true;
			}
			return false;
		}

		private void Append(IEnumerable<char> values, StringBuilder sb, DatabaseType databaseType)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				LiteralString.Write(sb, value.ToString(), databaseType);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(List<string> values, StringBuilder sb, DatabaseType databaseType)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				LiteralString.Write(sb, value, databaseType);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<double> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value.ToString(CultureInfo.InvariantCulture));
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<DateTime> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				if (value.Hour == 00 && value.Minute == 00 && value.Second == 00)
					sb.Append(value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
				else
					sb.Append(value.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<decimal> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value.ToString("0.######################", CultureInfo.InvariantCulture));
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<bool> values, StringBuilder sb)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<int> values, StringBuilder sb)
		{
			if (TryAppendIntRange(values, sb)) return;
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				sb.Append(value);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(IEnumerable<string> values, StringBuilder sb, DatabaseType databaseType)
		{
			var isFirst = true;
			sb.Append('{');
			foreach (var value in values)
			{
				if (!isFirst) sb.Append(',');
				LiteralString.Write(sb, value, databaseType);
				isFirst = false;
			}
			sb.Append('}');
		}

		private void Append(double value, StringBuilder sb)
		{
			sb.Append(value.ToString(CultureInfo.InvariantCulture));
		}

		private void Append(decimal value, StringBuilder sb)
		{
			sb.Append(value.ToString("0.######################", CultureInfo.InvariantCulture));
		}

		private void Append(DateTime value, StringBuilder sb)
		{
			if (value.Hour == 00 && value.Minute == 00 && value.Second == 00)
				sb.Append(value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
			else
				sb.Append(value.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture));
		}

		private void Append(bool value, StringBuilder sb)
		{
			sb.Append(value);
		}

		private void Append(int value, StringBuilder sb)
		{
			sb.Append(value);
		}

		private void Append(long value, StringBuilder sb)
		{
			sb.Append(value);
		}

		private void Append(string value, StringBuilder sb, DatabaseType databaseType)
		{
			LiteralString.Write(sb, value, databaseType);
		}

		// Paper 5 Lab 1: public entrypoint so the V2 fluent API can declare USER
		// parameters from C#. Triggers the JournalEntry.IsNewAction /
		// IsExistingAction persistence path (compact ActionEventData entries).
		// Lab-policy visibility bump — the live indexer that took this role was
		// internal-only.
		public void UserParameter<T>(string parameterName, T value)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			ArgumentNullException.ThrowIfNull(parameterName);
			if (value != null && !(value is T))
				throw new ArgumentException($"Value is not of type {typeof(T).FullName}");

			// Improvement (d): de-box int/bool via BoxCache before crossing to object, avoiding
			// a new box per call on the preparation path. Other T box normally.
			SetParameter(BoxCache.Box(value), Parameter.In, parameterName, typeof(T));
		}

		// Playbill final refactor: after removing SystemParameter (Ip/User out of the
		// journal in Phase 1; Now converted into an explicit user parameter in
		// Phase 4.5+), every present parameter counts as "user". Previously "Now"
		// was excluded from this count via _hasUserParameter; that no longer applies.
		internal bool HasAnyParameter()
		{
			return parameters.Count > 0;
		}

		// Counts only USER parameters (excludes the system Now). The
		// IsScript/IsNewAction classification must be based on this: the framework injects Now on every
		// Perform, and in the check-then-command flow Phase 1 (PerformChk) may already have
		// injected it into the same Parameters before Phase 2 decides; without this
		// exclusion a command without user parameters would be wrongly classified as
		// Action by the mere presence of Now.
		internal bool HasAnyUserParameter()
		{
			foreach (var parameter in parameters)
			{
				if (IsSystemNow(parameter)) continue;
				return true;
			}
			return false;
		}

		public string SerializeForTransport(DatabaseType databaseType)
		{
			if (databaseType < 0) throw new ArgumentOutOfRangeException(nameof(databaseType));
			if (!HasAnyParameter()) return string.Empty;

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

		// Dense transport: serialize ONLY the argument values, dropping the
		// declarations. Callers that already hold the declarations out-of-band
		// (e.g. a schema registered once) pair this with the two-argument
		// DeserializeFromTransport overload to rebuild the full Parameters. This
		// is the value-only half of SerializeForTransport.
		public string SerializeArgumentsForTransport(DatabaseType databaseType)
		{
			if (databaseType < 0) throw new ArgumentOutOfRangeException(nameof(databaseType));
			if (!HasAnyParameter()) return string.Empty;

			return ArgumentsAsString(databaseType);
		}

		// Rebuilds a Parameters from declarations supplied out-of-band and a
		// value-only argument string (the output of SerializeArgumentsForTransport).
		// Mirrors the single-argument overload but takes the declarations
		// separately instead of splitting them off a combined blob.
		public static Parameters DeserializeFromTransport(string declarations, string arguments)
		{
			if (string.IsNullOrEmpty(declarations)) return null;

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

			if (this.parameters.Count != other.parameters.Count)
				return false;

			for (int i = 0; i < this.parameters.Count; i++)
			{
				var thisParam = this.parameters[i];
				var otherParam = other.parameters[i];

				if (!string.Equals(thisParam.Name, otherParam.Name, StringComparison.OrdinalIgnoreCase))
					return false;

				if (thisParam.ParameterType != otherParam.ParameterType)
					return false;

				if (thisParam.ParameterModifier != otherParam.ParameterModifier)
					return false;
			}

			return true;
		}

	}

}
