using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class TernaryAstExpression : AstExpression
	{
		private readonly AstExpression condition;
		private readonly AstExpression ifTrue;
		private readonly AstExpression ifFalse;

		internal TernaryAstExpression(AstExpression condition, AstExpression ifTrue, AstExpression ifFalse)
		{
			ArgumentNullException.ThrowIfNull(condition);
			ArgumentNullException.ThrowIfNull(ifTrue);
			ArgumentNullException.ThrowIfNull(ifFalse);
			this.condition = condition;
			this.ifTrue = ifTrue;
			this.ifFalse = ifFalse;
		}

		internal override Type ComputeType()
		{
			Type trueType = ifTrue.ComputeType();
			Type falseType = ifFalse.ComputeType();

			if (trueType == falseType)
				return trueType;

			return PromotesTo(trueType, falseType);
		}

		internal override void ValidateStatically()
		{
			condition.ValidateStatically();
			ifTrue.ValidateStatically();
			ifFalse.ValidateStatically();

			Type conditionType = condition.ComputeType();
			if (conditionType != typeof(bool))
			{
				throw new LanguageException($"The condition of the ternary operator must be of type Boolean, but found type '{conditionType.Name}'.");
			}

			ForcedType = ComputeType();
		}

		internal override object Execute()
		{
			object conditionValue = condition.Execute();
			if (conditionValue.GetType() != typeof(bool))
			{
				throw new LanguageException($"The condition of the ternary operator must be of type Boolean, but found type '{conditionValue.GetType().Name}'.");
			}

			bool cumple = (bool)conditionValue;
			return cumple ? ifTrue.Execute() : ifFalse.Execute();
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			Expression condExpr = condition.ExecuteExpression(parametersParam);
			Expression trueExpr = ifTrue.ExecuteExpression(parametersParam);
			Expression falseExpr = ifFalse.ExecuteExpression(parametersParam);

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
				bool staticValue = (bool)constCond.Value;
				return staticValue ? trueExpr : falseExpr;
			}

			return Expression.Condition(condExpr, trueExpr, falseExpr);
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			condition.write(result, databaseType);
			result.Append(" ? ");
			ifTrue.write(result, databaseType);
			result.Append(" : ");
			ifFalse.write(result, databaseType);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			condition.Visit(v);
			ifTrue.Visit(v);
			ifFalse.Visit(v);
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			condition.PreparePatternMatching(patternAst, ref position);
			ifTrue.PreparePatternMatching(patternAst, ref position);
			ifFalse.PreparePatternMatching(patternAst, ref position);
		}
	}
}
