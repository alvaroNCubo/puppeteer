using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// A char literal: `'L'c`. The suffix is what distinguishes it from a string literal, the same
	// device `1L` uses to distinguish a long from an int — and for the same reason, since in script
	// text `'L'` alone lexes as a string.
	//
	// char was admitted as a parameter type before the language could WRITE one, which is what made
	// every char at a call site arrive as a one-character string leaning on an implicit conversion,
	// or wrapped in a cast. Those were never ergonomics; they were the workaround for this missing
	// literal. With the literal, a char argument binds a char parameter EXACTLY, so no conversion
	// stands between the two and no later overload can take the binding away from a journaled entry.
	class LiteralChar : AstExpression
	{
		private readonly char value;

		internal LiteralChar(char value)
		{
			this.value = value;
		}

		internal override Type ComputeType()
		{
			return typeof(char);
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

		internal override void write(StringBuilder output, DatabaseType databaseType)
		{
			// Quoted through the string writer so a char that IS a quote, a comma or a brace is
			// escaped exactly as the string reader expects — the two must not drift apart, since the
			// lexer recognizes one set of escape sequences inside '...'. Then the 'c' suffix, so the
			// canonical text re-lexes as a char and does not degrade to a one-character string on
			// the next parse (journal replay), silently changing its static type.
			LiteralString.Write(output, value.ToString(), databaseType);
			output.Append('c');
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
