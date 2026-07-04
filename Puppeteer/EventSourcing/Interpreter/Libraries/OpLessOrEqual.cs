using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpLessOrEqual : BinaryAstExpression
	{
		internal OpLessOrEqual(AstExpression e1, AstExpression e2) : base(e1, e2)
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
			bool validTypes = (typeE1 == typeof(int) || typeE1 == typeof(double) || typeE1 == typeof(decimal) || typeE1 == typeof(DateTime)) &&
				(typeE2 == typeof(int) || typeE2 == typeof(double) || typeE2 == typeof(decimal) || typeE2 == typeof(DateTime));
			bool ambosTimeSpan = typeE1 == typeof(TimeSpan) && typeE2 == typeof(TimeSpan);
			if (!validTypes && !ambosTimeSpan)
			{
				throw new LanguageException($"Operator '<=' cannot compare value type '{typeE1.Name}' with value type '{typeE2.Name}'.");
			}
			ForcedType = typeof(bool);
		}

		internal override object Execute()
		{
			object object1 = e1.Execute();
			object object2 = e2.Execute();

			Type type1 = object1.GetType();
			Type type2 = object2.GetType();

			if (type1 == typeof(int) && type2 == typeof(int))
				return (int)object1 <= (int)object2;

			if (type1 == typeof(int) && type2 == typeof(double))
				return Convert.ToDouble(object1) <= (double)object2;

			if (type1 == typeof(int) && type2 == typeof(decimal))
				return Convert.ToDecimal(object1) <= (decimal)object2;

			if (type1 == typeof(double) && type2 == typeof(int))
				return (double)object1 <= Convert.ToDouble(object2);

			if (type1 == typeof(double) && type2 == typeof(double))
				return (double)object1 <= (double)object2;

			if (type1 == typeof(double) && type2 == typeof(decimal))
				return Convert.ToDecimal(object1) <= (decimal)object2;

			if (type1 == typeof(decimal) && type2 == typeof(int))
				return (decimal)object1 <= Convert.ToDecimal(object2);

			if (type1 == typeof(decimal) && type2 == typeof(double))
				return (decimal)object1 <= Convert.ToDecimal(object2);

			if (type1 == typeof(decimal) && type2 == typeof(decimal))
				return (decimal)object1 <= (decimal)object2;

			if (type1 == typeof(DateTime) && type2 == typeof(DateTime))
				return (DateTime)object1 <= (DateTime)object2;

			if (type1 == typeof(TimeSpan) && type2 == typeof(TimeSpan))
				return (TimeSpan)object1 <= (TimeSpan)object2;

			throw new LanguageException($"Operator '<=' cannot compare type '{type1.Name}' with type '{type2.Name}'.");
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

			Type type1 = left.Type;
			Type type2 = right.Type;

			if (type1 == typeof(int) && type2 == typeof(int))
				return Expression.LessThanOrEqual(left, right);

			if (type1 == typeof(int) && type2 == typeof(double))
				return Expression.LessThanOrEqual(Expression.Convert(left, typeof(double)), right);

			if (type1 == typeof(int) && type2 == typeof(decimal))
				return Expression.LessThanOrEqual(Expression.Convert(left, typeof(decimal)), right);

			if (type1 == typeof(double) && type2 == typeof(int))
				return Expression.LessThanOrEqual(left, Expression.Convert(right, typeof(double)));

			if (type1 == typeof(double) && type2 == typeof(double))
				return Expression.LessThanOrEqual(left, right);

			if (type1 == typeof(double) && type2 == typeof(decimal))
				return Expression.LessThanOrEqual(Expression.Convert(left, typeof(decimal)), right);

			if (type1 == typeof(decimal) && type2 == typeof(int))
				return Expression.LessThanOrEqual(left, Expression.Convert(right, typeof(decimal)));

			if (type1 == typeof(decimal) && type2 == typeof(double))
				return Expression.LessThanOrEqual(left, Expression.Convert(right, typeof(decimal)));

			if (type1 == typeof(decimal) && type2 == typeof(decimal))
				return Expression.LessThanOrEqual(left, right);

			if (type1 == typeof(DateTime) && type2 == typeof(DateTime))
				return Expression.LessThanOrEqual(left, right);

			if (type1 == typeof(TimeSpan) && type2 == typeof(TimeSpan))
				return Expression.LessThanOrEqual(left, right);

			throw new LanguageException($"Operator '<=' cannot compare type '{type1.Name}' with type '{type2.Name}'.");
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			e1.write(result, databaseType);
			result.Append(" <= ");
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
