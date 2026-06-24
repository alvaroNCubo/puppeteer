using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class IfStatement : Statement
	{
		private AstExpression expression;
		private Statement ifBranchStatement;
		private Statement elseBranchStatement;
		private readonly SymbolTable symbolTable;


		internal IfStatement(SymbolTable symbolTable, AstExpression expression, Statement ifBranchStatement)
		{
			this.symbolTable = symbolTable;
			this.expression = expression;
			this.ifBranchStatement = ifBranchStatement;
			this.elseBranchStatement = null;
		}

		internal IfStatement(SymbolTable symbolTable, AstExpression expression, Statement ifBranchStatement, Statement elseBranchStatement)
		{
			this.symbolTable = symbolTable;
			this.expression = expression;
			this.ifBranchStatement = ifBranchStatement;
			this.elseBranchStatement = elseBranchStatement;
		}

		internal override void Execute(ExecutionOutput output)
		{
			object valorDeLaExpresion = expression.Execute();
			bool cumpleCondicion = (bool)valorDeLaExpresion;
			if (cumpleCondicion)
			{
				if (!(ifBranchStatement is BlockStatement))
				{
					if (Program != null) Program.lastExecutedStatement = ifBranchStatement;
				}

				ifBranchStatement.Execute(output);
			}
			else if (elseBranchStatement != null)
			{
				if (!(elseBranchStatement is BlockStatement))
				{
					if (Program != null) Program.lastExecutedStatement = elseBranchStatement;
				}

				elseBranchStatement.Execute(output);
			}
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			Expression resultado = null;
			if (elseBranchStatement != null)
			{
				resultado = Expression.IfThenElse(
					expression.ExecuteExpression(parametersParam),
					ifBranchStatement.ExecuteExpression(parametersParam, outputParam),
					elseBranchStatement.ExecuteExpression(parametersParam, outputParam)
				);
			}
			else if (ifBranchStatement != null)
			{
				resultado = Expression.IfThen(
					expression.ExecuteExpression(parametersParam),
					ifBranchStatement.ExecuteExpression(parametersParam, outputParam)
				);
			}
			else
			{
				resultado = Expression.IsTrue(
					expression.ExecuteExpression(parametersParam)
				);
			}
			return resultado;
		}

		internal override void ValidateStatically()
		{
			expression.ValidateStatically();
			Type expressionType = expression.ComputeType();
			if (expressionType != typeof(bool))
			{
				throw new LanguageException($"An 'if' statement can only be executed when its condition is of type Boolean, but found type '{expressionType.Name}'.");
			}
			ifBranchStatement.ValidateStatically();
			if (elseBranchStatement != null)
			{
				elseBranchStatement.ValidateStatically();
			}
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			expression.PreparePatternMatching(patternAst, ref position);

			if (ifBranchStatement != null) ifBranchStatement.PreparePatternMatching(patternAst, ref position);
			if (elseBranchStatement != null) elseBranchStatement.PreparePatternMatching(patternAst, ref position);
		}

		// B.3.1: include condition + branches.
		internal override void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(nameof(IfStatement));
			expression.AccumulatePromotionCandidateHash(ref hc);
			if (ifBranchStatement != null) { hc.Add(1); ifBranchStatement.AccumulatePromotionCandidateHash(ref hc); } else { hc.Add(0); }
			if (elseBranchStatement != null) { hc.Add(1); elseBranchStatement.AccumulatePromotionCandidateHash(ref hc); } else { hc.Add(0); }
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			expression.Visit(v);
			ifBranchStatement.Visit(v);
			if (elseBranchStatement != null) elseBranchStatement.Visit(v);
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerateTabs(tabs));
			resultado.Append("If (");
			expression.write(resultado, databaseType);
			resultado.Append(")\r");

			if (!(ifBranchStatement is BlockStatement))
			{
				tabs++;
			}
			ifBranchStatement.Write(resultado, tabs, databaseType);
			if (!(ifBranchStatement is BlockStatement))
			{
				tabs--;
			}

			if (elseBranchStatement != null && !(elseBranchStatement.FueFiltrado))
			{
				if (tabs > 0) resultado.Append(GenerateTabs(tabs));
				resultado.Append("Else\r");
				if (!(elseBranchStatement is BlockStatement))
				{
					tabs++;
				}
				elseBranchStatement.Write(resultado, tabs, databaseType);
				if (!(elseBranchStatement is BlockStatement))
				{
					tabs--;
				}
			}
		}
	}
}
