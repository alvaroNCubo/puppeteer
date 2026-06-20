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
		internal readonly static LiteralString EMPTY = new LiteralString("");

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
			// Las cuatro ramas comparten el mismo invariante: el Lexer
			// (ProcessStringLiteral) solo reconoce \' y \\ como secuencias
			// de escape dentro de un literal '...'. La rama MySQL antes emitia
			// la ' interna sin escapar (bug reportado), y SQLServer ademas no
			// duplicaba el \. Se unifican MySQL/SQLServer al mismo formato
			// canonico que ya usaba IN_MEMORY/FileSystem para la apostrofe.
			// El handling del \ se preserva por rama para no romper el
			// formato historico que ya pinaban los tests de Parameters.
			if (value == null) throw new ArgumentNullException(nameof(value));

			if (databaseType == DatabaseType.MySQL || databaseType == DatabaseType.SQLServer)
			{
				output.Append('\'');
				foreach (char c in value)
				{
					switch (c)
					{
						case '\'':
							output.Append('\\').Append('\'');
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
			else if (databaseType == DatabaseType.IN_MEMORY || databaseType == DatabaseType.FileSystem)
			{
				output.Append('\'');
				foreach (char c in value)
				{
					switch (c)
					{
						case '\'':
							output.Append('\\').Append('\'');
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
