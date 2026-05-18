using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class LiteralNumber : AstExpression
	{

		private readonly int value;

		internal LiteralNumber(int value)
		{
			this.value = value;
		}

		internal override Type ComputeType()
		{
			return typeof(int);
		}

		internal override object Execute()
		{
			return value;
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			return Expression.Constant(value);
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			patternAst.RegisterLiteral(value, ComputeType(), position);
		}


		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			resultado.Append(value);
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
