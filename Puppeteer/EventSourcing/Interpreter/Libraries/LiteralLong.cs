using Puppeteer.EventSourcing.Follower;
using System;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class LiteralLong : AstExpression
	{
		private readonly long value;

		internal LiteralLong(long value)
		{
			this.value = value;
		}

		internal override Type ComputeType()
		{
			return typeof(long);
		}

		internal override object Execute()
		{
			return this.value;
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			return Expression.Constant(this.value);
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			patternAst.RegisterLiteral(value, ComputeType(), position);
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			// Re-emit the 'L' suffix so the canonical text re-lexes as a long literal;
			// without it the value would degrade to int on the next parse (e.g. journal
			// replay), silently changing its static type.
			result.Append(value.ToString(CultureInfo.InvariantCulture));
			result.Append('L');
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
