using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpGreaterOrEqual : BinaryAstExpression
	{
		internal OpGreaterOrEqual(AstExpression e1, AstExpression e2) : base(e1, e2)
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
			bool validTypes = (IsPromotableNumeric(typeE1) || typeE1 == typeof(DateTime)) &&
				(IsPromotableNumeric(typeE2) || typeE2 == typeof(DateTime));
			bool ambosTimeSpan = typeE1 == typeof(TimeSpan) && typeE2 == typeof(TimeSpan);
			if (!validTypes && !ambosTimeSpan)
			{
				throw new LanguageException($"Operator '>=' cannot compare value type '{typeE1.Name}' with value type '{typeE2.Name}'.");
			}
			ForcedType = typeof(bool);
		}

		internal override object Execute()
		{
			object object1 = e1.Execute();
			object object2 = e2.Execute();

			Type type1 = object1.GetType();
			Type type2 = object2.GetType();

			if (IsPromotableNumeric(type1) && IsPromotableNumeric(type2))
			{
				Type promoted = PromotesTo(type1, type2);
				object a = CoerceNumericValue(object1, promoted);
				object b = CoerceNumericValue(object2, promoted);
				if (promoted == typeof(int)) return (int)a >= (int)b;
				if (promoted == typeof(long)) return (long)a >= (long)b;
				if (promoted == typeof(double)) return (double)a >= (double)b;
				return (decimal)a >= (decimal)b;
			}
			else if (type1 == typeof(DateTime) && type2 == typeof(DateTime))
				return (DateTime)object1 >= (DateTime)object2;
			else if (type1 == typeof(TimeSpan) && type2 == typeof(TimeSpan))
				return (TimeSpan)object1 >= (TimeSpan)object2;

			throw new LanguageException($"Operator '>=' cannot compare type '{type1.Name}' with type '{type2.Name}'.");
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


			if (IsPromotableNumeric(type1) && IsPromotableNumeric(type2))
			{
				Type promoted = PromotesTo(type1, type2);
				return Expression.GreaterThanOrEqual(CoerceNumericExpression(left, promoted), CoerceNumericExpression(right, promoted));
			}

			if (type1 == typeof(DateTime) && type2 == typeof(DateTime))
				return Expression.GreaterThanOrEqual(left, right);

			if (type1 == typeof(TimeSpan) && type2 == typeof(TimeSpan))
				return Expression.GreaterThanOrEqual(left, right);

			throw new LanguageException($"Operator '>=' cannot compare type '{type1.Name}' with type '{type2.Name}'.");
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			e1.write(result, databaseType);
			result.Append(" >= ");
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
