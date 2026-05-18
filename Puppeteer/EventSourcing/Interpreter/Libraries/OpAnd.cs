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
				var valorEstatico = Execute();
				return Expression.Constant(valorEstatico, typeof(bool));
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
			Type tipo1 = e1.ComputeType();
			if (tipo1 != typeof(bool))
			{
				throw new LanguageException($"The left-hand expression of AND must return a boolean value, but got type '{tipo1.Name}'.");
			}
			Type tipo2 = e2.ComputeType();
			if (tipo2 != typeof(bool))
			{
				throw new LanguageException($"The right-hand expression of AND must return a boolean value, but got type '{tipo2.Name}'.");
			}

			ForcedType = typeof(bool);
		}

		internal override object Execute()
		{
			object objeto1 = e1.Execute();
			Type tipo1 = objeto1.GetType();
			if (tipo1 != typeof(bool))
			{
				throw new LanguageException($"The AND operator cannot operate on a value of type '{tipo1.Name}'.");
			}

			bool cortoCircuito = !(bool)objeto1;
			if (cortoCircuito)
			{
				return false;
			}

			object objeto2 = (bool)e2.Execute();
			Type tipo2 = objeto1.GetType();
			if (tipo2 != typeof(bool))
			{
				throw new LanguageException($"The AND operator cannot operate on values of types '{tipo1.Name}' and '{tipo2.Name}'.");
			}

			return (bool)objeto2;
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			e1.write(resultado, databaseType);
			resultado.Append(" && ");
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
