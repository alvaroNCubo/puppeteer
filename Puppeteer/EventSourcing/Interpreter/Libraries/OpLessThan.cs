using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpLessThan : BinaryAstExpression
	{
		internal OpLessThan(AstExpression e1, AstExpression e2) : base(e1, e2)
		{
		}

		internal override Type ComputeType()
		{
			return typeof(bool);
		}

		internal override void ValidateStatically()
		{
			var typeE1 = e1.ComputeType();
			var typeE2 = e2.ComputeType();
			bool tiposValidos = (typeE1 == typeof(int) || typeE1 == typeof(double) || typeE1 == typeof(decimal) || typeE1 == typeof(DateTime)) &&
				(typeE2 == typeof(int) || typeE2 == typeof(double) || typeE2 == typeof(decimal) || typeE2 == typeof(DateTime));
			bool ambosTimeSpan = typeE1 == typeof(TimeSpan) && typeE2 == typeof(TimeSpan);
			if (!tiposValidos && !ambosTimeSpan)
			{
				throw new LanguageException($"Operator '<' cannot compare value type '{typeE1.Name}' with value type '{typeE2.Name}'.");
			}
			ForcedType = typeof(bool);
		}

		internal override object Execute()
		{
			object objeto1 = e1.Execute();
			object objeto2 = e2.Execute();

			Type tipo1 = objeto1.GetType();
			Type tipo2 = objeto2.GetType();

			if (tipo1 == typeof(int) && tipo2 == typeof(int))
				return (int)objeto1 < (int)objeto2;

			if (tipo1 == typeof(int) && tipo2 == typeof(double))
				return Convert.ToDouble(objeto1) < (double)objeto2;

			if (tipo1 == typeof(int) && tipo2 == typeof(decimal))
				return Convert.ToDecimal(objeto1) < (decimal)objeto2;

			if (tipo1 == typeof(double) && tipo2 == typeof(int))
				return (double)objeto1 < Convert.ToDouble(objeto2);

			if (tipo1 == typeof(double) && tipo2 == typeof(double))
				return (double)objeto1 < (double)objeto2;

			if (tipo1 == typeof(double) && tipo2 == typeof(decimal))
				return Convert.ToDecimal(objeto1) < (decimal)objeto2;

			if (tipo1 == typeof(decimal) && tipo2 == typeof(int))
				return (decimal)objeto1 < Convert.ToDecimal(objeto2);

			if (tipo1 == typeof(decimal) && tipo2 == typeof(double))
				return (decimal)objeto1 < Convert.ToDecimal(objeto2);

			if (tipo1 == typeof(decimal) && tipo2 == typeof(decimal))
				return (decimal)objeto1 < (decimal)objeto2;

			if (tipo1 == typeof(DateTime) && tipo2 == typeof(DateTime))
				return (DateTime)objeto1 < (DateTime)objeto2;

			if (tipo1 == typeof(TimeSpan) && tipo2 == typeof(TimeSpan))
				return (TimeSpan)objeto1 < (TimeSpan)objeto2;

			throw new LanguageException($"Operator '<' cannot compare type '{tipo1.Name}' with type '{tipo2.Name}'.");
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			var left = e1.ExecuteExpression(parametersParam);
			var right = e2.ExecuteExpression(parametersParam);

			if (left is ConstantExpression && right is ConstantExpression)
			{
				var result = Execute();
				return Expression.Constant(result, typeof(bool));
			}

			var leftType = left.Type;
			var rightType = right.Type;

			if (leftType == typeof(int) && rightType == typeof(int))
				return Expression.LessThan(left, right);

			if (leftType == typeof(int) && rightType == typeof(double))
				return Expression.LessThan(Expression.Convert(left, typeof(double)), right);

			if (leftType == typeof(int) && rightType == typeof(decimal))
				return Expression.LessThan(Expression.Convert(left, typeof(decimal)), right);

			if (leftType == typeof(double) && rightType == typeof(int))
				return Expression.LessThan(left, Expression.Convert(right, typeof(double)));

			if (leftType == typeof(double) && rightType == typeof(double))
				return Expression.LessThan(left, right);

			if (leftType == typeof(double) && rightType == typeof(decimal))
				return Expression.LessThan(Expression.Convert(left, typeof(decimal)), right);

			if (leftType == typeof(decimal) && rightType == typeof(int))
				return Expression.LessThan(left, Expression.Convert(right, typeof(decimal)));

			if (leftType == typeof(decimal) && rightType == typeof(double))
				return Expression.LessThan(left, Expression.Convert(right, typeof(decimal)));

			if (leftType == typeof(decimal) && rightType == typeof(decimal))
				return Expression.LessThan(left, right);

			if (leftType == typeof(DateTime) && rightType == typeof(DateTime))
				return Expression.LessThan(left, right);

			if (leftType == typeof(TimeSpan) && rightType == typeof(TimeSpan))
				return Expression.LessThan(left, right);

			throw new LanguageException($"Operator '<' cannot compare type '{leftType.Name}' with type '{rightType.Name}'.");
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			e1.write(resultado, databaseType);
			resultado.Append(" < ");
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
