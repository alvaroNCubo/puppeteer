using System;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpAdd : BinaryAstExpression
	{
		internal OpAdd(AstExpression e1, AstExpression e2) : base(e1, e2)
		{
		}

		// The types a concatenation absorbs, as a STATIC predicate: the promoted primitive set of
		// the language and nothing else. It mirrors RenderOperandForConcatenation exactly, and the
		// two live next to each other so neither can be widened without the other.
		private static bool IsPromotedForConcatenation(Type type)
		{
			return type == typeof(string)
				|| type == typeof(char)
				|| type == typeof(bool)
				|| type == typeof(DateTime)
				|| IsPromotableNumeric(type);
		}

		// A char INITIATES a concatenation the way a string does, so it is admitted on the left as
		// well as the right and the result is always text.
		//
		// This does not reopen the conversion that was removed from argument binding, because the two
		// questions are different in kind. Binding is a CHOICE among overloads: a char reaching a
		// string parameter bound it non-exactly, so a char overload added later took the binding from
		// entries already journaled. Concatenation is not a choice — a `+` involving text has exactly
		// one meaning, and no future version of any library can add a second candidate for it. With
		// nothing to steal the binding, there is no reason to make the author write the conversion.
		//
		// char + char is text too, NOT the sum of two code points as it would be in C#. char is
		// deliberately absent from the promoted NUMERIC set (a char is a letter here, not a small
		// integer), so concatenation is the only reading left, and it is the one an author writing
		// 'L'c + 'e'c means.
		private static bool InitiatesConcatenation(Type type)
		{
			return type == typeof(string) || type == typeof(char);
		}

		// A type the parse cannot pin down: an untyped global, a late-bound member access, the null
		// literal (typeof(object)). Static validation must NOT refuse these — only the runtime sees
		// the value — so they are admitted here and the shared renderer decides. Refusing them
		// statically would reject scripts whose operand does resolve to a promoted primitive.
		private static bool IsUnknownAtParse(Type type)
		{
			return type == null || type == typeof(object);
		}

		private static LanguageException ConcatenationRefused(Type refused)
		{
			// A missing value is a different authoring mistake from an unrenderable type, so it gets
			// its own wording: naming the admitted set would not help — no type is at fault.
			if (refused == null)
			{
				return new LanguageException(
					"The plus operator cannot concatenate a null value with a string. A value that may be "
					+ "absent has to be resolved before it reaches the operator.");
			}

			return new LanguageException(
				"The plus operator concatenates a string with a value of a promoted primitive type "
				+ "(string, char, int, long, double, decimal, datetime, bool). A value of type "
				+ $"'{refused.Name}' is not one of them, and the DSL does not convert it "
				+ "implicitly: the textual form of such a value is a representation the author chooses. "
				+ "Convert it explicitly — calling .ToString() on the value works from a script — and "
				+ "concatenate the result.");
		}

		// Concatenation is the overload with the string on the LEFT. A `+` whose left operand is a
		// number produces a number; one whose left operand is a date produces a date. None of them
		// is defined for a string on the right, so a string there is not an invitation to render the
		// left operand — it is an operator that does not exist.
		private static LanguageException ConcatenationRequiresStringOnTheLeft(Type left)
		{
			return new LanguageException(
				$"The plus operator is not defined for a left operand of type '{left?.Name ?? "null"}' "
				+ "and a string right operand: a left operand of that type yields a value of its own "
				+ "kind, not text. Concatenation is the form with the string on the LEFT — write the "
				+ "text first, or convert the left operand explicitly by calling .ToString() on it.");
		}

		// The right operand is a string, so this is the concatenation form and the LEFT operand has
		// to be a string as well. Reached from the compiled path when the left type is not known
		// while the expression is built: without it, a left operand that turned out to be a number
		// would be rendered there while the same expression with a statically known number is
		// refused — the divergence this operator is being aligned against, one level down.
		internal static string RenderLeftOperandAgainstAString(object value)
		{
			if (value is string text) return text;
			if (value is char letter) return letter.ToString();

			throw ConcatenationRequiresStringOnTheLeft(value?.GetType());
		}

		// Renders ONE operand of a concatenation, or refuses it. This is the single place where the
		// language decides what a `+` involving a string may absorb, and BOTH engines reach it: the
		// interpreted path calls it directly, and the compiled path emits a call to it whenever the
		// operand's type is not known while the expression is being built. One implementation with
		// two call sites is the point — the two engines previously carried separate rules for the
		// same operator, the compiled one ending in a general object.ToString() fallback the
		// interpreted one did not have. That made the meaning of a script depend on which engine
		// happened to run it, and a value written under one engine was dropped on replay under the
		// other, since replay re-executes the recorded statement.
		//
		// Rendering is culture-invariant because the result can be journaled and replayed on a host
		// whose culture differs; the textual form of a record must not depend on where it is read.
		internal static string RenderOperandForConcatenation(object value)
		{
			switch (value)
			{
				case string text: return text;
				// A char renders as its single character: a char IS a one-character string here.
				case char letter: return letter.ToString();
				case bool flag: return flag.ToString();
				case int number: return number.ToString(CultureInfo.InvariantCulture);
				case long number: return number.ToString(CultureInfo.InvariantCulture);
				case double number: return number.ToString(CultureInfo.InvariantCulture);
				case decimal number: return number.ToString(CultureInfo.InvariantCulture);
				case DateTime moment: return RenderMoment(moment);
			}

			throw ConcatenationRefused(value?.GetType());
		}

		// A date with no time of day carries no time in its textual form. Kept as one helper so the
		// interpreted path and the compiled path's inline lowering cannot drift on the format.
		private static string RenderMoment(DateTime moment)
		{
			if (moment.Hour == 0 && moment.Minute == 0 && moment.Second == 0)
				return moment.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);

			return moment.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
		}

		internal override Type ComputeType()
		{
			Type typeE1 = e1.ComputeType();
			Type typeE2 = e2.ComputeType();
			if (this.CoercesToString)
			{
				return typeof(string);
			}

			if (IsPromotableNumeric(typeE1) && IsPromotableNumeric(typeE2))
			{
				return PromotesTo(typeE1, typeE2);
			}
			else if (InitiatesConcatenation(typeE1))
			{
				// A concatenation yields a string only when the RIGHT operand is one the language
				// renders. Returning string unconditionally is what let static validation ACCEPT an
				// expression that one engine then refused at runtime: the operator promised a type
				// it could not always produce.
				if (IsUnknownAtParse(typeE2) || IsPromotedForConcatenation(typeE2)) return typeof(string);
				return null;
			}
			else if (typeE2 == typeof(string))
			{
				// A string on the right with something else on the left is not the concatenation
				// form. It is still a string WHEN the left turns out to be one, which only the
				// runtime can tell for a type unknown at parse; a known left type is refused here.
				if (IsUnknownAtParse(typeE1)) return typeof(string);
				return null;
			}
			// Temporal arithmetic: DateTime + TimeSpan = DateTime; TimeSpan + TimeSpan = TimeSpan
			if (typeE1 == typeof(DateTime) && typeE2 == typeof(TimeSpan)) return typeof(DateTime);
			if (typeE1 == typeof(TimeSpan) && typeE2 == typeof(DateTime)) return typeof(DateTime);
			if (typeE1 == typeof(TimeSpan) && typeE2 == typeof(TimeSpan)) return typeof(TimeSpan);
			return null;
		}

		internal override void ValidateStatically()
		{
			var type = ComputeType();
			if (type == null)
			{
				Type type1 = e1.ComputeType();
				Type type2 = e2.ComputeType();
				// The concatenation cases get their own diagnostics: with the string on the left the
				// refused operand is almost always a value whose textual form the author still has to
				// choose, and with the string on the right the operator itself does not exist.
				if (InitiatesConcatenation(type1))
				{
					throw ConcatenationRefused(type2);
				}
				if (type2 == typeof(string))
				{
					throw ConcatenationRequiresStringOnTheLeft(type1);
				}
				throw new LanguageException($"Cannot add or concatenate a value of type '{type1?.Name ?? "null"}' with a value of type '{type2?.Name ?? "null"}'.");
			}
			ForcedType = type;
		}

		internal override object Execute()
		{
			object object1 = e1.Execute();
			Type type1 = object1?.GetType();

			if (this.CoercesToString)
			{
				if (IsPromotableNumeric(type1))
				{
					object1 = object1?.ToString();
					type1 = typeof(string);
				}
			}

			object object2 = e2.Execute();
			Type type2 = object2?.GetType();

			if (type1 != null && type2 != null && IsPromotableNumeric(type1) && IsPromotableNumeric(type2))
			{
				Type promoted = PromotesTo(type1, type2);
				object a = CoerceNumericValue(object1, promoted);
				object b = CoerceNumericValue(object2, promoted);
				if (promoted == typeof(int)) return (int)a + (int)b;
				if (promoted == typeof(long)) return (long)a + (long)b;
				if (promoted == typeof(double)) return (double)a + (double)b;
				return (decimal)a + (decimal)b;
			}
			else if (InitiatesConcatenation(type1))
			{
				// The concatenation form. The pairs used to be enumerated one type at a time, which
				// is why each type added to the language had to be added here by hand; the shared
				// renderer decides instead, and the compiled path asks the same renderer. Both sides
				// go through it so a char on the left renders as its character.
				return RenderOperandForConcatenation(object1) + RenderOperandForConcatenation(object2);
			}
			else if (type2 == typeof(string))
			{
				// A string on the right without one on the left. The compiled path used to key on
				// "either side is a string" and rendered the left operand here, so the same
				// expression concatenated under one engine and was refused under the other.
				if (type1 == null) throw ConcatenationRefused(null);
				throw ConcatenationRequiresStringOnTheLeft(type1);
			}
			else if (type1 == typeof(DateTime) && type2 == typeof(TimeSpan))
			{
				return (DateTime)object1 + (TimeSpan)object2;
			}
			else if (type1 == typeof(TimeSpan) && type2 == typeof(DateTime))
			{
				return (DateTime)object2 + (TimeSpan)object1;
			}
			else if (type1 == typeof(TimeSpan) && type2 == typeof(TimeSpan))
			{
				return (TimeSpan)object1 + (TimeSpan)object2;
			}

			throw new LanguageException($"The plus operator cannot add or concatenate values of types '{type1?.Name ?? "null"}' and '{type2?.Name ?? "null"}'.");
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			var expr1 = e1.ExecuteExpression(parametersParam);
			var expr2 = e2.ExecuteExpression(parametersParam);

			if (expr1 is ConstantExpression && expr2 is ConstantExpression)
			{
				var result = Execute();
				// Folding two literals: the folded constant is typed by what the operator YIELDS, which
				// for a concatenation is string whichever of the two text-bearing types started it. A
				// char left operand used to fall through to the numeric ladder here and be refused as
				// an unpromotable pair — and only when both operands were literals, so the same
				// expression worked with a variable and failed with a literal.
				return Expression.Constant(result, InitiatesConcatenation(expr1.Type) ? typeof(string) : PromotesTo(expr1.Type, expr2.Type));
			}

			if (this.CoercesToString)
			{
				var toStringMethod = typeof(object).GetMethod(nameof(object.ToString));
				var left = Expression.Call(expr1, toStringMethod);
				var right = Expression.Call(expr2, toStringMethod);
				return Expression.Add(
					left,
					right,
					typeof(string).GetMethod(nameof(String.Concat), new[] { typeof(string), typeof(string) })
				);
			}

			var type1 = expr1.Type;
			var type2 = expr2.Type;

			// Culture-invariant coercion to string for concatenation: dates
			// use the fixed format MM/dd/yyyy and numbers the decimal separator '.',
			// just like the interpreted path. Without this the compiled path fell into
			// object.ToString() (CurrentCulture), diverging from the interpreted one and
			// breaking the DSL representation invariant in non-US cultures.
			Expression CoerceToString(Expression operand)
			{
				Type t = operand.Type;
				if (t == typeof(string))
				{
					return operand;
				}
				if (t == typeof(char))
				{
					return Expression.Call(operand, typeof(char).GetMethod(nameof(char.ToString), Type.EmptyTypes));
				}
				if (t == typeof(DateTime))
				{
					var hourProp = Expression.Property(operand, nameof(DateTime.Hour));
					var minuteProp = Expression.Property(operand, nameof(DateTime.Minute));
					var secondProp = Expression.Property(operand, nameof(DateTime.Second));
					var zero = Expression.Constant(0, typeof(int));
					var isShort = Expression.AndAlso(
						Expression.AndAlso(Expression.Equal(hourProp, zero), Expression.Equal(minuteProp, zero)),
						Expression.Equal(secondProp, zero));
					var toStringMethod = typeof(DateTime).GetMethod("ToString", new[] { typeof(string), typeof(IFormatProvider) });
					var invariant = Expression.Constant(CultureInfo.InvariantCulture, typeof(IFormatProvider));
					return Expression.Condition(
						isShort,
						Expression.Call(operand, toStringMethod, Expression.Constant("MM/dd/yyyy"), invariant),
						Expression.Call(operand, toStringMethod, Expression.Constant("MM/dd/yyyy HH:mm:ss"), invariant));
				}
				if (t == typeof(double) || t == typeof(decimal) || t == typeof(int) || t == typeof(long))
				{
					var toStringMethod = t.GetMethod(nameof(double.ToString), new[] { typeof(IFormatProvider) });
					return Expression.Call(operand, toStringMethod, Expression.Constant(CultureInfo.InvariantCulture, typeof(IFormatProvider)));
				}
				if (t == typeof(bool))
				{
					return Expression.Call(operand, typeof(bool).GetMethod(nameof(bool.ToString), Type.EmptyTypes));
				}
				// Anything else is either unknown while the expression is being built (a late-bound
				// member access, an untyped global) or a type the language does not render. Both go to
				// the shared renderer, which admits the promoted primitives and raises the same error
				// the interpreted path raises. This replaces a general object.ToString() fallback that
				// rendered EVERY type — a struct, an enum, a collection, a puppet — so an expression
				// only this engine accepted could be committed and then fail to replay under the other.
				var renderMethod = typeof(OpAdd).GetMethod(
					nameof(RenderOperandForConcatenation),
					BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
				return Expression.Call(renderMethod, Expression.Convert(operand, typeof(object)));
			}

			if (IsPromotableNumeric(type1) && IsPromotableNumeric(type2))
			{
				Type promoted = PromotesTo(type1, type2);
				return Expression.Add(CoerceNumericExpression(expr1, promoted), CoerceNumericExpression(expr2, promoted));
			}
			else if (InitiatesConcatenation(type1))
			{
				var left = CoerceToString(expr1);
				var right = CoerceToString(expr2);
				return Expression.Add(
					left,
					right,
					typeof(string).GetMethod(nameof(String.Concat), new[] { typeof(string), typeof(string) })
				);
			}
			else if (type2 == typeof(string))
			{
				// A string on the right is the concatenation form only if the left is a string too.
				// When the left type is unknown here, the shared left-operand renderer decides at
				// runtime — it accepts a string and refuses anything else, so a late-bound left
				// operand cannot slip past a rule a statically known one obeys.
				if (!IsUnknownAtParse(type1)) throw ConcatenationRequiresStringOnTheLeft(type1);

				var renderLeftMethod = typeof(OpAdd).GetMethod(
					nameof(RenderLeftOperandAgainstAString),
					BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
				return Expression.Add(
					Expression.Call(renderLeftMethod, Expression.Convert(expr1, typeof(object))),
					CoerceToString(expr2),
					typeof(string).GetMethod(nameof(String.Concat), new[] { typeof(string), typeof(string) })
				);
			}
			else if (type1 == typeof(DateTime) && type2 == typeof(TimeSpan))
			{
				return Expression.Add(expr1, expr2);
			}
			else if (type1 == typeof(TimeSpan) && type2 == typeof(DateTime))
			{
				return Expression.Add(expr2, expr1);
			}
			else if (type1 == typeof(TimeSpan) && type2 == typeof(TimeSpan))
			{
				return Expression.Add(expr1, expr2);
			}
			else
			{
				var msg = $"The Plus operator cannot add or concatenate a {type1?.Name ?? "null"} and {type2?.Name ?? "null"}";
				var exceptionConstructor = typeof(LanguageException).GetConstructor(new[] { typeof(string) });
				return Expression.Throw(
					Expression.New(exceptionConstructor, Expression.Constant(msg)),
					typeof(object)
				);
			}
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			e1.write(result, databaseType);
			result.Append(" + ");
			e2.write(result, databaseType);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			e1.Visit(v);
			e2.Visit(v);
		}

	}
}
