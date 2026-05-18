using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class LiteralNull : AstExpression
	{
		internal LiteralNull()
		{
			this.ForcedType = typeof(object);
		}


		internal override object Execute()
		{
			return null;
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			return Expression.Constant(null);
		}

		internal override Type ComputeType()
		{
			return this.ForcedType;
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			patternAst.RegisterLiteral(null, ComputeType(), position);
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			resultado.Append("Null");
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
