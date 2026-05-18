using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class Parenthesis : AstExpression
	{
		private AstExpression e;

		internal Parenthesis(AstExpression e)
		{
			this.e = e;
		}

		internal AstExpression AstExpression => e;

		internal override Type ComputeType()
		{
			return e.ComputeType();
		}

		internal override object Execute()
		{
			var result = e.Execute();
			return result;
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			return e.ExecuteExpression(parametersParam);
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			e.PreparePatternMatching(patternAst, ref position);
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			resultado.Append('(');
			e.write(resultado, databaseType);
			resultado.Append(')');
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			e.Visit(v);
		}

	}
}
