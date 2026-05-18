using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter
{
	class OpEval : AstExpression
	{
		private readonly SymbolTable symbolTable;
		private readonly AstExpression e;

		internal OpEval(SymbolTable symbolTable, AstExpression e)
		{
			this.symbolTable = symbolTable;
			this.e = e;
		}

		internal override Type ComputeType()
		{
			return e.ComputeType();
		}

		internal override void ValidateStatically()
		{
			Type type = e.ComputeType();
			if (type != typeof(string))
			{
				throw new LanguageException($"The expression passed to 'eval' must be of type string, but got type '{type.Name}'.");
			}
		}

		internal override object Execute()
		{
			string nombreVariable = (string)e.Execute();
			object value = symbolTable.Value(nombreVariable);
			return value;
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			throw new LanguageException("Eval is only available for interpreted execution. Use a Eval type parameter for the compiled version instead.");
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			resultado.Append("Eval(");
			e.write(resultado, databaseType);
			resultado.Append(')');
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			e.Visit(v);
		}

	}
}
