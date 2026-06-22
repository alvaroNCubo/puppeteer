using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class Id : AstExpression
	{
		private readonly string name;
		private readonly SymbolTable symbolTable;
		private readonly int[] level;
		private VariableSymbol symbol = null;
		private Program program = null;
		private Scope scope = Scope.Undefined;
		private ValueCategory valueCategory = ValueCategory.Undefined;

		internal Id(SymbolTable symbolTable, string name, int[] level)
		{
			this.symbolTable = symbolTable;
			this.name = name;
			this.level = level;
		}

		internal string Name
		{
			get
			{
				return name;
			}
		}

		internal bool IsGlobalVariable
		{
			get
			{
				return this.scope == Scope.Global ? true : false;
			}
		}

		internal bool IsParameter
		{
			get
			{
				return this.scope == Scope.Parameter ? true : false;
			}
		}

		internal bool IsNullableParameter
		{
			get
			{
				return this.scope == Scope.Parameter && this.parameter != null && this.parameter.IsNullable;
			}
		}

		internal bool IsLocalVariable
		{
			get
			{
				return this.scope == Scope.Local ? true : false;
			}
		}

		// True when reference resolution has already bound this Id to a symbol
		// (parameter, local variable, or global). False if its scope is still Undefined,
		// which is the case of an identifier that does not correspond to any binding and
		// can therefore be treated as the name of a registered class
		// (symbol-first / class-fallback rule for static method calls).
		internal bool HasResolvedScope
		{
			get
			{
				return this.scope != Scope.Undefined;
			}
		}

		internal void MarkAsLValue()
		{
			if (this.valueCategory == ValueCategory.RValue)
			{
				throw new LanguageException($"Cannot assign a value to '{name}' because it is an RValue.");
			}
			this.valueCategory = ValueCategory.LValue;
		}

		internal void MarkAsRValue()
		{
			if (this.valueCategory == ValueCategory.LValue)
			{
				throw new LanguageException($"Cannot mark '{name}' as RValue because it is already an LValue.");
			}
			this.valueCategory = ValueCategory.RValue;
		}

		internal bool IsLValue
		{
			get
			{
				return this.valueCategory == ValueCategory.LValue;
			}
		}


		internal bool IsReferencedBy(Id aReference)
		{
			if (this == aReference) return false;

			if (!string.Equals(this.name, aReference.name, StringComparison.OrdinalIgnoreCase)) return false;

			Id declaration = this;

			if (declaration.level == aReference.level) return true;
			if (declaration.Level > aReference.Level) return false;
			int min = this.level.Length;
			for (int i = 0; i < min; i++)
			{
				if (declaration.level[i] != aReference.level[i]) return false;
			}
			return true;
		}

		internal int Level
		{
			get
			{
				return level.Length;
			}
		}

		internal void DeclareAsLocalVariable()
		{
			if (this.scope == Scope.Undefined)
			{
				if (!this.Program.IsCompiledMode)
				{
					this.symbol = SymbolTable.IsolatedStorage(this.name, null, null);
				}

				this.scope = Scope.Local;
			}
		}

		internal void DeclareAsGlobalVariable()
		{
			if (this.scope == Scope.Undefined)
			{
				if (!this.Program.IsCompiledMode)
				{
					if (!symbolTable.HasVariable(this.name))
					{
						symbolTable.SetVariable(this.name, null, null);
					}
					this.symbol = symbolTable.Entry(this.name);
				}
				else
				{
					if (symbolTable.HasVariable(this.name))
					{
						this.symbol = symbolTable.Entry(this.name);
					}
				}

				this.scope = Scope.Global;
			}
		}

		private Parameter parameter;

		internal void DeclareAsLocalParameter(Parameter parameter)
		{
			ArgumentNullException.ThrowIfNull(parameter);

			if (this.scope == Scope.Undefined)
			{
				if (this.parameter != null && this.parameter != parameter) throw new LanguageException($"Id '{Name}' of kind Parameter is already associated with parameter '{parameter.Name}'.");

				this.parameter = parameter;

				if (!this.Program.IsCompiledMode)
				{
					this.symbol = parameter.AssociateSimbol();
				}

				this.scope = Scope.Parameter;
			}
			else if (this.scope == Scope.Parameter && !this.Program.IsCompiledMode && String.Equals(this.parameter.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))
			{
				this.symbol = parameter.AssociateSimbol();
			}
		}

		internal void ReleaseLocalParameter()
		{
			if (this.scope != Scope.Parameter) throw new LanguageException($"Cannot release the Parameter binding of Id '{name}' because its kind is '{this.scope}'.");
			if (this.parameter == null) throw new LanguageException($"Cannot release the Parameter binding of Id '{name}' because it is not associated with any parameter.");

			this.parameter = null;
		}

		private Expression rValueReferenceExpression = null;
		private Expression lValueStorageExpression = null;

		internal Expression RValueReferenceExpression
		{
			get
			{
				if (rValueReferenceExpression != null) return rValueReferenceExpression;

				if (myDeclaration != null) return myDeclaration.RValueReferenceExpression;

				return null;
			}
			private set
			{
				rValueReferenceExpression = value;
			}
		}

		internal Expression LValueStorageExpression
		{
			get
			{
				if (lValueStorageExpression != null) return lValueStorageExpression;

				if (myDeclaration != null) return myDeclaration.LValueStorageExpression;

				return null;
			}
			private set
			{
				lValueStorageExpression = value;
			}
		}


		private Expression AllocateLocalStorageExpression()
		{
			if (this.scope != Scope.Local) throw new LanguageException($"Cannot create local storage for variable '{name}' because its kind is '{this.scope}', not Local.");
			if (LValueStorageExpression != null) throw new LanguageException($"Local storage for variable '{name}' has already been created.");
			if (RValueReferenceExpression != null) throw new LanguageException($"Local storage for variable '{name}' has already been created.");

			var symbolVar = Expression.Variable(typeof(VariableSymbol), $"_$_isolated_{name}_storage");

			var nameExpr = Expression.Constant(name, typeof(string));
			var nullObjExpr = Expression.Constant(null, typeof(object));
			var objectTypeExpr = Expression.Constant(this.ForcedType, typeof(Type));

			var newIsolatedStorage = typeof(VariableSymbol).GetConstructor(
				BindingFlags.Instance | BindingFlags.NonPublic,
				null,
				new[] { typeof(string), typeof(object), typeof(Type) },
				null
			);
			var createSymbolExpr = Expression.New(newIsolatedStorage, nameExpr, nullObjExpr, objectTypeExpr);

			LValueStorageExpression = symbolVar;

			{
				var valueField = typeof(VariableSymbol).GetField(
					nameof(VariableSymbol.value),
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly
				);

				var valueExpr = Expression.Field(symbolVar, valueField);

				this.RValueReferenceExpression = Expression.Convert(valueExpr, this.ForcedType);
			}

			var result = Expression.Assign(symbolVar, createSymbolExpr);

			return result;
		}

		private Expression AllocateGlobalStorageExpression(bool useLValueReference)
		{
			if (this.scope != Scope.Global) throw new LanguageException($"Cannot create global storage for variable '{name}' because its kind is '{this.scope}', not Global.");
			if (LValueStorageExpression != null) throw new LanguageException($"Global storage for variable '{name}' has already been associated.");
			if (RValueReferenceExpression != null) throw new LanguageException($"Global storage for variable '{name}' has already been associated.");
			if (this.ForcedType == null) throw new LanguageException($"Type was not found for global variable '{name}'.");

			var symbolTableExpr = Expression.Constant(symbolTable);
			var nameExpr = Expression.Constant(this.name);
			var nullObjExpr = Expression.Constant(null, typeof(object));
			var typeExpr = Expression.Constant(this.ForcedType, typeof(Type));

			// One ParameterExpression per global name, shared across every Id
			// occurrence in this Program. Without this sharing, each occurrence
			// would create a distinct ParameterExpression with the same Name,
			// and only one would end up declared in the outer Block (via the
			// GroupBy-First filter in ProgramExpression). The LambdaCompiler matches
			// variables by reference identity, so the rest would fail with
			// "referenced from scope '', but it is not defined".
			ParameterExpression symbolVar;
			bool isFirstAllocation = !this.Program.GlobalStorageByName.TryGetValue(this.name, out symbolVar);
			if (isFirstAllocation)
			{
				symbolVar = Expression.Variable(typeof(VariableSymbol), $"_$_global_{name}_storage");
				this.Program.GlobalStorageByName[this.name] = symbolVar;
			}

			var setVariableMethod = typeof(SymbolTable).GetMethod(
				nameof(SymbolTable.SetVariable),
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
				null,
				new[] { typeof(string), typeof(object), typeof(Type) },
				null
			);

			var entryMethod = typeof(SymbolTable).GetMethod(
				nameof(SymbolTable.Entry),
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
				null,
				new[] { typeof(string) },
				null
			);

			var entryCall = Expression.Call(symbolTableExpr, entryMethod, nameExpr);

			var hasVariableMethod = typeof(SymbolTable).GetMethod(
				nameof(SymbolTable.HasVariable),
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
				null,
				new[] { typeof(string) },
				null
			);

			var hasVariableCall = Expression.Call(symbolTableExpr, hasVariableMethod, nameExpr);

			var ifNotInitialized = Expression.IfThen(
				Expression.IsFalse(hasVariableCall),
				Expression.Call(symbolTableExpr, setVariableMethod, nameExpr, nullObjExpr, typeExpr)
			);

			this.LValueStorageExpression = symbolVar;

			{
				var valueField = typeof(VariableSymbol).GetField(
					nameof(VariableSymbol.value),
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly
				);

				var valueExpr = Expression.Field(entryCall, valueField);

				Type targetType = this.ForcedType;
				Expression convertedExpr;

				if (targetType == typeof(int))
				{
					convertedExpr = Expression.Call(typeof(Convert).GetMethod(nameof(Convert.ToInt32), new[] { typeof(object) }), valueExpr);
				}
				else if (targetType == typeof(double))
				{
					convertedExpr = Expression.Call(typeof(Convert).GetMethod(nameof(Convert.ToDouble), new[] { typeof(object) }), valueExpr);
				}
				else if (targetType == typeof(decimal))
				{
					convertedExpr = Expression.Call(typeof(Convert).GetMethod(nameof(Convert.ToDecimal), new[] { typeof(object) }), valueExpr);
				}
				else if (targetType == typeof(bool))
				{
					convertedExpr = Expression.Call(typeof(Convert).GetMethod(nameof(Convert.ToBoolean), new[] { typeof(object) }), valueExpr);
				}
				else if (targetType == typeof(DateTime))
				{
					convertedExpr = Expression.Call(typeof(Convert).GetMethod(nameof(Convert.ToDateTime), new[] { typeof(object) }), valueExpr);
				}
				else
				{
					convertedExpr = Expression.Convert(valueExpr, targetType);
				}

				this.RValueReferenceExpression = convertedExpr;
			}

			// Only the first occurrence per global name emits the symbol-table
			// init + Assign(symbolVar, entryCall). Later occurrences just
			// return the LValue/RValue expression — the canonical block has
			// already initialized the shared symbolVar by the time the lambda
			// body reaches them (statements execute in source order).
			if (isFirstAllocation)
			{
				return Expression.Block(
					ifNotInitialized,
					Expression.Assign(symbolVar, entryCall),
					(useLValueReference ? symbolVar : RValueReferenceExpression)
				);
			}
			return useLValueReference ? (Expression)symbolVar : RValueReferenceExpression;
		}


		private Expression AllocateParameterStorageExpression(ParameterExpression parametersParam, bool useLValueReference)
		{
			if (this.scope != Scope.Parameter) throw new LanguageException($"Cannot create parameter storage for variable '{name}' because its kind is '{this.scope}', not Parameter.");

			if (LValueStorageExpression != null) throw new LanguageException($"Parameter storage for variable '{name}' has already been associated.");
			if (RValueReferenceExpression != null) throw new LanguageException($"Parameter storage for variable '{name}' has already been associated.");

			Expression result;
			if (parameter.LValueStorageExpression == null && parameter.RValueReferenceExpression == null)
			{
				result = parameter.AllocateParameterStorageExpression(parametersParam, useLValueReference);
			}
			else
			{
				result = useLValueReference ? parameter.LValueStorageExpression : parameter.RValueReferenceExpression;
			}

			LValueStorageExpression = parameter.LValueStorageExpression;
			RValueReferenceExpression = parameter.RValueReferenceExpression;

			return result;
		}

		internal Parameter Parameter
		{
			get
			{
				if (this.scope != Scope.Parameter) throw new LanguageException($"Id '{name}' is not a Parameter; its kind is '{this.scope}'.");
				if (this.parameter == null) throw new LanguageException($"Parameter-kind Id '{name}' has not yet been associated with a passed-in parameter value.");

				return this.parameter;
			}
		}

		internal Expression AllocateStorageExpression(ParameterExpression parametersParam, bool useLValueReference)
		{
			if (!this.Program.IsCompiledMode) throw new LanguageException("Cannot generate Expression-based storage in interpreted mode.");
			if (LValueStorageExpression != null) throw new LanguageException($"Storage for the declaration of Id '{name}' has already been generated.");
			if (RValueReferenceExpression != null) throw new LanguageException($"Storage for the declaration of Id '{name}' has already been generated.");

			Expression result;

			if (this.scope == Scope.Global)
			{
				result = AllocateGlobalStorageExpression(useLValueReference);
			}
			else if (this.scope == Scope.Local)
			{
				result = AllocateLocalStorageExpression();
			}
			else if (this.scope == Scope.Parameter)
			{
				result = AllocateParameterStorageExpression(parametersParam, useLValueReference);
			}
			else
			{
				throw new LanguageException($"Cannot generate an Expression for Id '{name}' because its scope is undefined.");
			}

			return result;
		}

		internal override Type ComputeType()
		{
			var forcedType = this.ForcedType;

			if (forcedType != null)
			{
				return forcedType;
			}
			if (this.symbol != null)
			{
				if (this.symbol.type != null) return this.symbol.type;

				var value = this.symbol.value;
				return (value == null) ? null : value.GetType();
			}
			if (this.myDeclaration != null && this.myDeclaration != this)
			{
				var value = this.myDeclaration.ComputeType();
				return value;
			}
			if (IsParameter && this.parameter != null)
			{
				return this.parameter.ParameterType;
			}
			else if (symbolTable.HasVariable(name))
			{
				var symbol = symbolTable.Entry(name);
				return symbol.type;
			}
			else
			{
				return typeof(object);
			}
		}

		private Id myDeclaration = null;

		internal bool IsOriginalLValueDeclaration
		{
			get
			{
				if (!IsLValue) throw new LanguageException($"Id {name} is not a RValue. {nameof(IsOriginalLValueDeclaration)} is only for LValues");
				return IsLValue && myDeclaration == null;
			}
		}

		internal void ReferencesTo(Id other)
		{
			if (myDeclaration != null && myDeclaration != other) throw new LanguageException($"Id {Name} references ambigous declaration {myDeclaration.Name}");

			this.scope = other.scope;
			this.symbol = other.symbol;
			myDeclaration = other;
		}

		internal override Type ForcedType
		{
			set
			{
				base.ForcedType = value;
				if (this.myDeclaration != null && this.myDeclaration.symbol != null && this.myDeclaration.symbol.type != value)
				{
					throw new LanguageException($"Id {name} is already set as {this.myDeclaration.symbol.type} type. It can not be also {value} type");
				}
				if (this.symbol != null)
				{
					if (this.symbol.type == null)
					{
						this.symbol.type = value;
					}
					else if (this.symbol.type != value)
					{
						throw new LanguageException($"Id {name} is already set as {this.symbol.type} type. It can not be also {value} type");
					}
				}
			}
			get
			{
				if (base.ForcedType == null && this.myDeclaration != null)
				{
					base.ForcedType = this.myDeclaration.ForcedType;
				}
				return base.ForcedType;
			}
		}

		internal Program Program
		{
			get
			{
				if (program == null) throw new LanguageException($"Program associated with Id '{name}' has not been assigned");

				return program;
			}
			set
			{
				if (value == null) throw new LanguageException($"Program associated with Id '{name}' cannot be null.");

				program = value;
			}
		}

		private enum Scope { Local, Global, Parameter, Undefined }
		private enum ValueCategory { LValue, RValue, Undefined }

		internal override object Execute()
		{
			if (this.symbol == null)
			{
				this.symbol = symbolTable.Entry(name);
				if (this.symbol != null) this.scope = Scope.Global;
			}
			if (this.symbol == null)
			{
				throw new LanguageException($"Variable '{name}' has not been defined. Verify that you declared the variable and check for typos.");
			}

			object value = this.symbol.value;
			return value;
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			Expression result;
			if (this.symbol == null && symbolTable.HasVariable(name) && this.scope == Scope.Undefined)
			{
				this.scope = Scope.Global;
			}

			if (this.LValueStorageExpression == null && this.RValueReferenceExpression == null)
			{
				var representative = myDeclaration != null ? myDeclaration : this;

				if (representative.LValueStorageExpression == null && representative.RValueReferenceExpression == null)
				{
					result = representative.AllocateStorageExpression(parametersParam, useLValueReference: this.IsLValue);
				}
				else
				{
					result = this.IsLValue ? representative.LValueStorageExpression : representative.RValueReferenceExpression;
				}

				this.LValueStorageExpression = representative.LValueStorageExpression;
				this.RValueReferenceExpression = representative.RValueReferenceExpression;
			}
			else if (this.IsLValue)
			{
				result = this.LValueStorageExpression;
			}
			else
			{
				result = this.RValueReferenceExpression;
			}

			return result;
		}

		internal void Store(object value, Type type)
		{
			if (this.symbol == null)
			{
				symbolTable.SetVariable(name, value, type);
				this.symbol = symbolTable.Entry(name);
			}
			else
			{
				this.symbol.value = value;
				// The declared (static) type is assigned only once — normally from
				// the ForcedType setter during ValidateStatically. If Execute were
				// to overwrite it with value.GetType() (the concrete runtime type),
				// the next PerformCmd would resolve the global with that concrete
				// type as the lValue's ForcedType and reject every assignment that
				// is compatible with the originally declared type. The runtime
				// value remains inspectable via this.symbol.value.
				if (this.symbol.type == null)
				{
					if (type != null)
					{
						this.symbol.type = type;
					}
					else if (value != null)
					{
						this.symbol.type = value.GetType();
					}
				}
			}
		}

		// B.3.1: include the identifier name. Two scripts that differ only in
		// the name of a local (e.g. `orden` vs `pedido`) hash to distinct
		// promotion candidates; the walker (B.3.2) can later normalize names
		// if needed, but for B.3.1 the conservative choice is to treat
		// distinct names as distinct candidates.
		internal override void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(nameof(Id));
			hc.Add(name ?? string.Empty);
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			// Always use ComputeType(), which knows how to resolve the type for both parameters and variables
			Type type = ComputeType();

			// If it's a parameter, register it as a ScriptParameterIdentifier
			if (IsParameter && this.parameter != null)
			{
				patternAst.RegisterParameterIdentifier(name, type, position, this.parameter);
			}
			else
			{
				patternAst.RegisterIdentifier(name, type, position);
			}
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			resultado.Append(name);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
		}

	}

}
