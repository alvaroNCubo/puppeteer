using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class ChainedDotAccess : DotAccess
	{
		private AstExpression instance;

		internal ChainedDotAccess(DotAccess instance, string method, AstExpression[] arguments) : base(method, arguments)
		{
			this.instance = instance;
		}

		internal ChainedDotAccess(DotAccess instance, string property) : base(property)
		{
			this.instance = instance;
		}

		internal ChainedDotAccess(NewInstance instance, string method, AstExpression[] arguments) : base(method, arguments)
		{
			this.instance = instance;
		}

		internal ChainedDotAccess(NewInstance instance, string property) : base(property)
		{
			this.instance = instance;
		}

		protected internal override object GetTarget()
		{
			object result = instance.Execute();
			return result;
		}

		protected internal override Expression GetTargetExpression(ParameterExpression parametersParam)
		{
			var instanceExpr = instance.ExecuteExpression(parametersParam);
			return instanceExpr;
		}

		internal override Type ComputeType()
		{
			Type instanceClass = instance.ComputeType();
			Type result = ComputeCallExpressionType(instanceClass);
			return result;
		}

		internal override void ValidateStatically()
		{
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			// ChainedDotAccess has a chain of accesses we can extract
			var chain = new List<MemberInfo>();

			throw new NotImplementedException($"{nameof(PreparePatternMatching)} is not yet implemented for {nameof(ChainedDotAccess)}.");

			// For now, only the final type is registered
			var type = ComputeType();
			if (type != null && chain.Count > 0)
			{
				patternAst.RegisterChainedAccess(chain, position);
			}
		}

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			instance.write(result, databaseType);

			result.Append('.');

			if (!string.ReferenceEquals(Property(), null))
			{
				result.Append(Property());
			}
			else
			{
				AstExpression[] arguments = this.Arguments();
				result.Append(Method());
				result.Append('(');
				for (int i = 0; i < arguments.Length; i++)
				{
					if (i > 0)
					{
						result.Append(", ");
					}
					arguments[i].write(result, databaseType);
				}
				result.Append(')');
			}
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			instance.Visit(v);
			base.Visit(v);
		}
	}
}
