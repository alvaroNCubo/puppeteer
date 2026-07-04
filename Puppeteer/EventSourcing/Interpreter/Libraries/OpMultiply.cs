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
				Type type1 = e1.ComputeType();
				Type type2 = e2.ComputeType();
				throw new LanguageException($"Cannot multiply a value of type '{type1.Name}' by a value of type '{type2.Name}'.");
			}
			ForcedType = type;
		}
		internal override object Execute()
		{
			object object1 = e1.Execute();
			object object2 = e2.Execute();

			Type type1 = object1.GetType();
			Type type2 = object2.GetType();

			if (type1 == typeof(int) && type2 == typeof(int))
				return (int)object1 * (int)object2;
			else if (type1 == typeof(int) && type2 == typeof(double))
				return Convert.ToDouble(object1) * (double)object2;
			else if (type1 == typeof(int) && type2 == typeof(decimal))
				return Convert.ToDecimal(object1) * (decimal)object2;
			else if (type1 == typeof(double) && type2 == typeof(int))
				return (double)object1 * Convert.ToDouble(object2);
			else if (type1 == typeof(double) && type2 == typeof(double))
				return (double)object1 * (double)object2;
			else if (type1 == typeof(double) && type2 == typeof(decimal))
				return Convert.ToDecimal(object1) * (decimal)object2;
			else if (type1 == typeof(decimal) && type2 == typeof(int))
				return (decimal)object1 * Convert.ToDecimal(object2);
			else if (type1 == typeof(decimal) && type2 == typeof(double))
				return (decimal)object1 * Convert.ToDecimal(object2);
			else if (type1 == typeof(decimal) && type2 == typeof(decimal))
				return (decimal)object1 * (decimal)object2;

			throw new LanguageException($"Cannot multiply a value of type '{type1.Name}' by a value of type '{type2.Name}'.");
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			Expression left = this.e1.ExecuteExpression(parametersParam);
			Expression right = this.e2.ExecuteExpression(parametersParam);

			if (left is ConstantExpression && right is ConstantExpression)
			{
				var folded = Execute();
				return Expression.Constant(folded, PromotesTo(left.Type, right.Type));
			}

			Expression result = null;

			if (left.Type == typeof(int) && right.Type == typeof(int))
			{
				result = Expression.Multiply(left, right);
			}
			else if (left.Type == typeof(int) && right.Type == typeof(double))
			{
				result = Expression.Multiply(Expression.Convert(left, typeof(double)), right);
			}
			else if (left.Type == typeof(int) && right.Type == typeof(decimal))
			{
				result = Expression.Multiply(Expression.Convert(left, typeof(decimal)), right);
			}
			else if (left.Type == typeof(double) && right.Type == typeof(int))
			{
				result = Expression.Multiply(left, Expression.Convert(right, typeof(double)));
			}
			else if (left.Type == typeof(double) && right.Type == typeof(double))
			{
				result = Expression.Multiply(left, right);
			}
			else if (left.Type == typeof(double) && right.Type == typeof(decimal))
			{
				result = Expression.Multiply(Expression.Convert(left, typeof(decimal)), right);
			}
			else if (left.Type == typeof(decimal) && right.Type == typeof(int))
			{
				result = Expression.Multiply(left, Expression.Convert(right, typeof(decimal)));
			}
			else if (left.Type == typeof(decimal) && right.Type == typeof(double))
			{
				result = Expression.Multiply(left, Expression.Convert(right, typeof(decimal)));
			}
			else if (left.Type == typeof(decimal) && right.Type == typeof(decimal))
			{
				result = Expression.Multiply(left, right);
			}
			else
			{
				throw new LanguageException($"Cannot multiply a value of type '{left.Type.Name}' by a value of type '{right.Type.Name}'.");
			}
			return result;
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			e1.write(result, databaseType);
			result.Append(" * ");
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
