using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpDivide : BinaryAstExpression
	{
		internal OpDivide(AstExpression e1, AstExpression e2) : base(e1, e2)
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
				var noPerderPrecision = typeof(double);
				return noPerderPrecision;
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
				throw new LanguageException($"Cannot divide a value of type '{tipo1.Name}' by a value of type '{tipo2.Name}'.");
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
				return (int)objeto1 / (int)objeto2;
			if (tipo1 == typeof(int) && tipo2 == typeof(double))
				return Convert.ToDouble(objeto1) / (double)objeto2;
			if (tipo1 == typeof(int) && tipo2 == typeof(decimal))
				return Convert.ToDecimal(objeto1) / (decimal)objeto2;
			if (tipo1 == typeof(double) && tipo2 == typeof(int))
				return (double)objeto1 / Convert.ToDouble(objeto2);
			if (tipo1 == typeof(double) && tipo2 == typeof(double))
				return (double)objeto1 / (double)objeto2;
			if (tipo1 == typeof(double) && tipo2 == typeof(decimal))
				return Convert.ToDecimal(objeto1) / (decimal)objeto2;
			if (tipo1 == typeof(decimal) && tipo2 == typeof(int))
				return (decimal)objeto1 / Convert.ToDecimal(objeto2);
			if (tipo1 == typeof(decimal) && tipo2 == typeof(double))
				return (decimal)objeto1 / Convert.ToDecimal(objeto2);
			if (tipo1 == typeof(decimal) && tipo2 == typeof(decimal))
				return (decimal)objeto1 / (decimal)objeto2;

			throw new LanguageException($"Cannot divide a value of type '{tipo1.Name}' by a value of type '{tipo2.Name}'.");
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			var left = e1.ExecuteExpression(parametersParam);
			var right = e2.ExecuteExpression(parametersParam);

			if (left is ConstantExpression && right is ConstantExpression)
			{
				var result = Execute();
				return Expression.Constant(result, PromotesTo(left.Type, right.Type));
			}

			var tipo1 = e1.ComputeType();
			var tipo2 = e2.ComputeType();

			if (tipo1 == typeof(int) && tipo2 == typeof(int))
				return Expression.Divide(left, right);

			if (tipo1 == typeof(int) && tipo2 == typeof(double))
				return Expression.Divide(Expression.Convert(left, typeof(double)), right);

			if (tipo1 == typeof(int) && tipo2 == typeof(decimal))
				return Expression.Divide(Expression.Convert(left, typeof(decimal)), right);

			if (tipo1 == typeof(double) && tipo2 == typeof(int))
				return Expression.Divide(left, Expression.Convert(right, typeof(double)));

			if (tipo1 == typeof(double) && tipo2 == typeof(double))
				return Expression.Divide(left, right);

			if (tipo1 == typeof(double) && tipo2 == typeof(decimal))
				return Expression.Divide(Expression.Convert(left, typeof(decimal)), right);

			if (tipo1 == typeof(decimal) && tipo2 == typeof(int))
				return Expression.Divide(left, Expression.Convert(right, typeof(decimal)));

			if (tipo1 == typeof(decimal) && tipo2 == typeof(double))
				return Expression.Divide(left, Expression.Convert(right, typeof(decimal)));

			if (tipo1 == typeof(decimal) && tipo2 == typeof(decimal))
				return Expression.Divide(left, right);

			throw new LanguageException($"Cannot divide a value of type '{tipo1.Name}' by a value of type '{tipo2.Name}'.");
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			e1.write(resultado, databaseType);
			resultado.Append(" / ");
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
