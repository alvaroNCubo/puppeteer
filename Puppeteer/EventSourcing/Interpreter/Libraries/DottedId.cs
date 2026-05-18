using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

	using SymbolTable = SymbolTable;

	class DottedId : DotAccess
	{

		private Id id;
		private readonly SymbolTable symbolTable;

		internal DottedId(SymbolTable symbolTable, Id id, string method, AstExpression[] arguments) : base(method, arguments)
		{
			this.id = id;
			this.symbolTable = symbolTable;
		}

		internal DottedId(SymbolTable symbolTable, Id id, string property) : base(property)
		{
			this.id = id;
			this.symbolTable = symbolTable;
		}

		internal string Id()
		{
			return id.Name;
		}

		internal Type[] RequiredMethodSignature()
		{
			AstExpression[] arguments = this.Arguments();
			Type[] result = new Type[arguments.Length];
			for (int i = 0; i < arguments.Length; i++)
			{
				result[i] = arguments[i].ComputeType();
			}
			return result;
		}

		internal override Type ComputeType()
		{
			Type result = ComputeCallExpressionType(id.ComputeType());
			if (ForcedType == null) ForcedType = result;
			return result;
		}

		protected internal override object GetTarget()
		{
			object value = id.Execute();
			return value;
		}

		protected internal override Expression GetTargetExpression(ParameterExpression parametersParam)
		{
			var value = id.ExecuteExpression(parametersParam);
			return value;
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching] Processing: {id.Name}.{Property() ?? Method()}");

			Type idType = id.ComputeType();
			System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching]   id.ComputeType() returned: {idType?.Name ?? "NULL"}");

			if (idType != null)
			{
				var memberInfo = base.GetResolvedMemberInfo(idType);
				System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching]   GetResolvedMemberInfo returned: {memberInfo?.Name ?? "NULL"} (type: {memberInfo?.GetType().Name ?? "NULL"})");

				if (memberInfo != null)
				{
					string memberName = Property() ?? Method();

					patternAst.RegisterMemberAccess(id.Name, memberName, memberInfo, position);
					System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching]   RegisterMemberAccess called: {id.Name}.{memberName}");

					if (memberInfo is MethodInfo methodInfo)
					{
						object target = null;
						if (id.IsParameter)
						{
							target = id.Execute();
						}

						List<object> argumentValues = base.GetArgumentValues();
						patternAst.RegisterMethodCall(methodInfo, target, argumentValues, position, id.Name);
						System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching]   RegisterMethodCall called: {methodInfo.DeclaringType?.Name}.{methodInfo.Name}");
					}

					position++;
				}
				else
				{
					System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching]   SKIP: memberInfo is NULL");
				}
			}
			else
			{
				System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching]   SKIP: idType is NULL");
			}
		}


		internal Expression RValueReferenceExpression { get; private set; } = null;
		internal Expression LValueStorageExpression { get; private set; } = null;



		internal Expression AllocateStorageExpression(ParameterExpression parametersParam)
		{
			if (!this.id.Program.IsCompiledMode) throw new LanguageException("Cannot generate Expression-based storage in interpreted mode.");
			if (LValueStorageExpression != null) throw new LanguageException($"Storage for the declaration of Id '{id.Name}' has already been generated.");
			if (RValueReferenceExpression != null) throw new LanguageException($"Storage for the declaration of Id '{id.Name}' has already been generated.");

			var instanceType = this.id.ForcedType;

			if (this.Property() != null)
			{
				var fieldInfo = FindField(instanceType);
				var instanceExp = this.id.RValueReferenceExpression;
				if (fieldInfo != null)
				{
					LValueStorageExpression = RValueReferenceExpression = Expression.Field(instanceExp, fieldInfo);
				}
				else
				{
					var propertyGetInfo = FindPropertyByName(instanceType, this.Property(), lookupSetter: false);
					if (propertyGetInfo != null)
					{
						RValueReferenceExpression = Expression.Property(instanceExp, propertyGetInfo);
					}

					var propertySetInfo = FindPropertyByName(instanceType, this.Property(), lookupSetter: true);
					if (propertySetInfo != null)
					{
						LValueStorageExpression = Expression.Property(instanceExp, propertySetInfo);
					}
				}
			}
			else if (this.Method() != null)
			{
				throw new LanguageException($"Cannot assign to a method ('{id.Name}.{this.Method()}').");
			}

			return Expression.Empty();
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			resultado.Append(id.Name);
			resultado.Append('.');

			if (Property() != null)
			{
				resultado.Append(Property());
			}
			else
			{
				AstExpression[] arguments = this.Arguments();
				resultado.Append(Method());
				resultado.Append('(');
				for (int i = 0; i < arguments.Length; i++)
				{
					if (i > 0)
					{
						resultado.Append(", ");
					}
					arguments[i].write(resultado, databaseType);
				}
				resultado.Append(')');

			}
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			id.Visit(v);
			base.Visit(v);
		}
	}

}
