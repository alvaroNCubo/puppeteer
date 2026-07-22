using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpSubtract : BinaryAstExpression
	{
		internal OpSubtract(AstExpression e1, AstExpression e2) : base(e1, e2)
		{
		}

		internal override Type ComputeType()
		{
			Type typeE1 = e1.ComputeType();
			Type typeE2 = e2.ComputeType();
			if (IsPromotableNumeric(typeE1) && IsPromotableNumeric(typeE2))
			{
				return PromotesTo(typeE1, typeE2);
			}
			// Time arithmetic
			if (typeE1 == typeof(DateTime) && typeE2 == typeof(DateTime)) return typeof(TimeSpan);
			if (typeE1 == typeof(DateTime) && typeE2 == typeof(TimeSpan)) return typeof(DateTime);
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
				throw new LanguageException($"Cannot subtract a value of type '{type2.Name}' from a value of type '{type1.Name}'.");
			}
			ForcedType = type;
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
				if (promoted == typeof(int)) return (int)a - (int)b;
				if (promoted == typeof(long)) return (long)a - (long)b;
				if (promoted == typeof(double)) return (double)a - (double)b;
				return (decimal)a - (decimal)b;
			}
			else if (type1 == typeof(DateTime) && type2 == typeof(DateTime))
				return (DateTime)object1 - (DateTime)object2;
			else if (type1 == typeof(DateTime) && type2 == typeof(TimeSpan))
				return (DateTime)object1 - (TimeSpan)object2;
			else if (type1 == typeof(TimeSpan) && type2 == typeof(TimeSpan))
				return (TimeSpan)object1 - (TimeSpan)object2;

			throw new LanguageException($"The subtract operator cannot operate on types '{type1.Name}' and '{type2.Name}'.");
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

			if (IsPromotableNumeric(left.Type) && IsPromotableNumeric(right.Type))
			{
				Type promoted = PromotesTo(left.Type, right.Type);
				result = Expression.Subtract(CoerceNumericExpression(left, promoted), CoerceNumericExpression(right, promoted));
			}
			else if (left.Type == typeof(DateTime) && right.Type == typeof(DateTime))
			{
				result = Expression.Subtract(left, right);
			}
			else if (left.Type == typeof(DateTime) && right.Type == typeof(TimeSpan))
			{
				result = Expression.Subtract(left, right);
			}
			else if (left.Type == typeof(TimeSpan) && right.Type == typeof(TimeSpan))
			{
				result = Expression.Subtract(left, right);
			}
			else
			{
				throw new LanguageException($"Cannot subtract a value of type '{right.Type.Name}' from a value of type '{left.Type.Name}'.");
			}
			return result;
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			e1.write(result, databaseType);
			result.Append(" - ");
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
