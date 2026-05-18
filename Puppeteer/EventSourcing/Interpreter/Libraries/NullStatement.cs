using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

	class NullStatement : Statement
	{
		private readonly string lineaComentada;

		internal NullStatement(string lineaComentada)
		{
			this.lineaComentada = lineaComentada;
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

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (lineaComentada == ReadOnlySpan<char>.Empty)
			{
				resultado.Append('\r');
			}
			else
			{
				resultado.Append(lineaComentada);
				resultado.Append('\r');
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
