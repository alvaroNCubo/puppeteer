using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

	internal abstract class Statement : AST
	{
		private Program program;

		internal abstract void Execute(ExecutionOutput output);

		internal abstract Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam);

		internal abstract void ValidateStatically();

		internal abstract void Write(StringBuilder resultado, int tabs, DatabaseType databaseType);

		internal Program Program
		{
			set
			{
				if (value == null) throw new LanguageException("The Program associated with a statement cannot be null.");
				this.program = value;
			}

			get
			{
				return program;
			}
		}

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			Write(builder, 0, DatabaseType.IN_MEMORY);
			return builder.ToString();
		}

		internal bool FueFiltrado { get; set; } = false;
		internal void FiltrarQueries()
		{
			FueFiltrado = true;
		}

	}

}
