using Puppeteer.EventSourcing.Follower;
using System;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class LiteralDouble : AstExpression
	{
		private readonly double value;
		private static NumberFormatInfo customFormat;

		static LiteralDouble()
		{
			CultureInfo original = CultureInfo.GetCultureInfo("en-US");
			customFormat = (NumberFormatInfo)original.NumberFormat.Clone();
			customFormat.NumberDecimalSeparator = ".";
		}

		internal LiteralDouble(double value)
		{
			this.value = value;
		}

		internal override Type ComputeType()
		{
			return typeof(double);
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

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			string text = value.ToString("0.######################");
			resultado.Append(text);
			if (text.IndexOf('.') == -1) resultado.Append(".0");
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
