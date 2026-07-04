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
		internal PrintStatementIndividual(AstExpression expression, String alias) : base(expression, alias, wasFiltered: true)
		{
		}

		protected override string GetCommandName()
		{
			return "Print";
		}

		protected override bool PreservedInAuthoredBody => true;

		protected override Output GetTargetBuffer(ExecutionOutput output)
		{
			return output.PrintBuffer;
		}
	}
}
