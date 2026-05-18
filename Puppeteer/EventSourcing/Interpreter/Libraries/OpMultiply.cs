using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpMultiply : BinaryAstExpression
	{
		internal OpMultiply(AstExpression e1, AstExpression e2) : base(e1, e2)
		{
		}

		internal override Type ComputeType()
		{
			Type typeE1 = e1.ComputeType();
			Type typeE2 = e2.ComputeType();
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
			return null;
		}

		internal override void ValidateStatically()
		{
			var type = ComputeType();
			if (type == null)
			{
				Type tipo1 = e1.ComputeType();
				Type tipo2 = e2.ComputeType();
				throw new LanguageException($"Cannot multiply a value of type '{tipo1.Name}' by a value of type '{tipo2.Name}'.");
			}
			ForcedType = type;
		}
		internal override object Execute()
		{
			object objeto1 = e1.Execute();
			object objeto2 = e2.Execute();

			Type tipo1 = objeto1.GetType();
			Type tipo2 = objeto2.GetType();

			if (tipo1 == typeof(int) && tipo2 == typeof(int))
				return (int)objeto1 * (int)objeto2;
			else if (tipo1 == typeof(int) && tipo2 == typeof(double))
				return Convert.ToDouble(objeto1) * (double)objeto2;
			else if (tipo1 == typeof(int) && tipo2 == typeof(decimal))
				return Convert.ToDecimal(objeto1) * (decimal)objeto2;
			else if (tipo1 == typeof(double) && tipo2 == typeof(int))
				return (double)objeto1 * Convert.ToDouble(objeto2);
			else if (tipo1 == typeof(double) && tipo2 == typeof(double))
				return (double)objeto1 * (double)objeto2;
			else if (tipo1 == typeof(double) && tipo2 == typeof(decimal))
				return Convert.ToDecimal(objeto1) * (decimal)objeto2;
			else if (tipo1 == typeof(decimal) && tipo2 == typeof(int))
				return (decimal)objeto1 * Convert.ToDecimal(objeto2);
			else if (tipo1 == typeof(decimal) && tipo2 == typeof(double))
				return (decimal)objeto1 * Convert.ToDecimal(objeto2);
			else if (tipo1 == typeof(decimal) && tipo2 == typeof(decimal))
				return (decimal)objeto1 * (decimal)objeto2;

			throw new LanguageException($"Cannot multiply a value of type '{tipo1.Name}' by a value of type '{tipo2.Name}'.");
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			Expression left = this.e1.ExecuteExpression(parametersParam);
			Expression right = this.e2.ExecuteExpression(parametersParam);

			if (left is ConstantExpression && right is ConstantExpression)
			{
				var result = Execute();
				return Expression.Constant(result, PromotesTo(left.Type, right.Type));
			}

			Expression resultado = null;

			if (left.Type == typeof(int) && right.Type == typeof(int))
			{
				resultado = Expression.Multiply(left, right);
			}
			else if (left.Type == typeof(int) && right.Type == typeof(double))
			{
				resultado = Expression.Multiply(Expression.Convert(left, typeof(double)), right);
			}
			else if (left.Type == typeof(int) && right.Type == typeof(decimal))
			{
				resultado = Expression.Multiply(Expression.Convert(left, typeof(decimal)), right);
			}
			else if (left.Type == typeof(double) && right.Type == typeof(int))
			{
				resultado = Expression.Multiply(left, Expression.Convert(right, typeof(double)));
			}
			else if (left.Type == typeof(double) && right.Type == typeof(double))
			{
				resultado = Expression.Multiply(left, right);
			}
			else if (left.Type == typeof(double) && right.Type == typeof(decimal))
			{
				resultado = Expression.Multiply(Expression.Convert(left, typeof(decimal)), right);
			}
			else if (left.Type == typeof(decimal) && right.Type == typeof(int))
			{
				resultado = Expression.Multiply(left, Expression.Convert(right, typeof(decimal)));
			}
			else if (left.Type == typeof(decimal) && right.Type == typeof(double))
			{
				resultado = Expression.Multiply(left, Expression.Convert(right, typeof(decimal)));
			}
			else if (left.Type == typeof(decimal) && right.Type == typeof(decimal))
			{
				resultado = Expression.Multiply(left, right);
			}
			else
			{
				throw new LanguageException($"Cannot multiply a value of type '{left.Type.Name}' by a value of type '{right.Type.Name}'.");
			}
			return resultado;
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			e1.write(resultado, databaseType);
			resultado.Append(" * ");
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
