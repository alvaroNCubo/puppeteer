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
			if (lValue is DottedId reference)
			{
				object value = reference.GetTarget();
				object valorDeLaExpresionDerecha = rValue.Execute();

				FieldInfo fieldInfo = FindField();
				if (fieldInfo != null)
				{
					fieldInfo.SetValue(value, TypeConversion.ImplicitCast(valorDeLaExpresionDerecha, fieldInfo.FieldType));
					return;
				}

				PropertyInfo propertyInfo = FindProperty();
				if (propertyInfo != null)
				{
					propertyInfo.SetValue(value, TypeConversion.ImplicitCast(valorDeLaExpresionDerecha, propertyInfo.PropertyType));
					return;
				}
				throw new LanguageException($"Type of variable '{reference.Id()}' does not have a property named '{reference.Property()}'.");
			}
			else if (lValue is ChainedDotAccess)
			{

			}
			else if (lValue is SubscriptAstExpression subscript)
			{
				object valorDeLaExpresionDerecha = rValue.Execute();
				subscript.ExecuteAssignment(valorDeLaExpresionDerecha);
			}
			else
			{
				object valorDeLaExpresionDerecha = rValue.Execute();
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
				if (rightExpressionType == null && valorDeLaExpresionDerecha != null)
				{
					rightExpressionType = valorDeLaExpresionDerecha.GetType();
				}
				string nuevaVariable = ((Id)lValue).Name;
				((Id)lValue).Store(valorDeLaExpresionDerecha, rightExpressionType);
			}
		}

		internal Expression AllocateLocalStorageExpression(ParameterExpression parametersParam)
		{
			if (lValue is DottedId referenciaIdConPunto)
			{
				return referenciaIdConPunto.AllocateStorageExpression(parametersParam);
			}
			else if (lValue is ChainedDotAccess)
			{
				throw new NotImplementedException();
			}
			else if (lValue is SubscriptAstExpression)
			{
				return Expression.Empty();
			}
			else if (lValue is Id referenciaId)
			{
				return referenciaId.AllocateStorageExpression(parametersParam, useLValueReference: referenciaId.IsLValue);
			}
			else
			{
				throw new LanguageException($"The lValue must be an Id, DottedId, or ChainedDotAccess, but found '{lValue?.GetType().Name ?? "null"}'.");
			}
		}

		internal Expression LocalStorageExpression
		{
			get
			{
				if (lValue is DottedId reference)
				{
					return null;
				}
				else if (lValue is ChainedDotAccess)
				{
					throw new NotImplementedException();
				}
				else if (lValue is SubscriptAstExpression)
				{
					return null;
				}
				else if (lValue is Id id)
				{
					return id.IsOriginalLValueDeclaration ? id.LValueStorageExpression : null;
				}
				else
				{
					throw new LanguageException($"The lValue must be an Id or DottedId, but found '{lValue?.GetType().Name ?? "null"}'.");
				}
			}
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			if (lValue is DottedId reference)
			{
				// value = reference.GetTarget();
				var instanceExpr = reference.GetTargetExpression(parametersParam);
				// valorDeLaExpresionDerecha = rValue.ExecuteExpression();
				var valorDerechaExpr = rValue.ExecuteExpression(parametersParam);

				// Buscar FieldInfo
				var fieldInfo = FindFieldExpression(parametersParam);
				if (fieldInfo != null)
				{
					var implicitCastMethod = typeof(TypeConversion).GetMethod(
						nameof(TypeConversion.ImplicitCast),
						BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
					);
					var castedValue = Expression.Call(
						implicitCastMethod,
						Expression.Convert(valorDerechaExpr, typeof(object)),
						Expression.Constant(fieldInfo.FieldType, typeof(Type))
					);
					var fieldExpr = Expression.Field(Expression.Convert(instanceExpr, fieldInfo.DeclaringType), fieldInfo);
					return Expression.Assign(fieldExpr, Expression.Convert(castedValue, fieldInfo.FieldType));
				}

				// Buscar PropertyInfo
				var propertyInfo = FindPropertyExpression(parametersParam);
				if (propertyInfo != null)
				{
					// Expression: Expression.Assign(Expression.Property(instanceExpr, propertyInfo), valorDerechaExpr)
					var implicitCastMethod = typeof(TypeConversion).GetMethod(
						nameof(TypeConversion.ImplicitCast),
						BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
					);
					var castedValue = Expression.Call(
						implicitCastMethod,
						Expression.Convert(valorDerechaExpr, typeof(object)),
						Expression.Constant(propertyInfo.PropertyType, typeof(Type))
					);
					var propertyExpr = Expression.Property(Expression.Convert(instanceExpr, propertyInfo.DeclaringType), propertyInfo);
					return Expression.Assign(propertyExpr, Expression.Convert(castedValue, propertyInfo.PropertyType));
				}

				throw new LanguageException($"Type of variable '{reference.Id()}' does not have a property named '{reference.Property()}'.");
			}
			else if (lValue is ChainedDotAccess)
			{
				return Expression.Empty();
			}
			else if (lValue is SubscriptAstExpression subscript)
			{
				var valorDerechaExpr = rValue.ExecuteExpression(parametersParam);
				return subscript.ExecuteAssignmentExpression(parametersParam, valorDerechaExpr);
			}
			else
			{
				var valorDerechaExpr = rValue.ExecuteExpression(parametersParam);
				var id = (Id)lValue;

				var objetoField = typeof(VariableSymbol).GetField(
					nameof(VariableSymbol.value),
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly
				);

				var tipoField = typeof(VariableSymbol).GetField(
					nameof(VariableSymbol.type),
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly
				);

				var referenceRValueVar = Expression.Variable(typeof(VariableSymbol), $"referenceRValueVar");
				var assignReferenceId = Expression.Assign(referenceRValueVar, id.ExecuteExpression(parametersParam));

				var assignObjeto = Expression.Assign(
					Expression.Field(referenceRValueVar, objetoField),
					Expression.Convert(valorDerechaExpr, typeof(object))
				);
				var assignTipo = Expression.Assign(
					Expression.Field(referenceRValueVar, tipoField),
					Expression.Constant(id.ForcedType)
				);
				var block = Expression.Block(
					new[] { referenceRValueVar },
					assignReferenceId,
					assignTipo,

					assignObjeto
				);
				return block;
			}
		}

		private string TipoLValue(Type type)
		{
			string tipoVariable;
			if (type == typeof(bool))
			{
				tipoVariable = "bool";
			}
			else if (type == typeof(double))
			{
				tipoVariable = "double";
			}
			else if (type == typeof(int))
			{
				tipoVariable = "int";
			}
			else if (type == typeof(string))
			{
				tipoVariable = "string";
			}
			else if (type == typeof(DateTime))
			{
				tipoVariable = "DateTime";
			}
			else
			{
				tipoVariable = type.FullName;
			}

			return tipoVariable;
		}

		internal override void ValidateStatically()
		{
			Type type = rValue.ComputeType();

			if (type != null && lValue is Id id && id.IsOriginalLValueDeclaration && id.ForcedType == null)
			{
				if (
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
				if (!covariantOverrideAccepted) throw new LanguageException($"Type {type} does not inherit from {lValue.ForcedType}.");
			}

			if (lValue is SubscriptAstExpression subscriptLValue)
				subscriptLValue.ValidateAsLValue();
			else
				lValue.ValidateStatically();
			rValue.ValidateStatically();
		}

		// B.3.1: include LValue + RValue contributions so two assignments with
		// the same shape but different literal RHS hash equally.
		internal override void AccumulatePromotionCandidateHash(ref HashCode hc)
		{
			hc.Add(nameof(NewInstanceStatement));
			lValue.AccumulatePromotionCandidateHash(ref hc);
			rValue.AccumulatePromotionCandidateHash(ref hc);
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

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (lValue != null && rValue != null)
			{
				if (tabs > 0) resultado.Append(GenerarTabs(tabs));
				lValue.write(resultado, databaseType);
				resultado.Append(" = ");
				rValue.write(resultado, databaseType);
				resultado.Append(";\r");
			}
		}

		private FieldInfo FindField()
		{
			DottedId reference = (DottedId)lValue;
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
			if (!(lValue is DottedId reference))
				throw new LanguageException("lValue must be DottedId");

			// Obtain the expression that represents the object
			var instanceExpr = reference.GetTargetExpression(parametersParam);
			string targetFieldName = reference.Property();

			// Look up the FieldInfo via reflection on the object type
			var instanceType = instanceExpr.Type;
			if (instanceType == null)
				throw new LanguageException($"Could not determine the object type for reference '{reference.Id()}.{targetFieldName}'.");

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
			DottedId reference = (DottedId)lValue;
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
			if (!(lValue is DottedId reference))
				throw new LanguageException("lValue must be DottedId");

			// Obtain the expression that represents the object
			var instanceExpr = reference.GetTargetExpression(parametersParam);
			string targetPropertyName = reference.Property();

			// Look up the PropertyInfo via reflection on the object type
			var instanceType = instanceExpr.Type;
			if (instanceType == null)
				throw new LanguageException($"Could not determine the object type for reference '{reference.Id()}.{targetPropertyName}'.");

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

		private PropertyInfo FindAssignablePropertyOn(Type objectType, string targetPropertyName, DottedId reference)
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
			List<ParameterInfo> resultado = null;
			if (parameters != null)
			{
				resultado = new List<ParameterInfo>(parameters.Length);
				foreach (var parametro in parameters)
				{
					if (parametro.Name != "value")
					{
						resultado.Add(parametro);
					}
				}
			}
			return resultado.ToArray();
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
