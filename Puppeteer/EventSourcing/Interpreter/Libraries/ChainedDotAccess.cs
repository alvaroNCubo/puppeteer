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
			// Una cadena a.B().C() es ChainedDotAccess (C) envolviendo el prefijo
			// (a.B(), un DotAccess/NewInstance). Para que TODAS las llamadas de la
			// cadena sean matcheables como obligaciones:
			//   1. recursar en el prefijo (instance) — se evalua antes, position menor.
			//   2. recursar en los argumentos (llamadas anidadas como argumento), args-first.
			//   3. registrar esta llamada/acceso de la cadena.
			// El receiver de un eslabon encadenado es una expresion sin nombre simple, asi
			// que target/targetName van nulos; el matcher casa por tipo declarante + metodo +
			// args (suficiente para patrones [_:Tipo].Metodo(...)). Un instance name explicito
			// sobre un eslabon encadenado no se correlaciona (caso de borde fuera de uso).
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
					patternAst.RegisterMemberAccess(null, memberName, memberInfo, position);

					if (memberInfo is MethodInfo methodInfo)
					{
						List<object> argumentValues = base.GetArgumentValues();
						patternAst.RegisterMethodCall(methodInfo, null, argumentValues, position, null);
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
