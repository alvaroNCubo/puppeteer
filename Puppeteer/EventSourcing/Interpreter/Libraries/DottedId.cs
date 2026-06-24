using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq;
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
		private readonly DomainLibraries libraries;
		private readonly string targetNamespace;

		private bool staticReceiverResolved = false;
		private Type staticReceiverType = null;

		internal DottedId(DomainLibraries libraries, SymbolTable symbolTable, Id id, string method, AstExpression[] arguments, string targetNamespace = null) : base(method, arguments)
		{
			if (libraries == null) throw new ArgumentNullException(nameof(libraries));
			if (symbolTable == null) throw new ArgumentNullException(nameof(symbolTable));
			if (id == null) throw new ArgumentNullException(nameof(id));

			this.libraries = libraries;
			this.id = id;
			this.symbolTable = symbolTable;
			this.targetNamespace = targetNamespace;
		}

		internal DottedId(DomainLibraries libraries, SymbolTable symbolTable, Id id, string property) : base(property)
		{
			if (libraries == null) throw new ArgumentNullException(nameof(libraries));
			if (symbolTable == null) throw new ArgumentNullException(nameof(symbolTable));
			if (id == null) throw new ArgumentNullException(nameof(id));

			this.libraries = libraries;
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
			Type staticType = ResolveStaticReceiverType();
			Type receiverType = staticType ?? id.ComputeType();
			Type result = ComputeCallExpressionType(receiverType);
			if (ForcedType == null) ForcedType = result;
			return result;
		}

		// Applies the symbol-first / class-fallback rule to decide whether 'Id.Method(args)' is
		// a static call (receiver = registered class) or an instance call (receiver = variable).
		// Returns the class Type for the static case; null for the instance case. It only applies
		// to method calls (with parentheses); static property/field access is not supported.
		protected internal override Type ResolveStaticReceiverType()
		{
			if (Method() == null)
			{
				return null;
			}

			if (staticReceiverResolved)
			{
				return staticReceiverType;
			}

			staticReceiverType = ResolveStaticReceiverTypeUncached();
			staticReceiverResolved = true;
			return staticReceiverType;
		}

		private Type ResolveStaticReceiverTypeUncached()
		{
			// (A) Instance vs class: symbol-first. If the receiver is bound to a
			// variable/parameter/global (or exists as a global in the symbol table), it is an
			// instance call and NEVER static, even if its name matches (case-insensitive)
			// a registered class. This prevents a variable 'obj' from being interpreted as
			// the class 'Obj'.
			bool boundToSymbol = id.HasResolvedScope || symbolTable.HasVariable(id.Name);
			if (boundToSymbol)
			{
				if (targetNamespace != null)
				{
					throw new LanguageException($"The 'in' clause is only valid for static class method calls, but '{id.Name}' resolves to a variable, parameter or global.");
				}
				return null;
			}

			// (Next) class-fallback: the receiver is not a binding; try to resolve it
			// as a class registered in Libraries (case-insensitive).
			if (!libraries.TryFindClassesByName(id.Name, out var classesEnumerable))
			{
				// Neither binding nor registered class: it is not static. The instance / method
				// resolution path will produce the appropriate error (undefined variable, etc.).
				return null;
			}

			List<DomainLibraries.ClassInfo> allCandidates = classesEnumerable.ToList();
			if (allCandidates.Count == 0)
			{
				return null;
			}

			// (B) Namespace homonymy: if the 'in' clause was given, filter by that namespace
			// (same machinery as the Class(args) in Namespace construction).
			List<DomainLibraries.ClassInfo> candidates = allCandidates;
			if (targetNamespace != null)
			{
				candidates = allCandidates
					.Where(ci => string.Equals(ci.Namespace, targetNamespace, StringComparison.OrdinalIgnoreCase))
					.ToList();

				if (candidates.Count == 0)
				{
					string availableNamespaces = string.Join(", ", allCandidates.Select(ci => ci.Namespace).Distinct());
					throw new LanguageException($"Class '{id.Name}' was not found in namespace '{targetNamespace}'. Available namespaces: {availableNamespaces}.");
				}
			}

			// A single candidate (a single namespace): we resolve to that class directly. If the
			// static method does not exist, ComputeCallExpressionType/InvokeStaticMethod will report it.
			if (candidates.Count == 1)
			{
				return candidates[0].Type;
			}

			// Several candidates by homonymy: disambiguate by which one declares a static method with
			// a compatible signature. If exactly one class matches, resolved; if several, ambiguous.
			Type[] signature = RequiredMethodSignature();
			List<Type> matching = new List<Type>();
			foreach (DomainLibraries.ClassInfo classInfo in candidates)
			{
				MethodInfo candidateMethod = FindMethod(classInfo.Type, signature, out _, staticReceiver: true);
				if (candidateMethod != null)
				{
					matching.Add(classInfo.Type);
				}
			}

			List<Type> distinctMatching = matching.Distinct().ToList();
			if (distinctMatching.Count == 1)
			{
				return distinctMatching[0];
			}

			if (distinctMatching.Count > 1)
			{
				string namespaces = string.Join(", ", distinctMatching.Select(t => t.Namespace ?? string.Empty).Distinct());
				throw new LanguageException($"Ambiguous reference: class '{id.Name}' with a static method '{Method()}' matching the given signature exists in multiple namespaces: {namespaces}. Use the 'in' clause to specify the namespace (e.g. {id.Name}.{Method()}(...) in MyNamespace).");
			}

			// No candidate declares the static method with that signature, but the class exists in
			// several namespaces: ask for 'in' to narrow it and get the precise namespace error.
			string allNamespaces = string.Join(", ", candidates.Select(ci => ci.Namespace).Distinct());
			throw new LanguageException($"Ambiguous reference: class '{id.Name}' exists in multiple namespaces: {allNamespaces}, and no static method '{Method()}' with a compatible signature was found to disambiguate. Use the 'in' clause to specify the namespace (e.g. {id.Name}.{Method()}(...) in MyNamespace).");
		}

		protected internal override object GetTarget()
		{
			object value = id.Execute();
			return value;
		}

		protected internal override Type ComputeInstanceType()
		{
			return id.ComputeType();
		}

		protected internal override Expression GetTargetExpression(ParameterExpression parametersParam)
		{
			var value = id.ExecuteExpression(parametersParam);
			return value;
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching] Processing: {id.Name}.{Property() ?? Method()}");

			// Recurse into the arguments FIRST, so that a nested call as an
			// argument (e.g. transform($x) used as a parameter) is registered and
			// matchable as an obligation. Same args-first order as
			// NewInstance.PreparePatternMatching: nested calls receive a
			// position lower than the outer call. Literals/Ids self-register
			// without moving 'position'; only methods/constructors increment it.
			AstExpression[] nestedArgs = this.Arguments();
			if (nestedArgs != null)
			{
				foreach (AstExpression nestedArg in nestedArgs)
				{
					nestedArg.PreparePatternMatching(patternAst, ref position);
				}
			}

			Type staticType = ResolveStaticReceiverType();
			Type idType = staticType ?? id.ComputeType();
			System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching]   receiver type resolved: {idType?.Name ?? "NULL"}");

			if (idType != null)
			{
				var memberInfo = base.GetResolvedMemberInfo(idType);
				System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching]   GetResolvedMemberInfo returned: {memberInfo?.Name ?? "NULL"} (type: {memberInfo?.GetType().Name ?? "NULL"})");

				if (memberInfo != null)
				{
					string memberName = Property() ?? Method();

					patternAst.RegisterMemberAccess(id.Name, memberName, memberInfo, position, idType);
					System.Diagnostics.Debug.WriteLine($"[DottedId.PreparePatternMatching]   RegisterMemberAccess called: {id.Name}.{memberName}");

					if (memberInfo is MethodInfo methodInfo)
					{
						object target = null;
						if (id.IsParameter)
						{
							target = id.Execute();
						}

						List<object> argumentValues = base.GetArgumentValues();
						patternAst.RegisterMethodCall(methodInfo, target, argumentValues, position, id.Name, idType);
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
