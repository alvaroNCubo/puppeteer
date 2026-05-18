using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class LiteralDateTime : AstExpression
	{
		private readonly DateTime value;

		internal LiteralDateTime(DateTime value)
		{
			this.value = value;
		}

		internal override Type ComputeType()
		{
			return typeof(DateTime);
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

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			bool esFecha = value.Hour == 0 && value.Minute == 0 && value.Second == 0;
			string text = esFecha ? value.ToString("MM/dd/yyyy") : value.ToString("MM/dd/yyyy HH:mm:ss");
			resultado.Append(text);
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
