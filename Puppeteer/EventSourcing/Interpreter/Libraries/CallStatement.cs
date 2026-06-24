using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class CallStatement : Statement
	{
		private readonly AstExpression expression;
		private readonly SymbolTable symbolTable;

		internal CallStatement(SymbolTable symbolTable, AstExpression expression)
		{
			this.expression = expression;
			this.symbolTable = symbolTable;
		}

		internal AstExpression AstExpression => expression;

		internal override void Execute(ExecutionOutput output)
		{
			expression.Execute();
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			return expression.ExecuteExpression(parametersParam);
		}

		internal override void ValidateStatically()
		{
			Type expressionType = expression.ComputeType();
			expression.ValidateStatically();
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			expression.PreparePatternMatching(patternAst, ref position);
		}

		// B.3.1: include the call expression so method name + arg shape propagate.
		internal override void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(nameof(CallStatement));
			expression.AccumulatePromotionCandidateHash(ref hc);
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerateTabs(tabs));
			expression.write(resultado, databaseType);
			resultado.Append(";\r");
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			expression.Visit(v);
		}

	}

}
