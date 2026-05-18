using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class LiteralString : AstExpression
	{
		internal const Char SLASH_OR_SINGLE_QUOTED_CHARACTER = '\u0000'; //Para SQLServer es SINGLE_QUOTED && MySQL es SLASH
		internal const Char DOUBLE_QUOTED_CHARACTER = '\u0001';
		internal const Char PIPE_CHARACTER = '\u0002';
		internal readonly static LiteralString VACIA = new LiteralString("");

		private readonly string value;

		internal LiteralString(string value)
		{
			this.value = value;
		}

		internal override Type ComputeType()
		{
			return typeof(string);
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

		internal override void write(StringBuilder output, DatabaseType databaseType)
		{
			Write(output, value, databaseType);
		}

		internal static void Write(StringBuilder output, string value, DatabaseType databaseType)
		{
			if (databaseType == DatabaseType.MySQL)
			{
				output.Append('\'');
				foreach (char c in value)
				{
					switch (c)
					{
						case '\'':
							output.Append('\'');
							break;
						case '"':
							output.Append('\"');
							break;
						case '\\':
							output.Append('\\').Append('\\');
							break;
						default:
							output.Append(c);
							break;
					}
				}
				output.Append('\'');
			}
			else if (databaseType == DatabaseType.SQLServer)
			{
				output.Append('\'');
				foreach (char c in value)
				{
					switch (c)
					{
						case '\'':
							output.Append('\'');
							break;
						case '"':
							output.Append('"');
							break;
						case '\\':
							output.Append('\\');
							break;
						default:
							output.Append(c);
							break;
					}
				}
				output.Append('\'');
			}
			else if (databaseType == DatabaseType.IN_MEMORY || databaseType == DatabaseType.FileSystem)
			{
				output.Append('\'');
				foreach (char c in value)
				{
					switch (c)
					{
						case '\'':
							output.Append('\\');
							output.Append('\'');
							break;
						default:
							output.Append(c);
							break;
					}
				}
				output.Append('\'');
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
