using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Interpreter.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	abstract class AstExpression : AST
	{

		protected bool coercesToString = false;

		internal bool CoercesToString
		{
			get
			{
				return coercesToString;
			}
			set
			{
				coercesToString = value;
			}
		}

		internal abstract object Execute();

		internal abstract Expression ExecuteExpression(ParameterExpression parametersParam);

		internal abstract Type ComputeType();

		private void PropagateTypes()
		{
			if (this.ForcedType == null && this.ComputeType() != null) this.ForcedType = this.ComputeType();
		}

		internal virtual void ValidateStatically()
		{
			PropagateTypes();
		}

		internal abstract void write(StringBuilder result, DatabaseType databaseType);

		private Type forcedType;

		internal virtual Type ForcedType
		{
			set
			{
				forcedType = value;
			}
			get
			{
				return forcedType;
			}
		}

		// --- Symbol->enum coercion support -------------------------------------------------
		// An argument can bind to an enum parameter in two ways:
		//   Symbol      : a bare identifier without scope (foo(SomeMember)). It has no value at
		//                 runtime — its NAME is the enum member. ComputeType() == object/null
		//                 and it cannot be Execute()/ExecuteExpression()'d (undefined scope).
		//   StringValue : a string with a value (string parameter @x, string variable, or literal
		//                 'SomeMember'). Its runtime VALUE is the member name. ComputeType()==string.
		// An OpCast (including (string)x) is NOT enum-bindable by this path: it respects the declared
		// type of the cast — that gives the disambiguation opt-out when foo(SomeEnum) and foo(string) coexist.
		internal enum EnumArgKind { NotEnumBindable, Symbol, StringValue }

		internal static EnumArgKind ClassifyEnumArg(AstExpression arg)
		{
			if (arg == null) return EnumArgKind.NotEnumBindable;
			if (arg is LiteralString) return EnumArgKind.StringValue;
			if (arg is Id id)
			{
				Type t = id.ComputeType();
				if (t == null || t == typeof(object)) return EnumArgKind.Symbol;
				if (t == typeof(string)) return EnumArgKind.StringValue;
				// The symbol marker (`typeof(Enum)` at the parameter boundary): the value is a
				// member name, exactly like a string, and it says so at the border instead of
				// leaving the call site to guess. It is enum-bindable for the same reason a string
				// is, and it is NOT compatible with a parameter of any other type — so where a
				// string would be ambiguous between two readings, this has only one and needs no
				// cast to disambiguate. Reading it as text is then the case that must be written
				// explicitly, by calling ToString() on it.
				if (t == typeof(Enum)) return EnumArgKind.StringValue;
			}
			return EnumArgKind.NotEnumBindable;
		}

		internal static bool EnumNameExists(Type enumType, string name)
		{
			if (name == null) return false;
			foreach (string n in Enum.GetNames(enumType))
			{
				if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		internal static LanguageException EnumValueUnknown(Type enumType, string written)
		{
			string options = string.Join(" or ", Enum.GetNames(enumType));
			return new LanguageException(string.Format("You are trying to assign a value of type '{0}' but wrote '{1}'; the expected value is one of: {2}.", enumType.Name, written, options));
		}

		// Called at runtime (interpreted and compiled) to convert the symbolic name to the
		// enum value. Throws LanguageException (not ArgumentException) on an invalid name.
		internal static object ParseEnumOrThrow(Type enumType, string name)
		{
			if (!Enum.TryParse(enumType, name, true, out object value))
			{
				throw EnumValueUnknown(enumType, name);
			}
			return value;
		}

		// Static compatibility of an argument against an enum parameter.
		// Symbol/LiteralString: the name/value is known at parse -> it must exist in the enum.
		// StringValue via Id (parameter/variable): the value is runtime -> compatible, deferred.
		internal static bool IsEnumArgCompatible(Type enumType, AstExpression arg)
		{
			switch (ClassifyEnumArg(arg))
			{
				case EnumArgKind.Symbol:
					return EnumNameExists(enumType, ((Id)arg).Name);
				case EnumArgKind.StringValue:
					if (arg is LiteralString)
					{
						return EnumNameExists(enumType, (string)arg.Execute());
					}
					return true;
				default:
					return false;
			}
		}

		// Extracts the observed argument VALUES of a call for pattern matching, shared by the
		// method-call path (DotAccess) and the constructor path (NewInstance) so both record the
		// same thing. For each argument: a LITERAL yields its value; an @PARAMETER yields the value
		// from the invocation channel (the journaled Arguments the Reaction hangs on the symbol
		// table) or, failing that, its interpreted Execute(); an OUT parameter yields an
		// OutParameterMarker; any non-bindable (a bare global/local variable, a declared parameter
		// whose value failed to resolve, or a derived expression) yields a TypedValuePlaceholder so
		// the argument COUNT is preserved and the capture contract can classify it. Value-free
		// (types only) is NOT enough — a $-capture needs the value.
		internal static List<object> ExtractArgumentValues(AstExpression[] arguments)
		{
			List<object> values = new List<object>();
			if (arguments == null) return values;

			foreach (var arg in arguments)
			{
				if (arg is LiteralBoolean || arg is LiteralDecimal || arg is LiteralDouble || arg is LiteralDateTime || arg is LiteralString || arg is LiteralList || arg is LiteralNull || arg is LiteralNumber)
				{
					values.Add(arg.Execute());
				}
				else if (arg is Id id)
				{
					if (id.IsParameter)
					{
						if (id.Parameter != null && id.Parameter.ParameterModifier == Parameter.Out)
						{
							values.Add(new OutParameterMarker(arg.ComputeType(), id.Name));
						}
						else
						{
							// Prefer the value from the INVOCATION (journaled Arguments exposed on the
							// symbol table by the Reaction) so it does not depend on the program's
							// per-instance parameter binding; fall back to Execute() outside a reaction.
							Parameters invocation = id.SymbolTable?.CurrentActionArguments;
							if (invocation != null && invocation.ContainsParameter(id.Name))
								values.Add(invocation[id.Name]?.GetValue());
							else
								values.Add(arg.Execute());
						}
					}
					else
					{
						// Only the type is known — placeholder. Distinguish a declared parameter whose
						// value failed to resolve (graceful no-match) from a genuine variable (hard
						// authoring error), per the capture contract.
						bool isDeclaredParameter = id.IsParameter
							|| (id.SymbolTable?.CurrentActionParameterNames?.Contains(id.Name) ?? false);
						values.Add(new TypedValuePlaceholder(arg.ComputeType(), id.Name, isDeclaredParameter));
					}
				}
				else
				{
					// Any other expression (nested call, chained access): not statically evaluable.
					// Preserve the count with a typed placeholder.
					values.Add(new TypedValuePlaceholder(arg.ComputeType()));
				}
			}

			return values;
		}

		// Enum value (boxed) for interpreted mode.
		internal static object ParseEnumArgValue(Type enumType, AstExpression arg)
		{
			switch (ClassifyEnumArg(arg))
			{
				case EnumArgKind.Symbol:
					return ParseEnumOrThrow(enumType, ((Id)arg).Name);
				case EnumArgKind.StringValue:
					return ParseEnumOrThrow(enumType, (string)arg.Execute());
				default:
					throw new LanguageException($"Argument cannot be bound to enum '{enumType.Name}'.");
			}
		}

		// Expression that produces the enum value for compiled mode.
		internal static Expression ParseEnumArgExpression(Type enumType, AstExpression arg, ParameterExpression parametersParam)
		{
			Expression nameExpr;
			switch (ClassifyEnumArg(arg))
			{
				case EnumArgKind.Symbol:
					nameExpr = Expression.Constant(((Id)arg).Name, typeof(string));
					break;
				case EnumArgKind.StringValue:
					nameExpr = arg.ExecuteExpression(parametersParam);
					// A StringValue argument CARRIES a member name but is not always TYPED as a
					// string: the symbol marker (`typeof(Enum)` at the parameter boundary) is
					// declared as the abstract base while its slot stores the name. The declared
					// type is what the expression gets typed with, so it has to be brought back to
					// string here or the call below is built against the wrong signature. Routed
					// through object because Enum -> string is not a CLR conversion, while the value
					// in the slot has been a string since it crossed the boundary.
					if (nameExpr.Type != typeof(string))
					{
						nameExpr = Expression.Convert(Expression.Convert(nameExpr, typeof(object)), typeof(string));
					}
					break;
				default:
					throw new LanguageException($"Argument cannot be bound to enum '{enumType.Name}'.");
			}

			var parseMethod = typeof(AstExpression).GetMethod(
				nameof(ParseEnumOrThrow),
				BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
				null,
				new[] { typeof(Type), typeof(string) },
				null);
			Expression parseCall = Expression.Call(
				parseMethod,
				Expression.Constant(enumType, typeof(Type)),
				nameExpr);
			return Expression.Convert(parseCall, enumType);
		}

		protected static object[] BindValuesToParameters(ParameterInfo[] methodSignature, object[] arguments)
		{
			object[] result = new object[arguments == null ? 0 : arguments.Length];
			for (int i = 0; i < methodSignature.Length; i++)
			{
				ParameterInfo parameterInfo = methodSignature[i];
				// arguments[i] may be an already-evaluated value (method-invocation path, which
				// pre-resolves enums) or a raw AST node (property-get path). We only coerce
				// when it is still an enum-bindable node (bare symbol or string with a value).
				if (parameterInfo.ParameterType.IsEnum && arguments[i] is AstExpression enumArgNode
					&& ClassifyEnumArg(enumArgNode) != EnumArgKind.NotEnumBindable)
				{
					result[i] = ParseEnumArgValue(parameterInfo.ParameterType, enumArgNode);
				}
				else
				{
					var paramType = methodSignature[i].ParameterType;
					object evaluatedArgument = arguments[i];

					Type parameterType = parameterInfo.ParameterType;

					if (NewInstance.TypeOfCollection(paramType) == typeof(List<>) || NewInstance.TypeOfCollection(paramType) == typeof(IEnumerable<>))
					{
						if (paramType.GetGenericArguments().Length == 1)
						{
							result[i] = TypeConversion.ImplicitCast(evaluatedArgument, paramType);
						}

					}
					else if (NewInstance.TypeOfCollection(paramType).IsArray)
					{
						result[i] = TypeConversion.ImplicitCast(evaluatedArgument, paramType);
					}
					else
					{
						result[i] = CoerceScalarValue(evaluatedArgument, parameterType);
					}
				}
			}

			return result;
		}
		protected static Expression[] BindValueExpressionsToParameters(ParameterInfo[] methodSignature, Expression[] arguments)
		{
			Expression[] result = new Expression[arguments == null ? 0 : arguments.Length];
			for (int i = 0; i < methodSignature.Length; i++)
			{
				ParameterInfo parameterInfo = methodSignature[i];
				var paramType = parameterInfo.ParameterType;
				Expression argument = arguments[i];

				// Enum handling: an argument that is a Constant wrapping a bare-symbol Id.
				// (The string-with-value ones were already resolved to an enum-typed Expression upstream.)
				if (paramType.IsEnum && argument is ConstantExpression constExpr && constExpr.Value is Id idArg)
				{
					result[i] = Expression.Constant(ParseEnumOrThrow(paramType, idArg.Name), paramType);
				}
				// Collection handling.
				else if (NewInstance.TypeOfCollection(paramType) == typeof(List<>) ||
						 NewInstance.TypeOfCollection(paramType) == typeof(IEnumerable<>))
				{
					if (paramType.GetGenericArguments().Length == 1)
					{
						// Ensure we use a closed generic type for Expression.Convert.
						var closedType = paramType.IsGenericTypeDefinition
							? paramType.MakeGenericType(argument.Type.GetGenericArguments())
							: paramType;

						// Only perform Expression.Convert if closedType does not contain generic parameters.
						if (!closedType.ContainsGenericParameters)
						{
							result[i] = Expression.Convert(
								Expression.Call(
									typeof(TypeConversion).GetMethod(
										nameof(TypeConversion.ImplicitCast),
										BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
									),
									argument,
									Expression.Constant(closedType, typeof(Type))
								),
								closedType
							);
						}
						else
						{
							// If still open generic, just pass the argument as is (no conversion).
							result[i] = argument;
						}
					}
				}
				else if (NewInstance.TypeOfCollection(paramType).IsArray)
				{
					result[i] = Expression.Convert(
						Expression.Call(
							typeof(TypeConversion).GetMethod(nameof(TypeConversion.ImplicitCast), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
							argument,
							Expression.Constant(paramType, typeof(Type))
						),
						paramType
					);
				}
				// Numeric conversions.
				else
				{
					result[i] = CoerceScalarExpression(argument, paramType);
				}
			}
			return result;
		}

		// Scalar coercion of ONE already-evaluated argument (interpreted mode). It is the same
		// numeric ladder that BindValuesToParameters applied in its scalar branch; extracted so it
		// can be reused per-element when populating a params array. enum/collection are handled separately.
		protected static object CoerceScalarValue(object evaluatedArgument, Type parameterType)
		{
			if (evaluatedArgument == null) return null;

			Type argumentType = evaluatedArgument.GetType();

			if (parameterType == typeof(long) && argumentType == typeof(int))
			{
				return Convert.ToInt64(evaluatedArgument);
			}
			else if (parameterType == typeof(double) && argumentType == typeof(int))
			{
				return Convert.ToDouble(evaluatedArgument);
			}
			else if (parameterType == typeof(double) && argumentType == typeof(decimal))
			{
				return Convert.ToDouble(evaluatedArgument);
			}
			else if (parameterType == typeof(decimal) && argumentType == typeof(int))
			{
				return Convert.ToDecimal(evaluatedArgument);
			}
			else if (parameterType == typeof(decimal) && argumentType == typeof(double))
			{
				return Convert.ToDecimal(evaluatedArgument);
			}
			// No string -> char branch either. A string is a sequence and a char is one of its
			// positions, so there is no conversion between them to apply — the author names the
			// position: text[0]. A length-1 string used to be accepted here, which made the rule
			// depend on the VALUE rather than the type, so the same script was legal or not according
			// to what happened to be in the string at that moment.
			// No char -> string branch: a char argument no longer reaches a string parameter
			// implicitly, so there is nothing to coerce here. An explicit (string)x is an OpCast and
			// arrives already typed.

			return evaluatedArgument;
		}

		// Scalar coercion of ONE argument (compiled mode), producing the Expression with the
		// target type. Same numeric ladder + ImplicitCast/boxing fallback that the scalar branch
		// of BindValueExpressionsToParameters had; extracted to reuse it per-element when
		// emitting Expression.NewArrayInit of a params parameter.
		protected static Expression CoerceScalarExpression(Expression argument, Type parameterType)
		{
			if (argument == null) throw new ArgumentNullException(nameof(argument));
			if (parameterType == null) throw new ArgumentNullException(nameof(parameterType));

			// A null constant (a `null` literal) binds to a reference type or Nullable<T> without
			// coercion. Routing it through ImplicitCast would dereference the null value
			// (value.GetType()) at runtime, so emit the typed null constant. Mirrors the null
			// short-circuit of CoerceScalarValue in the interpreted path.
			if (argument is ConstantExpression nullConstant && nullConstant.Value == null
				&& (!parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) != null))
			{
				return Expression.Constant(null, parameterType);
			}

			Type argumentType = argument.Type;

			if (parameterType == typeof(long) && argumentType == typeof(int))
			{
				return Expression.Convert(argument, typeof(long));
			}
			else if (parameterType == typeof(double) && argumentType == typeof(int))
			{
				return Expression.Convert(argument, typeof(double));
			}
			else if (parameterType == typeof(double) && argumentType == typeof(decimal))
			{
				return Expression.Convert(argument, typeof(double));
			}
			else if (parameterType == typeof(decimal) && argumentType == typeof(int))
			{
				return Expression.Convert(argument, typeof(decimal));
			}
			else if (parameterType == typeof(decimal) && argumentType == typeof(double))
			{
				return Expression.Convert(argument, typeof(decimal));
			}

			// Neither direction between string and char is emitted here: the promotion is gone and so
			// is the length-1 coercion, so the compiled path has nothing to emit and stays in step
			// with the interpreted one.

			// If types are not directly assignable, use ImplicitCast.
			if (!AreCompatible(argumentType, parameterType))
			{
				return Expression.Convert(
					Expression.Call(
						typeof(TypeConversion).GetMethod(
							nameof(TypeConversion.ImplicitCast), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
						argument,
						Expression.Constant(parameterType, typeof(Type))
					),
					parameterType
				);
			}
			else if (parameterType != argumentType)
			{
				// Boxing / reference upcast. Expression.Call requires the argument
				// expression type to be reference-assignable to the parameter type.
				// A value type (decimal, bool, int, ...) is NOT reference-assignable
				// to object/an interface without an explicit boxing conversion, so when
				// a value-type argument lands on a parameter typed as object (e.g.
				// AttributeOptions.Set(string, object)) we must emit it here; otherwise
				// Expression.Call throws "Expression of type 'System.Decimal' cannot be
				// used for parameter of type 'System.Object'". Interpreted mode never
				// hits this because every runtime value is already boxed as object.
				return Expression.Convert(argument, parameterType);
			}

			return argument;
		}

		// A parameter is params (C# params T[]) if it carries ParamArrayAttribute. It is always the last
		// parameter and its type is a one-dimensional array.
		internal static bool IsParamsParameter(ParameterInfo parameter)
		{
			if (parameter == null) throw new ArgumentNullException(nameof(parameter));
			return parameter.IsDefined(typeof(ParamArrayAttribute), false) && parameter.ParameterType.IsArray;
		}

		internal static bool AreCompatible(Type argType, Type paramType)
		{
			bool compatible = false;

			if (argType == paramType)
			{
				compatible = true;
			}
			else if (paramType == typeof(int))
			{
				compatible = argType == typeof(int);
			}
			else if (paramType == typeof(long))
			{
				compatible = argType == typeof(long) || argType == typeof(int);
			}
			else if (paramType == typeof(bool))
			{
				compatible = argType == typeof(bool);
			}
			else if (paramType == typeof(double) || paramType == typeof(decimal))
			{
				compatible = argType == typeof(int) || argType == typeof(double) || argType == typeof(decimal);
			}
			else if (paramType == typeof(string))
			{
				// A char does NOT satisfy a string parameter. The promotion existed to spare the
				// author a conversion when a value is naturally one letter, back when the language
				// could not WRITE a char at all — so every char reaching a call site had to arrive as
				// a one-character string, or through this promotion. The `'L'c` literal replaced that
				// need, and the promotion had a cost the ergonomics did not pay for: a char argument
				// bound a string parameter NON-EXACTLY, so adding a char overload later took the
				// binding away from entries already in the journal, silently. What a promotion cannot
				// be is undone once it is written down.
				//
				// A char that is genuinely meant as text says so by calling ToString() on it. There is
				// no cast to string in this language, for the reason OpCast.ExplicitCast gives: text is
				// produced by an operation that names its format, not by a conversion.
				compatible = argType == typeof(string);
			}
			else if (paramType == typeof(char))
			{
				// A char parameter accepts a char. A length-1 string used to satisfy it, which made
				// compatibility depend on the VALUE and not the type: the same script bound or failed
				// according to what the string happened to hold. A string is a sequence and a char is
				// one of its positions, so the author names the position — text[0], which already
				// yields a char.
				compatible = argType == typeof(char);
			}
			else if (paramType == typeof(DateTime))
			{
				compatible = argType == typeof(DateTime);
			}
			else if (TypeOfCollection(paramType) == typeof(List<>) || TypeOfCollection(paramType) == typeof(IEnumerable<>) || TypeOfCollection(paramType).IsArray)
			{
				compatible = false;
				if (paramType.ContainsGenericParameters)
				{
					if (paramType.GetGenericArguments().Length == 1)
					{
						compatible = argType.GetGenericArguments()[0] == paramType.GetGenericArguments()[0];
						if (compatible)
						{
							var argGenericTypeDef = TypeOfCollection(argType);
							var paramGenericTypeDef = TypeOfCollection(paramType);

							compatible =
						   (
							   argGenericTypeDef == typeof(IEnumerable<>) ||
							   argGenericTypeDef == typeof(List<>) ||
							   argGenericTypeDef.IsArray
							)
							&&
							(
							   paramGenericTypeDef == typeof(IEnumerable<>) ||
							   paramGenericTypeDef == typeof(List<>) ||
							   paramGenericTypeDef.IsArray
							);
						}
					}
				}
				else
				{
					var argElementType = TypeOfCollectionElement(argType);
					var paramElementType = TypeOfCollectionElement(paramType);
					compatible = AreCompatible(argElementType, paramElementType);
					if (compatible)
					{
						var argGenericTypeDef = TypeOfCollection(argType);
						var paramGenericTypeDef = TypeOfCollection(paramType);

						compatible =
							(
								argGenericTypeDef == typeof(IEnumerable<>) ||
								argGenericTypeDef == typeof(List<>) ||
								argGenericTypeDef.IsArray
							 )
							 &&
							 (
								paramGenericTypeDef == typeof(IEnumerable<>) ||
								paramGenericTypeDef == typeof(List<>) ||
								paramGenericTypeDef.IsArray
							 );
					}
				}
			}
			else
			{
				compatible = argType == paramType || paramType.IsAssignableFrom(argType);
			}

			return compatible;
		}

		internal static Type TypeOfCollection(Type collection)
		{
			if (collection != null)
			{
				if (collection.IsArray) return collection;
				if (collection.IsGenericType)
				{
					var genericDef = collection.GetGenericTypeDefinition();
					if (genericDef == typeof(List<>)) return genericDef;
					if (genericDef == typeof(IEnumerable<>)) return genericDef;
				}
			}
			return typeof(object);
		}

		internal static Type TypeOfCollectionElement(Type collection)
		{
			if (collection.IsArray)
			{
				return collection.GetElementType();
			}
			else if (collection.IsGenericType)
			{
				return collection.GetGenericArguments()[0];
			}
			else
			{
				return null;
			}
		}

		protected Type PromotesTo(Type leftType, Type rightType)
		{
			Type promoted = PromotedNumericType(leftType, rightType);
			if (promoted == null)
				throw new LanguageException($"Binary operation {this.GetType().Name} cannot combine {leftType.Name} and {rightType.Name}");

			return promoted;
		}

		// The numeric promotion ladder: decimal dominates double dominates long dominates int.
		// int widens to long (safe); long never narrows to int implicitly. Returns null when
		// either type is off the ladder, leaving the decision to the caller.
		//
		// Exposed as a static because the ladder is the ordering of the numeric family, not a
		// rule of any single operator: the operators use it to promote their operands, and an
		// assignment uses it to decide whether the value fits the storage. The family has no
		// usable ordering in the type hierarchy — the common ancestor of two numeric structs
		// is System.ValueType, which carries no numeric semantics — so the ladder is the only
		// place that ordering exists.
		internal static Type PromotedNumericType(Type leftType, Type rightType)
		{
			if (!IsPromotableNumeric(leftType) || !IsPromotableNumeric(rightType))
				return null;

			if (leftType == typeof(decimal) || rightType == typeof(decimal))
				return typeof(decimal);
			if (leftType == typeof(double) || rightType == typeof(double))
				return typeof(double);
			if (leftType == typeof(long) || rightType == typeof(long))
				return typeof(long);
			return typeof(int);
		}

		internal static bool IsPromotableNumeric(Type t)
		{
			return t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal);
		}

		// Widens an already-evaluated boxed numeric value to the promoted target type
		// (interpreted operators). Only widenings occur here (int->long->double->decimal),
		// so no value is ever narrowed/lost.
		internal static object CoerceNumericValue(object value, Type target)
		{
			if (target == typeof(long)) return Convert.ToInt64(value);
			if (target == typeof(double)) return Convert.ToDouble(value);
			if (target == typeof(decimal)) return Convert.ToDecimal(value);
			return Convert.ToInt32(value);
		}

		// Widens a numeric operand expression to the promoted target type (compiled operators).
		protected static Expression CoerceNumericExpression(Expression operand, Type target)
		{
			return operand.Type == target ? operand : Expression.Convert(operand, target);
		}

	}
	abstract class BinaryAstExpression : AstExpression
	{
		protected readonly AstExpression e1;
		protected readonly AstExpression e2;

		protected BinaryAstExpression(AstExpression e1, AstExpression e2)
		{
			this.e1 = e1;
			this.e2 = e2;
		}

		internal AstExpression LeftExpression => e1;
		internal AstExpression RightExpression => e2;

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			e1.PreparePatternMatching(patternAst, ref position);
			e2.PreparePatternMatching(patternAst, ref position);
		}
	}
	abstract class UnaryAstExpression : AstExpression
	{
		protected readonly AstExpression e;

		protected UnaryAstExpression(AstExpression e)
		{
			this.e = e;
		}

		internal AstExpression AstExpression => e;

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			e.PreparePatternMatching(patternAst, ref position);
		}
	}

}
