using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

	class NullStatement : Statement
	{
		private readonly string commentedLine;

		internal NullStatement(string commentedLine)
		{
			this.commentedLine = commentedLine;
		}

		internal NullStatement()
		{
		}

		internal override void Execute(ExecutionOutput output)
		{
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			return Expression.Empty();
		}

		internal override void ValidateStatically()
		{
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
		}

		internal override void Write(StringBuilder result, int tabs, DatabaseType databaseType)
		{
			if (WasFiltered) return;
			if (commentedLine == ReadOnlySpan<char>.Empty)
			{
				result.Append('\r');
			}
			else
			{
				result.Append(commentedLine);
				result.Append('\r');
			}
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
		}
	}

}
