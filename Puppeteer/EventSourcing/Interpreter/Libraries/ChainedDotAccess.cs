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

		protected internal override Type ComputeInstanceType()
		{
			return instance.ComputeType();
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
			// A chain a.B().C() is a ChainedDotAccess (C) wrapping the prefix
			// (a.B(), a DotAccess/NewInstance). So that ALL calls in the
			// chain are matchable as obligations:
			//   1. recurse into the prefix (instance) — it evaluates first, lower position.
			//   2. recurse into the arguments (nested calls as arguments), args-first.
			//   3. register this call/access of the chain.
			// The receiver of a chained link is an expression without a simple name, so
			// target/targetName are null; the matcher matches by declaring type + method +
			// args (enough for patterns [_:Type].Method(...)). An explicit instance name
			// on a chained link is not correlated (edge case, not in use).
			instance.PreparePatternMatching(patternAst, ref position);

			AstExpression[] nestedArgs = this.Arguments();
			if (nestedArgs != null)
			{
				foreach (AstExpression nestedArg in nestedArgs)
				{
					nestedArg.PreparePatternMatching(patternAst, ref position);
				}
			}

			Type instanceType = instance.ComputeType();
			if (instanceType != null)
			{
				MemberInfo memberInfo = base.GetResolvedMemberInfo(instanceType);
				if (memberInfo != null)
				{
					string memberName = Property() ?? Method();
					patternAst.RegisterMemberAccess(null, memberName, memberInfo, position, instanceType);

					if (memberInfo is MethodInfo methodInfo)
					{
						List<object> argumentValues = base.GetArgumentValues();
						patternAst.RegisterMethodCall(methodInfo, null, argumentValues, position, null, instanceType);
					}

					position++;
				}
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
