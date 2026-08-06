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

		internal Statement[] Commands
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

				if (cmd is NewInstanceStatement newInstanceCmd)
				{
					// Every assignment goes through the allocation: the statement is the
					// authority on whether its target needs storage created first, so this
					// block does not test the target's shape. Only a variable declaration
					// creates anything; a member assignment (obj.Member = rValue) or a
					// subscript assignment (coll[i] = rValue) writes to a location that
					// already exists and yields an empty expression here.
					Expression allocateLocalStorageExpr = newInstanceCmd.AllocateLocalStorageExpression(parametersParam);
					expressions.Add(allocateLocalStorageExpr);

					ParameterExpression localVarDeclarationExpr = (ParameterExpression)newInstanceCmd.LocalStorageExpression;
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


		internal override void Write(StringBuilder result, int tabs, DatabaseType databaseType)
		{
			if (WasFiltered) return;
			if (tabs > 0) result.Append(GenerateTabs(tabs));
			result.Append("{\r");
			tabs++;
			foreach (Statement source in statements)
			{
				source.Write(result, tabs, databaseType);
			}
			tabs--;
			if (tabs > 0) result.Append(GenerateTabs(tabs));
			result.Append("}\r");
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

		internal override void PropagateProgram(Program program)
		{
			base.PropagateProgram(program);
			foreach (Statement source in statements)
			{
				source.PropagateProgram(program);
			}
		}

	}

}
