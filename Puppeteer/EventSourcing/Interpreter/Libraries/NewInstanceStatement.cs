using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Interpreter.Utils;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{

	class NewInstanceStatement : Statement
	{
		private readonly AstExpression lValue;
		private readonly AstExpression rValue;
		private readonly SymbolTable symbolTable;

		internal NewInstanceStatement(SymbolTable symbolTable, AstExpression lValue, AstExpression rValue)
		{
			this.lValue = lValue;
			this.rValue = rValue;
			this.symbolTable = symbolTable;
		}

		internal AstExpression LValue
		{
			get
			{
				return lValue;
			}
		}

		internal AstExpression RValue
		{
			get
			{
				return rValue;
			}
		}

		internal override void Execute(ExecutionOutput output)
		{
			// Every member assignment shape goes through the same DotAccess contract: a
			// DottedId (obj.Member, receiver named by an Id) and a ChainedDotAccess
			// (obj.Inner.Member, or Class(args).Member — a receiver that is itself an
			// expression) both resolve their receiver via GetTarget and name the member via
			// Property. Dispatching on the base type keeps the two shapes on one code path.
			if (lValue is DotAccess reference)
			{
				RejectMethodCallAsLValue(reference);

				object value = reference.GetTarget();
				object rightExpressionValue = rValue.Execute();

				FieldInfo fieldInfo = FindField();
				if (fieldInfo != null)
				{
					fieldInfo.SetValue(value, TypeConversion.ImplicitCast(rightExpressionValue, fieldInfo.FieldType));
					return;
				}

				PropertyInfo propertyInfo = FindProperty();
				if (propertyInfo != null)
				{
					propertyInfo.SetValue(value, TypeConversion.ImplicitCast(rightExpressionValue, propertyInfo.PropertyType));
					return;
				}
				throw new LanguageException($"Type '{value.GetType()}' does not have an assignable member named '{reference.Property()}'.");
			}
			else if (lValue is SubscriptAstExpression subscript)
			{
				object rightExpressionValue = rValue.Execute();
				subscript.ExecuteAssignment(rightExpressionValue);
			}
			else
			{
				object rightExpressionValue = rValue.Execute();
				// Pass the DECLARED type of the rValue, not the concrete runtime one. The
				// declared type is the one the ForcedType setter would have fixed during
				// ValidateStatically; if the concrete one is stored in symbol.type, a
				// later PerformCmd that reassigns the same global sees that type as the
				// lValue's ForcedType and rejects legitimate reassignments with a
				// more general type (symptom: "Type X does not inherit from Y" where X
				// is the declared and Y the concrete). Triggered when the current
				// PerformCmd skips ValidateStatically (e.g. it contains Eval); the
				// runtime value stays inspectable via symbol.value.GetType().
				Type rightExpressionType = rValue.ComputeType();
				if (rightExpressionType == null && rightExpressionValue != null)
				{
					rightExpressionType = rightExpressionValue.GetType();
				}
				Type numericStorageType = NumericStorageWiderThan(((Id)lValue).ForcedType, rightExpressionValue?.GetType());
				if (numericStorageType != null)
				{
					rightExpressionValue = AstExpression.CoerceNumericValue(rightExpressionValue, numericStorageType);
					rightExpressionType = numericStorageType;
				}
				string newVariable = ((Id)lValue).Name;
				((Id)lValue).Store(rightExpressionValue, rightExpressionType);
			}
		}

		// The target of this assignment when it is a variable DECLARATION — the only target
		// whose storage has to be created before the assignment is lowered. False for every
		// target that writes to a location that already exists:
		//   a parameter               its storage is created with the parameter itself;
		//   a re-assignment           the variable was declared by an earlier occurrence;
		//   a member or a subscript    the location belongs to the receiver / the collection.
		internal bool TryGetDeclaredVariable(out Id variable)
		{
			variable = null;
			if (!(lValue is Id id)) return false;
			if (!id.IsLValue) return false;
			if (id.IsParameter) return false;
			if (!id.IsOriginalLValueDeclaration) return false;

			variable = id;
			return true;
		}

		// Creates the storage this assignment's target needs, ahead of lowering the assignment
		// itself. Total over every target shape the grammar can produce, so the caller does not
		// have to know which shapes declare storage — it just asks.
		internal Expression AllocateLocalStorageExpression(ParameterExpression parametersParam)
		{
			if (TryGetDeclaredVariable(out Id declaredVariable))
			{
				return declaredVariable.AllocateStorageExpression(parametersParam, useLValueReference: declaredVariable.IsLValue);
			}

			// Nothing to create. For a member assignment (obj.Member = rValue) that is not
			// merely an optimization: the storage belongs to the RECEIVER, and the receiver's
			// own storage is allocated on demand while ExecuteExpression resolves it through
			// DotAccess.GetTargetExpression. Pre-building a member L-value here would instead
			// read the receiver's already-generated expression, which does not exist when the
			// receiver is a global carried over from a previous journal entry: such a receiver
			// is filtered out of this program's declarations (its name is already in the
			// SymbolTable), so no Id occurrence binds it and its reference expression is still
			// null while the enclosing block is being lowered.
			if (lValue is DotAccess || lValue is SubscriptAstExpression || lValue is Id)
			{
				return Expression.Empty();
			}

			throw new LanguageException($"The target of an assignment must be a variable, a member access or a subscript, but found '{lValue?.GetType().Name ?? "null"}'.");
		}

		// The block-scoped variable this assignment contributes to the enclosing block, or null
		// when it contributes none. A declaration owns a VariableSymbol that the block has to
		// declare; a member or subscript target owns nothing.
		internal Expression LocalStorageExpression
		{
			get
			{
				if (lValue is Id id && id.IsLValue && id.IsOriginalLValueDeclaration)
				{
					return id.LValueStorageExpression;
				}
				return null;
			}
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			if (lValue is DotAccess reference)
			{
				RejectMethodCallAsLValue(reference);

				// value = reference.GetTarget();
				var instanceExpr = reference.GetTargetExpression(parametersParam);
				// rightExpressionValue = rValue.ExecuteExpression();
				var rightExprValue = rValue.ExecuteExpression(parametersParam);

				// Look up FieldInfo
				var fieldInfo = FindFieldExpression(parametersParam);
				if (fieldInfo != null)
				{
					var implicitCastMethod = typeof(TypeConversion).GetMethod(
						nameof(TypeConversion.ImplicitCast),
						BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
					);
					var castedValue = Expression.Call(
						implicitCastMethod,
						Expression.Convert(rightExprValue, typeof(object)),
						Expression.Constant(fieldInfo.FieldType, typeof(Type))
					);
					var fieldExpr = Expression.Field(Expression.Convert(instanceExpr, fieldInfo.DeclaringType), fieldInfo);
					return Expression.Assign(fieldExpr, Expression.Convert(castedValue, fieldInfo.FieldType));
				}

				// Look up PropertyInfo
				var propertyInfo = FindPropertyExpression(parametersParam);
				if (propertyInfo != null)
				{
					// Expression: Expression.Assign(Expression.Property(instanceExpr, propertyInfo), rightExprValue)
					var implicitCastMethod = typeof(TypeConversion).GetMethod(
						nameof(TypeConversion.ImplicitCast),
						BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
					);
					var castedValue = Expression.Call(
						implicitCastMethod,
						Expression.Convert(rightExprValue, typeof(object)),
						Expression.Constant(propertyInfo.PropertyType, typeof(Type))
					);
					var propertyExpr = Expression.Property(Expression.Convert(instanceExpr, propertyInfo.DeclaringType), propertyInfo);
					return Expression.Assign(propertyExpr, Expression.Convert(castedValue, propertyInfo.PropertyType));
				}

				throw new LanguageException($"Type '{instanceExpr.Type}' does not have an assignable member named '{reference.Property()}'.");
			}
			else if (lValue is SubscriptAstExpression subscript)
			{
				var rightExprValue = rValue.ExecuteExpression(parametersParam);
				return subscript.ExecuteAssignmentExpression(parametersParam, rightExprValue);
			}
			else
			{
				var id = (Id)lValue;

				// Compile the LValue storage BEFORE the rValue. For a self-referential
				// assignment whose target global appears for the first time in the
				// program on the RHS (e.g. `X = X + @value;`), the RHS's rvalue
				// occurrence would otherwise be the "first allocation" of the global and
				// carry the symbol-table init `Assign(symbolVar, entryCall)` (see
				// Id.AllocateGlobalStorageExpression). That init would then be embedded in
				// `rightExprValue`, which this block evaluates LAST (inside assignObject),
				// while `assignReferenceId` reads the same `symbolVar` FIRST — dereferencing
				// an uninitialized (null) VariableSymbol and throwing NullReferenceException
				// inside the compiled lambda. Allocating the LValue first makes its
				// occurrence the first allocation, so the init block becomes the source of
				// `assignReferenceId` and `symbolVar` is initialized exactly where it is read.
				var lValueStorage = id.ExecuteExpression(parametersParam);
				var rightExprValue = rValue.ExecuteExpression(parametersParam);

				Type numericStorageType = NumericStorageWiderThan(id.ForcedType, rightExprValue.Type);
				if (numericStorageType != null)
				{
					rightExprValue = Expression.Convert(rightExprValue, numericStorageType);
				}

				var objectField = typeof(VariableSymbol).GetField(
					nameof(VariableSymbol.value),
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly
				);

				var typeField = typeof(VariableSymbol).GetField(
					nameof(VariableSymbol.type),
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly
				);

				var referenceRValueVar = Expression.Variable(typeof(VariableSymbol), $"referenceRValueVar");
				var assignReferenceId = Expression.Assign(referenceRValueVar, lValueStorage);

				var assignObject = Expression.Assign(
					Expression.Field(referenceRValueVar, objectField),
					Expression.Convert(rightExprValue, typeof(object))
				);
				var assignType = Expression.Assign(
					Expression.Field(referenceRValueVar, typeField),
					Expression.Constant(id.ForcedType)
				);
				var block = Expression.Block(
					new[] { referenceRValueVar },
					assignReferenceId,
					assignType,

					assignObject
				);
				return block;
			}
		}

		private string LValueType(Type type)
		{
			string variableType;
			if (type == typeof(bool))
			{
				variableType = "bool";
			}
			else if (type == typeof(double))
			{
				variableType = "double";
			}
			else if (type == typeof(int))
			{
				variableType = "int";
			}
			else if (type == typeof(string))
			{
				variableType = "string";
			}
			else if (type == typeof(DateTime))
			{
				variableType = "DateTime";
			}
			else
			{
				variableType = type.FullName;
			}

			return variableType;
		}

		internal override void ValidateStatically()
		{
			Type type = rValue.ComputeType();

			if (type != null && lValue is Id id && id.IsOriginalLValueDeclaration && id.ForcedType == null)
			{
				// This occurrence carries no ForcedType yet, but the SYMBOL may already be typed
				// by an earlier assignment: a variable fixed by a previous statement of the same
				// script, or a global fixed by a previous command, reaches this validation in
				// exactly that state. Fixing the storage to the VALUE's type would retype the
				// variable to fit the value, which is the one move the ladder forbids, and the
				// consequences split by direction. A narrower storage then reported the conflict
				// from the ForcedType setter, whose diagnostic names host runtime types instead
				// of the script's vocabulary — the very substitution this refusal exists to
				// prevent. A WIDER storage was rejected outright, because retyping it down to the
				// value's type contradicts a type the symbol already holds, so an assignment the
				// ladder admits never reached the coercion that completes it.
				//
				// So when the storage is already typed, the ladder decides the pair here, exactly
				// as it does below for an occurrence that does carry a ForcedType, and the storage
				// keeps its declared type either way.
				Type declaredNumericStorage = id.ComputeType();
				bool storageAlreadyTypedOffThisOccurrence = declaredNumericStorage != null
					&& declaredNumericStorage != type
					&& AstExpression.IsPromotableNumeric(declaredNumericStorage)
					&& AstExpression.IsPromotableNumeric(type);
				if (storageAlreadyTypedOffThisOccurrence)
				{
					RefuseWhenNumericStorageIsNarrower(lValue, declaredNumericStorage, type);
					lValue.ForcedType = declaredNumericStorage;
				}
				else if (
					type == typeof(string) ||
					type == typeof(int) ||
					type == typeof(double) ||
					type == typeof(decimal) ||
					type == typeof(DateTime) ||
					type == typeof(bool) ||
					(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
				)
				{
					lValue.ForcedType = type;
				}
				else
				{
					lValue.ForcedType = type;
				}
			}

			if (lValue.ForcedType != null && !lValue.ForcedType.IsAssignableFrom(type))
			{
				// Permissive resolution for covariant returns and identity-return
				// methods: when rValue is a method call, the runtime value may be
				// assignable to lValue.ForcedType even though the declared static
				// return type is not. Two distinct shapes are covered:
				//   (a) The receiver is abstract/interface (or non-sealed) and a
				//       concrete subclass overrides the method with a more refined
				//       return type.
				//   (b) The receiver is concrete and the method's declared return
				//       type is a strict BASE of ForcedType — typically a body that
				//       returns its own caller-supplied argument (identity-return /
				//       accumulator).
				// Mirrors the polymorphic resolution already applied in
				// DotAccess.ComputeCallExpressionType and DotAccess.InvokeMethodExpression
				// for the symmetric "member only on subclass" pattern.
				bool covariantOverrideAccepted = rValue is DotAccess rValueAsDotAccess
					&& rValueAsDotAccess.HasOverrideReturnTypeAssignableTo(lValue.ForcedType);
				if (AstExpression.IsPromotableNumeric(lValue.ForcedType) && AstExpression.IsPromotableNumeric(type))
				{
					// Two members of the numeric family. IsAssignableFrom is false for every
					// such pair — they are unrelated structs — so the ladder stands in for it:
					// the storage holds the value when the ladder names the STORAGE as the
					// wider of the two, exactly as a reference storage holds a value of its
					// own type or of a subtype.
					//
					// The variable is never retyped to fit the value. Resolving a wider type
					// for the pair would be resolving it up the hierarchy, and the only
					// ancestor two numeric structs share is System.ValueType — a type off the
					// ladder, so the operators reject every later reference to the variable
					// and the diagnostic names an ancestor the author never wrote.
					RefuseWhenNumericStorageIsNarrower(lValue, lValue.ForcedType, type);
				}
				else if (!covariantOverrideAccepted)
				{
					// Sibling reassignment: rValue's type is neither base nor subclass
					// of the lValue's ForcedType, but both descend from a shared base
					// (e.g. a variable fixed to one refined subtype is later reassigned
					// a distinct subtype of the same hierarchy). The assignment is sound
					// dynamically — the variable simply needs a static type wide enough
					// to hold either value. Widen its ForcedType to the least common base
					// across every occurrence of the name plus the persisted symbol so
					// member resolution and the compiled storage cast remain valid. If the
					// only shared ancestor is object (genuinely unrelated types), there is
					// no sound common type and the assignment is rejected as before.
					Type commonBase = LeastCommonBase(lValue.ForcedType, type);
					bool widenedToSharedBase = commonBase != null
						&& commonBase != typeof(object)
						&& lValue is Id lValueId;
					if (widenedToSharedBase)
					{
						WidenVariableToCommonBase((Id)lValue, commonBase);
					}
					else
					{
						throw new LanguageException($"Type {type} does not inherit from {lValue.ForcedType}.");
					}
				}
			}

			if (lValue is SubscriptAstExpression subscriptLValue)
				subscriptLValue.ValidateAsLValue();
			else
				lValue.ValidateStatically();
			rValue.ValidateStatically();
		}

		// The ladder's refusal, in one place, so that every route into a numeric assignment
		// states the rule with the same words. A refusal an author cannot act on is worse than
		// none: it must name the storage, the value and the literal that opens the storage wide
		// enough, in the vocabulary of the script. Returns without throwing whenever the pair is
		// admissible — either side off the ladder, or the storage already the wider of the two.
		private static void RefuseWhenNumericStorageIsNarrower(AstExpression lValue, Type storageType, Type valueType)
		{
			if (storageType == null || valueType == null) return;
			if (!AstExpression.IsPromotableNumeric(storageType) || !AstExpression.IsPromotableNumeric(valueType)) return;
			if (AstExpression.PromotedNumericType(storageType, valueType) == storageType) return;

			string variableName = (lValue is Id numericLValue) ? numericLValue.Name : null;
			string storage = (variableName == null) ? "this storage" : $"'{variableName}'";
			throw new LanguageException(
				$"Cannot store a {NumericTypeName(valueType)} value in {storage}, which is declared {NumericTypeName(storageType)}. "
				+ $"A numeric value reaches a storage of its own type or a wider one (int, long, double, decimal), never a narrower one: "
				+ $"declare the storage at the wider type (for instance '{ZeroLiteralOf(valueType)}' instead of '{ZeroLiteralOf(storageType)}').");
		}

		// The name the AUTHOR writes for a numeric type. A diagnostic about a script quotes the
		// script's vocabulary, not the host runtime's: an author who wrote 'int' cannot act on
		// a message that names Int32.
		private static string NumericTypeName(Type numericType)
		{
			if (numericType == typeof(int)) return "int";
			if (numericType == typeof(long)) return "long";
			if (numericType == typeof(double)) return "double";
			if (numericType == typeof(decimal)) return "decimal";
			return numericType.Name;
		}

		// The zero literal that declares a storage of this type, so the diagnostic can show the
		// one-character edit that opens the storage wide enough instead of only naming the rule.
		private static string ZeroLiteralOf(Type numericType)
		{
			if (numericType == typeof(long)) return "0L";
			if (numericType == typeof(double)) return "0.0";
			if (numericType == typeof(decimal)) return "0m";
			return "0";
		}

		// The storage's declared type when it is a numeric type WIDER than the value about to
		// be stored, null otherwise (nothing to coerce, or either side off the ladder). Never
		// narrows: the answer is the storage type only when the ladder names it as the wider
		// of the two, which is the same condition that admitted the assignment.
		//
		// A numeric value narrower than its storage must be widened AT THE STORE, because for
		// value types "fits" means the representation itself changes. Leaving the narrower
		// value in the slot makes the two engines disagree about the variable: the interpreted
		// plane reports the stored value's runtime type, while the compiled plane coerces the
		// slot to the declared type on every read — so the same script renders the variable
		// differently by compilation mode, and a local slot, whose compiled read is an unbox
		// rather than a conversion, fails outright. Coercing here keeps the declared type and
		// the stored value in agreement, which is the single fact both planes read back.
		private static Type NumericStorageWiderThan(Type storageType, Type valueType)
		{
			if (storageType == null || valueType == null) return null;
			if (storageType == valueType) return null;
			return AstExpression.PromotedNumericType(storageType, valueType) == storageType ? storageType : null;
		}

		// Least common base of two types: the most derived type that is assignable
		// from both. Walks lhs's base chain and returns the first ancestor that is
		// also assignable from rhs. Returns object for unrelated types (their only
		// shared ancestor), which the caller treats as "no sound common base".
		private static Type LeastCommonBase(Type lhs, Type rhs)
		{
			if (lhs == null || rhs == null) return null;
			for (Type ancestor = lhs; ancestor != null; ancestor = ancestor.BaseType)
			{
				if (ancestor.IsAssignableFrom(rhs)) return ancestor;
			}
			return null;
		}

		// Apply the widened static type uniformly: every Id occurrence of the
		// variable (declaration and references, in this script and any carried over
		// from a previous PerformCmd) and the persisted global symbol must agree on
		// the common base. Otherwise a stale narrower ForcedType on one occurrence
		// would drive the compiled storage cast and fail at runtime for a value of
		// the sibling type.
		private void WidenVariableToCommonBase(Id variable, Type commonBase)
		{
			foreach (Id occurrence in variable.Program.Collect<Id>())
			{
				if (string.Equals(occurrence.Name, variable.Name, StringComparison.OrdinalIgnoreCase))
				{
					occurrence.WidenForcedTypeTo(commonBase);
				}
			}
			if (this.symbolTable.HasVariable(variable.Name))
			{
				VariableSymbol symbol = this.symbolTable.Entry(variable.Name);
				if (symbol != null) symbol.type = commonBase;
			}
		}


		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			lValue.PreparePatternMatching(patternAst, ref position);
			rValue.PreparePatternMatching(patternAst, ref position);

			string targetName = (lValue is Id id) ? id.Name : null;
			Type targetType = lValue.ComputeType();
			object value = null;
			if (rValue is LiteralString || rValue is LiteralNumber || rValue is LiteralDecimal || rValue is LiteralDouble || rValue is LiteralBoolean || rValue is LiteralDateTime || rValue is LiteralList || rValue is LiteralNull)
			{
				value = rValue.Execute();
			}
			else if (rValue is Id idParam && idParam.IsParameter)
			{
				value = rValue.Execute();
			}
			else
			{
				Type valueType = rValue.ComputeType();
				string variableName = (rValue is Id idRValue) ? idRValue.Name : null;
				value = new TypedValuePlaceholder(valueType, variableName);
			}

			if (targetName != null && targetType != null)
			{
				patternAst.RegisterAssignment(targetName, targetType, value, position);
			}

			if (targetType != null && lValue is Id variable && variable.IsGlobalVariable)
			{
				this.symbolTable.SetVariable(variable.Name, null, targetType);
			}
		}

		internal override void Write(StringBuilder result, int tabs, DatabaseType databaseType)
		{
			if (WasFiltered) return;
			if (lValue != null && rValue != null)
			{
				if (tabs > 0) result.Append(GenerateTabs(tabs));
				lValue.write(result, databaseType);
				result.Append(" = ");
				rValue.write(result, databaseType);
				result.Append(";\r");
			}
		}

		// The last link of the chain must name a member, not invoke one: the result of a call
		// is a value, not a storage location. Reported as a language error instead of failing
		// the member lookup with an empty member name.
		private static void RejectMethodCallAsLValue(DotAccess reference)
		{
			if (reference.Method() != null) throw new LanguageException($"Cannot assign to the result of a method call ('{reference.Method()}').");
		}

		private FieldInfo FindField()
		{
			DotAccess reference = (DotAccess)lValue;
			object instance = (object)reference.GetTarget();
			string targetFieldName = reference.Property();
			FieldInfo fieldEncontrado = null;
			foreach (FieldInfo field in instance.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				if (field.IsPublic || field.IsAssembly)
				{
					string fieldName = field.Name;
					if (string.Equals(fieldName, targetFieldName, StringComparison.OrdinalIgnoreCase))
					{
						fieldEncontrado = field;
						break;
					}
				}
			}
			return fieldEncontrado;
		}

		private FieldInfo FindFieldExpression(ParameterExpression parametersParam)
		{
			if (!(lValue is DotAccess reference))
				throw new LanguageException("The lValue of a member assignment must be a member access.");

			// Obtain the expression that represents the object
			var instanceExpr = reference.GetTargetExpression(parametersParam);
			string targetFieldName = reference.Property();

			// Look up the FieldInfo via reflection on the object type
			var instanceType = instanceExpr.Type;
			if (instanceType == null)
				throw new LanguageException($"Could not determine the receiver type of the assignment to member '{targetFieldName}'.");

			FieldInfo fieldEncontrado = FindAssignableFieldOn(instanceType, targetFieldName);
			if (fieldEncontrado != null) return fieldEncontrado;

			// Polymorphic resolution: if the declared type is abstract/interface,
			// search assignable concrete subclasses for the field. ExecuteExpression
			// will cast via fieldInfo.DeclaringType, so returning a subclass field
			// works transparently.
			if (DotAccess.CanHaveConcreteSubclasses(instanceType))
			{
				foreach (Type derived in DotAccess.EnumerateAssignableConcreteSubclasses(instanceType))
				{
					fieldEncontrado = FindAssignableFieldOn(derived, targetFieldName);
					if (fieldEncontrado != null) return fieldEncontrado;
				}
			}

			return null;
		}

		private static FieldInfo FindAssignableFieldOn(Type objectType, string targetFieldName)
		{
			foreach (FieldInfo field in objectType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				if (field.IsPublic || field.IsAssembly)
				{
					if (string.Equals(field.Name, targetFieldName, StringComparison.OrdinalIgnoreCase))
					{
						return field;
					}
				}
			}
			return null;
		}

		private PropertyInfo FindProperty()
		{
			DotAccess reference = (DotAccess)lValue;
			object instance = (object)reference.GetTarget();

			string targetPropertyName = reference.Property();

			PropertyInfo foundProperty = null;
			foreach (PropertyInfo property in instance.GetType().GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				string propertyName = property.Name;
				if (string.Equals(propertyName, targetPropertyName, StringComparison.OrdinalIgnoreCase) && property.SetMethod != null)
				{
					ParameterInfo[] variables = property.SetMethod.GetParameters();
					variables = RemoveValueFromSetter(variables);

					bool sameArgumentCount =
						(variables.Length == 0 && reference.Arguments() == null) ||
						(variables.Length == reference.Arguments().Length);

					if (sameArgumentCount)
					{
						bool validSignatures = reference.ValidateArgumentSignature(variables);
						if (validSignatures)
						{
							foundProperty = property;
							break;
						}
					}
				}
			}
			return foundProperty;
		}

		private PropertyInfo FindPropertyExpression(ParameterExpression parametersParam)
		{
			if (!(lValue is DotAccess reference))
				throw new LanguageException("The lValue of a member assignment must be a member access.");

			// Obtain the expression that represents the object
			var instanceExpr = reference.GetTargetExpression(parametersParam);
			string targetPropertyName = reference.Property();

			// Look up the PropertyInfo via reflection on the object type
			var instanceType = instanceExpr.Type;
			if (instanceType == null)
				throw new LanguageException($"Could not determine the receiver type of the assignment to member '{targetPropertyName}'.");

			PropertyInfo foundProperty = FindAssignablePropertyOn(instanceType, targetPropertyName, reference);
			if (foundProperty != null) return foundProperty;

			// Polymorphic resolution: if the declared type is abstract/interface,
			// search assignable concrete subclasses. ExecuteExpression casts via
			// propertyInfo.DeclaringType, so a subclass property works transparently.
			if (DotAccess.CanHaveConcreteSubclasses(instanceType))
			{
				foreach (Type derived in DotAccess.EnumerateAssignableConcreteSubclasses(instanceType))
				{
					foundProperty = FindAssignablePropertyOn(derived, targetPropertyName, reference);
					if (foundProperty != null) return foundProperty;
				}
			}

			return null;
		}

		private PropertyInfo FindAssignablePropertyOn(Type objectType, string targetPropertyName, DotAccess reference)
		{
			foreach (PropertyInfo property in objectType.GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
			{
				if (string.Equals(property.Name, targetPropertyName, StringComparison.OrdinalIgnoreCase) && property.SetMethod != null)
				{
					ParameterInfo[] variables = property.SetMethod.GetParameters();
					variables = RemoveValueFromSetter(variables);

					bool sameArgumentCount =
						(variables.Length == 0 && reference.Arguments() == null) ||
						(variables.Length == reference.Arguments().Length);

					if (sameArgumentCount)
					{
						bool validSignatures = reference.ValidateArgumentSignature(variables);
						if (validSignatures)
						{
							return property;
						}
					}
				}
			}
			return null;
		}

		private ParameterInfo[] RemoveValueFromSetter(ParameterInfo[] parameters)
		{
			List<ParameterInfo> result = null;
			if (parameters != null)
			{
				result = new List<ParameterInfo>(parameters.Length);
				foreach (var parameter in parameters)
				{
					if (parameter.Name != "value")
					{
						result.Add(parameter);
					}
				}
			}
			return result.ToArray();
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			lValue.Visit(v);
			rValue.Visit(v);
		}


	}
}
