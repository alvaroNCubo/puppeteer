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

			if ((typeE1 == typeof(int) || typeE1 == typeof(double) || typeE1 == typeof(decimal)) && (typeE2 == typeof(int) || typeE2 == typeof(double) || typeE2 == typeof(decimal)))
			{
				if (typeE1 == typeof(decimal) || typeE2 == typeof(decimal))
				{
					return typeof(decimal);
				}
				else if (typeE1 == typeof(double) || typeE2 == typeof(double))
				{
					return typeof(double);
				}
				else if (typeE1 == typeof(int) && typeE2 == typeof(int))
				{
					return typeof(int);
				}
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
				Type tipo1 = e1.ComputeType();
				Type tipo2 = e2.ComputeType();
				throw new LanguageException($"Cannot add or concatenate a value of type '{tipo1.Name}' with a value of type '{tipo2.Name}'.");
			}
			ForcedType = type;
		}

		internal override object Execute()
		{
			object objeto1 = e1.Execute();
			Type tipo1 = objeto1?.GetType();

			if (this.CoercesToString)
			{
				if (tipo1 == typeof(int) || tipo1 == typeof(double) || tipo1 == typeof(decimal))
				{
					objeto1 = objeto1?.ToString();
					tipo1 = typeof(string);
				}
			}

			object objeto2 = e2.Execute();
			Type tipo2 = objeto2?.GetType();

			if (tipo1 == typeof(int) && tipo2 == typeof(int))
			{
				return (int)objeto1 + (int)objeto2;
			}
			else if (tipo1 == typeof(int) && tipo2 == typeof(double))
			{
				return Convert.ToDouble(objeto1) + (double)objeto2;
			}
			else if (tipo1 == typeof(int) && tipo2 == typeof(decimal))
			{
				return Convert.ToDecimal(objeto1) + (decimal)objeto2;
			}
			else if (tipo1 == typeof(double) && tipo2 == typeof(int))
			{
				return (double)objeto1 + Convert.ToDouble(objeto2);
			}
			else if (tipo1 == typeof(double) && tipo2 == typeof(double))
			{
				return (double)objeto1 + (double)objeto2;
			}
			else if (tipo1 == typeof(double) && tipo2 == typeof(decimal))
			{
				return Convert.ToDecimal(objeto1) + (decimal)objeto2;
			}
			else if (tipo1 == typeof(decimal) && tipo2 == typeof(int))
			{
				return (decimal)objeto1 + Convert.ToDecimal(objeto2);
			}
			else if (tipo1 == typeof(decimal) && tipo2 == typeof(double))
			{
				return (decimal)objeto1 + Convert.ToDecimal(objeto2);
			}
			else if (tipo1 == typeof(decimal) && tipo2 == typeof(decimal))
			{
				return (decimal)objeto1 + (decimal)objeto2;
			}
			else if (tipo1 == typeof(string) && tipo2 == typeof(string))
			{
				return (string)objeto1 + (string)objeto2;
			}
			else if (tipo1 == typeof(string) && tipo2 == typeof(int))
			{
				return (string)objeto1 + (int)objeto2;
			}
			else if (tipo1 == typeof(string) && tipo2 == typeof(double))
			{
				return (string)objeto1 + ((double)objeto2).ToString(CultureInfo.InvariantCulture);
			}
			else if (tipo1 == typeof(string) && tipo2 == typeof(decimal))
			{
				return (string)objeto1 + ((decimal)objeto2).ToString(CultureInfo.InvariantCulture);
			}
			else if (tipo1 == typeof(string) && tipo2 == typeof(DateTime))
			{
				var valorDate = ((DateTime)objeto2);
				if (valorDate.Hour == 0 && valorDate.Minute == 0 && valorDate.Second == 0)
					return (string)objeto1 + valorDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
				else
					return (string)objeto1 + valorDate.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
			}
			else if (tipo1 == typeof(string) && tipo2 == typeof(bool))
			{
				return (string)objeto1 + ((bool)objeto2).ToString();
			}
			else if (tipo1 == typeof(DateTime) && tipo2 == typeof(TimeSpan))
			{
				return (DateTime)objeto1 + (TimeSpan)objeto2;
			}
			else if (tipo1 == typeof(TimeSpan) && tipo2 == typeof(DateTime))
			{
				return (DateTime)objeto2 + (TimeSpan)objeto1;
			}
			else if (tipo1 == typeof(TimeSpan) && tipo2 == typeof(TimeSpan))
			{
				return (TimeSpan)objeto1 + (TimeSpan)objeto2;
			}

			throw new LanguageException($"The plus operator cannot add or concatenate values of types '{tipo1?.Name ?? "null"}' and '{tipo2?.Name ?? "null"}'.");
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

			var tipo1 = expr1.Type;
			var tipo2 = expr2.Type;

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

			if (tipo1 == typeof(int) && tipo2 == typeof(int))
			{
				return Expression.Add(expr1, expr2);
			}
			else if ((tipo1 == typeof(int) && tipo2 == typeof(double)) ||
						(tipo1 == typeof(double) && tipo2 == typeof(int)) ||
						(tipo1 == typeof(double) && tipo2 == typeof(double)))
			{
				var left = tipo1 == typeof(double) ? expr1 : Expression.Convert(expr1, typeof(double));
				var right = tipo2 == typeof(double) ? expr2 : Expression.Convert(expr2, typeof(double));
				return Expression.Add(left, right);
			}
			else if ((tipo1 == typeof(int) && tipo2 == typeof(decimal)) ||
						(tipo1 == typeof(decimal) && tipo2 == typeof(int)) ||
						(tipo1 == typeof(decimal) && tipo2 == typeof(double)) ||
						(tipo1 == typeof(double) && tipo2 == typeof(decimal)) ||
						(tipo1 == typeof(decimal) && tipo2 == typeof(decimal)))
			{
				var left = tipo1 == typeof(decimal) ? expr1 : Expression.Convert(expr1, typeof(decimal));
				var right = tipo2 == typeof(decimal) ? expr2 : Expression.Convert(expr2, typeof(decimal));
				return Expression.Add(left, right);
			}
			else if (tipo1 == typeof(string) || tipo2 == typeof(string))
			{
				var left = CoerceToString(expr1);
				var right = CoerceToString(expr2);
				return Expression.Add(
					left,
					right,
					typeof(string).GetMethod(nameof(String.Concat), new[] { typeof(string), typeof(string) })
				);
			}
			else if (tipo1 == typeof(DateTime) && tipo2 == typeof(TimeSpan))
			{
				return Expression.Add(expr1, expr2);
			}
			else if (tipo1 == typeof(TimeSpan) && tipo2 == typeof(DateTime))
			{
				return Expression.Add(expr2, expr1);
			}
			else if (tipo1 == typeof(TimeSpan) && tipo2 == typeof(TimeSpan))
			{
				return Expression.Add(expr1, expr2);
			}
			else
			{
				var msg = $"El operador Mas no puede sumar ni concatenar un {tipo1?.Name ?? "null"} y {tipo2?.Name ?? "null"}";
				var exceptionConstructor = typeof(LanguageException).GetConstructor(new[] { typeof(string) });
				return Expression.Throw(
					Expression.New(exceptionConstructor, Expression.Constant(msg)),
					typeof(object)
				);
			}
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			e1.write(resultado, databaseType);
			resultado.Append(" + ");
			e2.write(resultado, databaseType);
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
