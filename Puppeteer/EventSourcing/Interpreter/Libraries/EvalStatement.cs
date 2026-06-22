using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

	using SymbolTable = SymbolTable;

	class EvalStatement : Statement
	{
		private readonly AstExpression expression;
		private readonly SymbolTable symbolTable;
		private readonly DomainLibraries libraries;
		private string forDairy;
		private readonly Parser parser;
		private readonly int[] currLevel;
		private readonly bool isQuery;
		private readonly bool isCheck;

		internal EvalStatement(DomainLibraries libraries, SymbolTable symbolTable, AstExpression expression, int[] currLevel, bool isQuery, bool isCheck)
		{
			this.expression = expression;
			this.symbolTable = symbolTable;
			this.libraries = libraries ?? throw new ArgumentNullException(nameof(libraries));
			this.parser = new Parser(libraries, symbolTable);
			this.currLevel = currLevel;
			this.isQuery = isQuery;
			this.isCheck = isCheck;
		}

		internal override void Execute(ExecutionOutput output)
		{
			string script = ((string)expression.Execute());
			parser.SetSource(script);
			Program programaEval = parser.ParseEval(currLevel, isQuery, isCheck);
			programaEval.DeclaracionesExternas = this.Program.Declaraciones;
			programaEval.Parameters = this.Program.Parameters;
			programaEval.SolveReferences(programaEval.Parameters, withStaticValidation: false);
			programaEval.SetContextInfo();
			string resultado = programaEval.Execute();
			forDairy = programaEval.ConvertToString(DatabaseType.IN_MEMORY);
			if (resultado != "")
			{
				output.PrintBuffer.Append(resultado, 1, resultado.Length - 2);
			}
			this.Program.DeclaracionesExternas = programaEval.Declaraciones;
			this.Program.SolveReferences(this.Program.Parameters, withStaticValidation: true);
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			throw new LanguageException("Eval is only available for interpreted execution. Use a Eval type parameter for the compiled version instead.");
		}


		internal override void ValidateStatically()
		{
			expression.ValidateStatically();
			Type expressionType = expression.ComputeType();
			if (expressionType != typeof(string))
			{
				throw new LanguageException($"An 'eval' statement can only be executed when its expression is of type string, but found type '{expressionType.Name}'.");
			}
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
		}

		// B.3.1: include the wrapped expression. The actual evaluated body is
		// dynamic and only known at runtime, so the hash captures the static
		// shape of the eval-expression itself (which is what's parsed and
		// journaled as part of the host script).
		internal override void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(nameof(EvalStatement));
			expression.AccumulatePromotionCandidateHash(ref hc);
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (!String.IsNullOrWhiteSpace(forDairy))
			{
				if (tabs > 0) resultado.Append(GenerarTabs(tabs));
				resultado.Append(forDairy);
			}
			else
			{
				// forDairy is set by Execute with the EXPANDED form of the eval (e.g.
				// `id = 1;`). But ActorHandler invokes ConvertToString of the parent
				// program BEFORE Perform — the snapshot goes to the journal while forDairy
				// is null and, without this branch, the assignment synthesized by Eval is
				// lost. Without it, rehydration sees a script with free references
				// to the variable created by Eval (typeof(object)) and static validation
				// throws `Unknown property or method 'X' on type 'Y'.`.
				// When the literal Eval is emitted the replayed AST contains
				// EvalStatement, hasEvals==true in Program.ValidateStatically, and
				// static validation is skipped — replay re-executes the Eval and
				// rebuilds the globals deterministically (same call order, etc.).
				if (tabs > 0) resultado.Append(GenerarTabs(tabs));
				resultado.Append("Eval(");
				expression.write(resultado, databaseType);
				resultado.Append(");\r");
			}
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			expression.Visit(v);
		}
	}

}
