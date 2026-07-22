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

		internal abstract void Write(StringBuilder result, int tabs, DatabaseType databaseType);

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

		// Propagate the owning Program backref to this statement and, for a
		// container statement, to every statement nested in its body. Program.
		// SetContextInfo sets the backref only on the TOP-LEVEL statements, so a
		// statement nested in an if/foreach/block body would otherwise keep a null
		// Program. Anything that reads Statement.Program at execution needs it at
		// any depth — e.g. a `tell` nested in an `if` reads Program.Parameters to
		// resolve a captured @-argument by name; without the backref the capture
		// fails to resolve. The base sets its own backref; container statements
		// override to recurse, mirroring the recursion their Visit already performs.
		internal virtual void PropagateProgram(Program program)
		{
			this.Program = program;
		}

		public override string ToString()
		{
			StringBuilder builder = new StringBuilder();
			Write(builder, 0, DatabaseType.IN_MEMORY);
			return builder.ToString();
		}

		internal bool WasFiltered { get; set; } = false;
		internal void FiltrarQueries()
		{
			WasFiltered = true;
		}

	}

}
