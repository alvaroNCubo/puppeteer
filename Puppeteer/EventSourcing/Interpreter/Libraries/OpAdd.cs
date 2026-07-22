using System;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpAdd : BinaryAstExpression
	{
		internal OpAdd(AstExpression e1, AstExpression e2) : base(e1, e2)
		{
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
			else if (typeE1 == typeof(string) || typeE2 == typeof(string))
			{
				return typeof(string);
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
				throw new LanguageException($"Cannot add or concatenate a value of type '{type1.Name}' with a value of type '{type2.Name}'.");
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
			else if (type1 == typeof(string) && type2 == typeof(string))
			{
				return (string)object1 + (string)object2;
			}
			else if (type1 == typeof(string) && type2 == typeof(int))
			{
				return (string)object1 + (int)object2;
			}
			else if (type1 == typeof(string) && type2 == typeof(long))
			{
				return (string)object1 + ((long)object2).ToString(CultureInfo.InvariantCulture);
			}
			else if (type1 == typeof(string) && type2 == typeof(double))
			{
				return (string)object1 + ((double)object2).ToString(CultureInfo.InvariantCulture);
			}
			else if (type1 == typeof(string) && type2 == typeof(decimal))
			{
				return (string)object1 + ((decimal)object2).ToString(CultureInfo.InvariantCulture);
			}
			else if (type1 == typeof(string) && type2 == typeof(DateTime))
			{
				var valueDate = ((DateTime)object2);
				if (valueDate.Hour == 0 && valueDate.Minute == 0 && valueDate.Second == 0)
					return (string)object1 + valueDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
				else
					return (string)object1 + valueDate.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
			}
			else if (type1 == typeof(string) && type2 == typeof(bool))
			{
				return (string)object1 + ((bool)object2).ToString();
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
				return Expression.Constant(result, expr1.Type == typeof(string) ? typeof(string) : PromotesTo(expr1.Type, expr2.Type));
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
				if (t == typeof(double) || t == typeof(decimal))
				{
					var toStringMethod = t.GetMethod(nameof(double.ToString), new[] { typeof(IFormatProvider) });
					return Expression.Call(operand, toStringMethod, Expression.Constant(CultureInfo.InvariantCulture, typeof(IFormatProvider)));
				}
				return Expression.Call(operand, typeof(object).GetMethod(nameof(object.ToString)));
			}

			if (IsPromotableNumeric(type1) && IsPromotableNumeric(type2))
			{
				Type promoted = PromotesTo(type1, type2);
				return Expression.Add(CoerceNumericExpression(expr1, promoted), CoerceNumericExpression(expr2, promoted));
			}
			else if (type1 == typeof(string) || type2 == typeof(string))
			{
				var left = CoerceToString(expr1);
				var right = CoerceToString(expr2);
				return Expression.Add(
					left,
					right,
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
