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
			else if ((typeE1 == typeof(int) || typeE1 == typeof(long)) && (typeE2 == typeof(int) || typeE2 == typeof(long)))
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
				Type type1 = e1.ComputeType();
				Type type2 = e2.ComputeType();
				throw new LanguageException($"Cannot divide a value of type '{type1.Name}' by a value of type '{type2.Name}'.");
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
				return (int)object1 / (int)object2;
			if (type1 == typeof(int) && type2 == typeof(double))
				return Convert.ToDouble(object1) / (double)object2;
			if (type1 == typeof(int) && type2 == typeof(decimal))
				return Convert.ToDecimal(object1) / (decimal)object2;
			if (type1 == typeof(double) && type2 == typeof(int))
				return (double)object1 / Convert.ToDouble(object2);
			if (type1 == typeof(double) && type2 == typeof(double))
				return (double)object1 / (double)object2;
			if (type1 == typeof(double) && type2 == typeof(decimal))
				return Convert.ToDecimal(object1) / (decimal)object2;
			if (type1 == typeof(decimal) && type2 == typeof(int))
				return (decimal)object1 / Convert.ToDecimal(object2);
			if (type1 == typeof(decimal) && type2 == typeof(double))
				return (decimal)object1 / Convert.ToDecimal(object2);
			if (type1 == typeof(decimal) && type2 == typeof(decimal))
				return (decimal)object1 / (decimal)object2;
			if (type1 == typeof(long) && type2 == typeof(long))
				return (long)object1 / (long)object2;
			if (type1 == typeof(long) && type2 == typeof(int))
				return (long)object1 / Convert.ToInt64(object2);
			if (type1 == typeof(int) && type2 == typeof(long))
				return Convert.ToInt64(object1) / (long)object2;
			if (type1 == typeof(long) && type2 == typeof(double))
				return Convert.ToDouble(object1) / (double)object2;
			if (type1 == typeof(double) && type2 == typeof(long))
				return (double)object1 / Convert.ToDouble(object2);
			if (type1 == typeof(long) && type2 == typeof(decimal))
				return Convert.ToDecimal(object1) / (decimal)object2;
			if (type1 == typeof(decimal) && type2 == typeof(long))
				return (decimal)object1 / Convert.ToDecimal(object2);

			throw new LanguageException($"Cannot divide a value of type '{type1.Name}' by a value of type '{type2.Name}'.");
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

			var type1 = e1.ComputeType();
			var type2 = e2.ComputeType();

			if (type1 == typeof(int) && type2 == typeof(int))
				return Expression.Divide(left, right);

			if (type1 == typeof(int) && type2 == typeof(double))
				return Expression.Divide(Expression.Convert(left, typeof(double)), right);

			if (type1 == typeof(int) && type2 == typeof(decimal))
				return Expression.Divide(Expression.Convert(left, typeof(decimal)), right);

			if (type1 == typeof(double) && type2 == typeof(int))
				return Expression.Divide(left, Expression.Convert(right, typeof(double)));

			if (type1 == typeof(double) && type2 == typeof(double))
				return Expression.Divide(left, right);

			if (type1 == typeof(double) && type2 == typeof(decimal))
				return Expression.Divide(Expression.Convert(left, typeof(decimal)), right);

			if (type1 == typeof(decimal) && type2 == typeof(int))
				return Expression.Divide(left, Expression.Convert(right, typeof(decimal)));

			if (type1 == typeof(decimal) && type2 == typeof(double))
				return Expression.Divide(left, Expression.Convert(right, typeof(decimal)));

			if (type1 == typeof(decimal) && type2 == typeof(decimal))
				return Expression.Divide(left, right);

			if (type1 == typeof(long) && type2 == typeof(long))
				return Expression.Divide(left, right);

			if (type1 == typeof(long) && type2 == typeof(int))
				return Expression.Divide(left, Expression.Convert(right, typeof(long)));

			if (type1 == typeof(int) && type2 == typeof(long))
				return Expression.Divide(Expression.Convert(left, typeof(long)), right);

			if (type1 == typeof(long) && type2 == typeof(double))
				return Expression.Divide(Expression.Convert(left, typeof(double)), right);

			if (type1 == typeof(double) && type2 == typeof(long))
				return Expression.Divide(left, Expression.Convert(right, typeof(double)));

			if (type1 == typeof(long) && type2 == typeof(decimal))
				return Expression.Divide(Expression.Convert(left, typeof(decimal)), right);

			if (type1 == typeof(decimal) && type2 == typeof(long))
				return Expression.Divide(left, Expression.Convert(right, typeof(decimal)));

			throw new LanguageException($"Cannot divide a value of type '{type1.Name}' by a value of type '{type2.Name}'.");
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			e1.write(result, databaseType);
			result.Append(" / ");
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
