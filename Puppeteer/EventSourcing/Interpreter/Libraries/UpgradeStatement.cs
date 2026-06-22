using Puppeteer.EventSourcing.Follower;
using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class UpgradeStatement : Statement
	{
		private readonly string upgradeName;
		private readonly Statement body;
		private readonly SymbolTable symbolTable;

		internal UpgradeStatement(SymbolTable symbolTable, string upgradeName, Statement body)
		{
			ArgumentNullException.ThrowIfNull(symbolTable);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(upgradeName);
			ArgumentNullException.ThrowIfNull(body);

			this.symbolTable = symbolTable;
			this.upgradeName = upgradeName;
			this.body = body;
		}

		internal string UpgradeName => upgradeName;

		internal Statement Body => body;

		// Canonical signature of the upgrade body, used to detect silent edits
		// of an upgrade already applied to the actor. Always computed with IN_MEMORY so
		// the signature is independent of the persistence backend.
		private string ComputeBodySignature()
		{
			StringBuilder sb = new StringBuilder();
			body.Write(sb, 0, DatabaseType.IN_MEMORY);
			return sb.ToString();
		}

		internal override void Execute(ExecutionOutput output)
		{
			string signature = ComputeBodySignature();

			if (symbolTable.IsUpgradeApplied(upgradeName))
			{
				symbolTable.ValidateUpgradeSignature(upgradeName, signature);
				return;
			}

			body.Execute(output);
			symbolTable.MarkUpgradeApplied(upgradeName, signature);
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			string signature = ComputeBodySignature();

			var tablaExpr = Expression.Constant(symbolTable);
			var nameExpr = Expression.Constant(upgradeName, typeof(string));
			var signatureExpr = Expression.Constant(signature, typeof(string));

			var isAppliedMethod = typeof(SymbolTable).GetMethod(
				nameof(SymbolTable.IsUpgradeApplied),
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
				null,
				new[] { typeof(string) },
				null
			);

			var validateMethod = typeof(SymbolTable).GetMethod(
				nameof(SymbolTable.ValidateUpgradeSignature),
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
				null,
				new[] { typeof(string), typeof(string) },
				null
			);

			var markMethod = typeof(SymbolTable).GetMethod(
				nameof(SymbolTable.MarkUpgradeApplied),
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
				null,
				new[] { typeof(string), typeof(string) },
				null
			);

			Expression isAppliedCall = Expression.Call(tablaExpr, isAppliedMethod, nameExpr);
			Expression validateCall = Expression.Call(tablaExpr, validateMethod, nameExpr, signatureExpr);
			Expression markCall = Expression.Call(tablaExpr, markMethod, nameExpr, signatureExpr);

			Expression bodyExpr = body.ExecuteExpression(parametersParam, outputParam);

			Expression resultado = Expression.IfThenElse(
				isAppliedCall,
				validateCall,
				Expression.Block(bodyExpr, markCall)
			);

			return resultado;
		}

		internal override void ValidateStatically()
		{
			body.ValidateStatically();
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			body.PreparePatternMatching(patternAst, ref position);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			body.Visit(v);
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));
			resultado.Append("upgrade('");
			resultado.Append(upgradeName);
			resultado.Append("')\r");

			if (!(body is BlockStatement))
			{
				tabs++;
			}
			body.Write(resultado, tabs, databaseType);
			if (!(body is BlockStatement))
			{
				tabs--;
			}
		}
	}
}
