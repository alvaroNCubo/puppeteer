using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class LiteralBoolean : AstExpression
	{
		private readonly bool value;

		internal readonly static LiteralBoolean LiteralTrue = new LiteralBoolean(true);
		internal readonly static LiteralBoolean LiteralFalse = new LiteralBoolean(false);

		private LiteralBoolean(bool value)
		{
			this.value = value;
		}

		internal override Type ComputeType()
		{
			return typeof(bool);
		}

		internal override object Execute()
		{
			return value;
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			return Expression.Constant(this.value);
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			patternAst.RegisterLiteral(value, ComputeType(), position);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			resultado.Append(value ? "true" : "false");
		}

	}

}
