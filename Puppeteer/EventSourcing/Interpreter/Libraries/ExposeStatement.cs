using System;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	internal class ExposeStatement : OutputStatementBase
	{
		internal ExposeStatement(IEnumerable<ExposeStatementIndividual> exposes) : base(exposes)
		{
		}
	}

	internal class ExposeStatementIndividual : OutputStatementIndividual
	{
		internal ExposeStatementIndividual(AstExpression expression, String alias) : base(expression, alias, fueFiltrado: false)
		{
		}

		protected override string GetComandoName()
		{
			return "Expose";
		}

		protected override Output GetTargetBuffer(ExecutionOutput output)
		{
			return output.ExposeBuffer;
		}
	}
}
