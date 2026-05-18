using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class TernaryAstExpression : AstExpression
	{
		private readonly AstExpression condicion;
		private readonly AstExpression siVerdadero;
		private readonly AstExpression siFalso;

		internal TernaryAstExpression(AstExpression condicion, AstExpression siVerdadero, AstExpression siFalso)
		{
			ArgumentNullException.ThrowIfNull(condicion);
			ArgumentNullException.ThrowIfNull(siVerdadero);
			ArgumentNullException.ThrowIfNull(siFalso);
			this.condicion = condicion;
			this.siVerdadero = siVerdadero;
			this.siFalso = siFalso;
		}

		internal override Type ComputeType()
		{
			Type trueType = siVerdadero.ComputeType();
			Type falseType = siFalso.ComputeType();

			if (trueType == falseType)
				return trueType;

			return PromotesTo(trueType, falseType);
		}

		internal override void ValidateStatically()
		{
			condicion.ValidateStatically();
			siVerdadero.ValidateStatically();
			siFalso.ValidateStatically();

			Type conditionType = condicion.ComputeType();
			if (conditionType != typeof(bool))
			{
				throw new LanguageException($"The condition of the ternary operator must be of type Boolean, but found type '{conditionType.Name}'.");
			}

			ForcedType = ComputeType();
		}

		internal override object Execute()
		{
			object valorCondicion = condicion.Execute();
			if (valorCondicion.GetType() != typeof(bool))
			{
				throw new LanguageException($"The condition of the ternary operator must be of type Boolean, but found type '{valorCondicion.GetType().Name}'.");
			}

			bool cumple = (bool)valorCondicion;
			return cumple ? siVerdadero.Execute() : siFalso.Execute();
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			Expression condExpr = condicion.ExecuteExpression(parametersParam);
			Expression trueExpr = siVerdadero.ExecuteExpression(parametersParam);
			Expression falseExpr = siFalso.ExecuteExpression(parametersParam);

			if (condExpr.Type != typeof(bool))
				throw new LanguageException($"The condition of the ternary operator must be of type Boolean, but found type '{condExpr.Type.Name}'.");

			if (trueExpr.Type != falseExpr.Type)
			{
				Type promotedType = PromotesTo(trueExpr.Type, falseExpr.Type);
				if (trueExpr.Type != promotedType)
					trueExpr = Expression.Convert(trueExpr, promotedType);
				if (falseExpr.Type != promotedType)
					falseExpr = Expression.Convert(falseExpr, promotedType);
			}

			if (condExpr is ConstantExpression constCond)
			{
				bool valorEstatico = (bool)constCond.Value;
				return valorEstatico ? trueExpr : falseExpr;
			}

			return Expression.Condition(condExpr, trueExpr, falseExpr);
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			condicion.write(resultado, databaseType);
			resultado.Append(" ? ");
			siVerdadero.write(resultado, databaseType);
			resultado.Append(" : ");
			siFalso.write(resultado, databaseType);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			condicion.Visit(v);
			siVerdadero.Visit(v);
			siFalso.Visit(v);
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			condicion.PreparePatternMatching(patternAst, ref position);
			siVerdadero.PreparePatternMatching(patternAst, ref position);
			siFalso.PreparePatternMatching(patternAst, ref position);
		}
	}
}
