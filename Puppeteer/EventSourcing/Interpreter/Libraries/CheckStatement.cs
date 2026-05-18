using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class CheckStatement : Statement
	{
		private readonly AstExpression expression;
		private readonly EWI reason;

		internal CheckStatement(AstExpression expression, EWI reason)
		{
			this.expression = expression;
			this.reason = reason;
			base.FueFiltrado = true;
		}

		internal override void Execute(ExecutionOutput output)
		{
			object valorDeLaExpresion = expression.Execute();
			bool cumpleCondicion = ((bool)valorDeLaExpresion);
			if (!cumpleCondicion)
			{
				StringBuilder sB = new StringBuilder();
				expression.write(sB, DatabaseType.IN_MEMORY);

				reason.Execute(output.PrintBuffer);
			}
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			var exprExpression = expression.ExecuteExpression(parametersParam);

			var boolExpr = Expression.Convert(exprExpression, typeof(bool));

			var reasonExpr = reason.ExecuteExpression(parametersParam, outputParam);

			var ifBlock = Expression.IfThen(
				Expression.IsFalse(boolExpr),
				reasonExpr
			);

			return Expression.Block(ifBlock);
		}

		internal override void ValidateStatically()
		{
			expression.ValidateStatically();
			Type expressionType = expression.ComputeType();
			if (expressionType != typeof(bool))
			{
				throw new LanguageException($"A 'check' statement can only be executed when its condition is of type Boolean, but found type '{expressionType.Name}'.");
			}

			reason.ValidateStatically();

		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			expression.PreparePatternMatching(patternAst, ref position);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			expression.Visit(v);
			if (reason != null) reason.Visit(v);
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));

			if (expression != LiteralBoolean.LiteralTrue)
			{
				resultado.Append("Check (");
				expression.write(resultado, databaseType);
				resultado.Append(") ");
			}

			reason.Write(resultado, databaseType);
		}
	}
}
