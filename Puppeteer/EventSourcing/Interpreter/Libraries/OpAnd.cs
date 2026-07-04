using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpAnd : BinaryAstExpression
	{
		internal OpAnd(AstExpression e1, AstExpression e2) : base(e1, e2)
		{
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			// Build a short-circuit expression for the AND operator
			var leftExpr = e1.ExecuteExpression(parametersParam);
			var rightExpr = e2.ExecuteExpression(parametersParam);

			// Both sides must be boolean
			if (leftExpr.Type != typeof(bool))
				throw new LanguageException($"The AND operator cannot operate on an expression of type '{leftExpr.Type.Name}'.");

			if (rightExpr.Type != typeof(bool))
				throw new LanguageException($"The AND operator cannot operate on an expression of type '{rightExpr.Type.Name}'.");

			if (leftExpr is ConstantExpression && rightExpr is ConstantExpression)
			{
				var staticValue = Execute();
				return Expression.Constant(staticValue, typeof(bool));
			}

			// Uses Expression.AndAlso for short-circuit evaluation
			return Expression.AndAlso(leftExpr, rightExpr);
		}

		internal override Type ComputeType()
		{
			return typeof(bool);
		}

		internal override void ValidateStatically()
		{
			Type type1 = e1.ComputeType();
			if (type1 != typeof(bool))
			{
				throw new LanguageException($"The left-hand expression of AND must return a boolean value, but got type '{type1.Name}'.");
			}
			Type type2 = e2.ComputeType();
			if (type2 != typeof(bool))
			{
				throw new LanguageException($"The right-hand expression of AND must return a boolean value, but got type '{type2.Name}'.");
			}

			ForcedType = typeof(bool);
		}

		internal override object Execute()
		{
			object object1 = e1.Execute();
			Type type1 = object1.GetType();
			if (type1 != typeof(bool))
			{
				throw new LanguageException($"The AND operator cannot operate on a value of type '{type1.Name}'.");
			}

			bool cortoCircuito = !(bool)object1;
			if (cortoCircuito)
			{
				return false;
			}

			object object2 = (bool)e2.Execute();
			Type type2 = object1.GetType();
			if (type2 != typeof(bool))
			{
				throw new LanguageException($"The AND operator cannot operate on values of types '{type1.Name}' and '{type2.Name}'.");
			}

			return (bool)object2;
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			e1.write(result, databaseType);
			result.Append(" && ");
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
