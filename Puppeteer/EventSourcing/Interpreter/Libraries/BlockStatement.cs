using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

	using SymbolTable = SymbolTable;

	class BlockStatement : Statement
	{
		private Statement[] statements;
		private readonly SymbolTable symbolTable;

		internal BlockStatement(SymbolTable symbolTable, Statement[] statements)
		{
			this.statements = statements;
			this.symbolTable = symbolTable;
		}

		internal Statement[] Comandos
		{
			get
			{
				return statements;
			}
		}

		internal bool IsEmpty
		{
			get
			{
				return statements.Length == 0;
			}
		}

		internal override void Execute(ExecutionOutput output)
		{
			foreach (Statement source in statements)
			{
				if (Program != null) Program.lastExecutedStatement = source;
				source.Execute(output);
			}
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			var localVars = new List<ParameterExpression>();
			var expressions = new List<Expression>();

			foreach (var cmd in statements)
			{
				if (cmd is NullStatement) continue;

				if (cmd is NewInstanceStatement nuevaInstanciaCmd)
				{
					if (nuevaInstanciaCmd.LValue is Id id && !id.IsParameter && id.IsOriginalLValueDeclaration)
					{
						Expression allocateLocalStorageExpr = nuevaInstanciaCmd.AllocateLocalStorageExpression(parametersParam);
						expressions.Add(allocateLocalStorageExpr);
					}
					else if (nuevaInstanciaCmd.LValue is DottedId idConPunto)
					{
						nuevaInstanciaCmd.AllocateLocalStorageExpression(parametersParam);
					}

					ParameterExpression localVarDeclarationExpr = (ParameterExpression)nuevaInstanciaCmd.LocalStorageExpression;
					if (localVarDeclarationExpr != null) localVars.Add(localVarDeclarationExpr);

				}

				expressions.Add(cmd.ExecuteExpression(parametersParam, outputParam));
			}

			if (expressions.Count == 0)
				return Expression.Empty();

			if (localVars.Count > 0)
				return Expression.Block(localVars, expressions);
			else
				return Expression.Block(expressions);
		}

		internal override void ValidateStatically()
		{
			foreach (Statement source in statements)
			{
				source.ValidateStatically();
			}
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			foreach (Statement source in statements)
			{
				source.PreparePatternMatching(patternAst, ref position);
			}
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));
			resultado.Append("{\r");
			tabs++;
			foreach (Statement source in statements)
			{
				source.Write(resultado, tabs, databaseType);
			}
			tabs--;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));
			resultado.Append("}\r");
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			foreach (Statement source in statements)
			{
				source.Visit(v);
			}
		}

	}

}
