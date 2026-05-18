using System;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	internal class PrintStatement : OutputStatementBase
	{
		internal PrintStatement(IEnumerable<PrintStatementIndividual> prints) : base(prints)
		{
		}
	}

	internal class PrintStatementIndividual : OutputStatementIndividual
	{
		internal PrintStatementIndividual(AstExpression expression, String alias) : base(expression, alias, fueFiltrado: true)
		{
		}

		protected override string GetComandoName()
		{
			return "Print";
		}

		protected override Output GetTargetBuffer(ExecutionOutput output)
		{
			return output.PrintBuffer;
		}
	}
}
