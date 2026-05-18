using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Interpreter.Utils;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	abstract class AstExpression : AST
	{

		protected bool coercesToString = false;

		internal bool CoercesToString
		{
			get
			{
				return coercesToString;
			}
			set
			{
				coercesToString = value;
			}
		}

		internal abstract object Execute();

		internal abstract Expression ExecuteExpression(ParameterExpression parametersParam);

		internal abstract Type ComputeType();

		private void PropagateTypes()
		{
			if (this.ForcedType == null && this.ComputeType() != null) this.ForcedType = this.ComputeType();
		}

		internal virtual void ValidateStatically()
		{
			PropagateTypes();
		}

		internal abstract void write(StringBuilder result, DatabaseType databaseType);

		private Type forcedType;

		internal virtual Type ForcedType
		{
			set
			{
				forcedType = value;
			}
			get
			{
				return forcedType;
			}
		}

		protected static object[] BindValuesToParameters(ParameterInfo[] methodSignature, object[] arguments)
		{
			object[] result = new object[arguments == null ? 0 : arguments.Length];
			for (int i = 0; i < methodSignature.Length; i++)
			{
				ParameterInfo parameterInfo = methodSignature[i];
				bool argIsEnumIdentifier = parameterInfo.ParameterType.IsEnum && arguments[i].GetType() == typeof(Id);
				if (argIsEnumIdentifier)
				{
					Type enumType = parameterInfo.ParameterType;
					string enumName = ((Id)arguments[i]).Name;
					if (!Enum.TryParse(enumType, enumName, true, out result[i]))
					{
						throw new LanguageException($"Enum {enumName} is unknown");
					}
				}
				else
				{
					var paramType = methodSignature[i].ParameterType;
					object evaluatedArgument = arguments[i];

					Type parameterType = parameterInfo.ParameterType;

					if (NewInstance.TypeOfCollection(paramType) == typeof(List<>) || NewInstance.TypeOfCollection(paramType) == typeof(IEnumerable<>))
					{
						if (paramType.GetGenericArguments().Length == 1)
						{
							result[i] = TypeConversion.ImplicitCast(evaluatedArgument, paramType);
						}

					}
					else if (NewInstance.TypeOfCollection(paramType).IsArray)
					{
						result[i] = TypeConversion.ImplicitCast(evaluatedArgument, paramType);
					}
					else
					{
						Type argumentType = evaluatedArgument.GetType();

						if (parameterType == typeof(double) && argumentType == typeof(int))
						{
							evaluatedArgument = Convert.ToDouble(evaluatedArgument);
						}
						else if (parameterType == typeof(double) && argumentType == typeof(decimal))
						{
							evaluatedArgument = Convert.ToDouble(evaluatedArgument);
						}
						else if (parameterType == typeof(decimal) && argumentType == typeof(int))
						{
							evaluatedArgument = Convert.ToDecimal(evaluatedArgument);
						}
						else if (parameterType == typeof(decimal) && argumentType == typeof(double))
						{
							evaluatedArgument = Convert.ToDecimal(evaluatedArgument);
						}

						result[i] = evaluatedArgument;
					}
				}
			}

			return result;
		}
		protected static Expression[] BindValueExpressionsToParameters(ParameterInfo[] methodSignature, Expression[] arguments)
		{
			Expression[] result = new Expression[arguments == null ? 0 : arguments.Length];
			for (int i = 0; i < methodSignature.Length; i++)
			{
				ParameterInfo parameterInfo = methodSignature[i];
				var paramType = parameterInfo.ParameterType;
				Expression argument = arguments[i];

				// Enum handling: if the parameter is an enum and the argument is an Id expression.
				if (paramType.IsEnum && argument is ConstantExpression constExpr && constExpr.Value is Id idArg)
				{
					string enumName = idArg.Name;
					object enumValue;
					if (!Enum.TryParse(paramType, enumName, true, out enumValue))
					{
						throw new LanguageException($"Enum {enumName} is unknown");
					}
					result[i] = Expression.Constant(enumValue, paramType);
				}
				// Collection handling.
				else if (NewInstance.TypeOfCollection(paramType) == typeof(List<>) ||
						 NewInstance.TypeOfCollection(paramType) == typeof(IEnumerable<>))
				{
					if (paramType.GetGenericArguments().Length == 1)
					{
						// Ensure we use a closed generic type for Expression.Convert.
						var closedType = paramType.IsGenericTypeDefinition
							? paramType.MakeGenericType(argument.Type.GetGenericArguments())
							: paramType;

						// Only perform Expression.Convert if closedType does not contain generic parameters.
						if (!closedType.ContainsGenericParameters)
						{
							result[i] = Expression.Convert(
								Expression.Call(
									typeof(TypeConversion).GetMethod(
										nameof(TypeConversion.ImplicitCast),
										BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
									),
									argument,
									Expression.Constant(closedType, typeof(Type))
								),
								closedType
							);
						}
						else
						{
							// If still open generic, just pass the argument as is (no conversion).
							result[i] = argument;
						}
					}
				}
				else if (NewInstance.TypeOfCollection(paramType).IsArray)
				{
					result[i] = Expression.Convert(
						Expression.Call(
							typeof(TypeConversion).GetMethod(nameof(TypeConversion.ImplicitCast), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
							argument,
							Expression.Constant(paramType, typeof(Type))
						),
						paramType
					);
				}
				// Numeric conversions.
				else
				{
					Type parameterType = paramType;
					Type argumentType = argument.Type;

					if (parameterType == typeof(double) && argumentType == typeof(int))
					{
						result[i] = Expression.Convert(argument, typeof(double));
					}
					else if (parameterType == typeof(double) && argumentType == typeof(decimal))
					{
						result[i] = Expression.Convert(argument, typeof(double));
					}
					else if (parameterType == typeof(decimal) && argumentType == typeof(int))
					{
						result[i] = Expression.Convert(argument, typeof(decimal));
					}
					else if (parameterType == typeof(decimal) && argumentType == typeof(double))
					{
						result[i] = Expression.Convert(argument, typeof(decimal));
					}
					else
					{
						// If types are not directly assignable, use ImplicitCast.
						if (!AreCompatible(argumentType, parameterType))
						{
							result[i] = Expression.Convert(
								Expression.Call(
									typeof(TypeConversion).GetMethod(
										nameof(TypeConversion.ImplicitCast), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
									argument,
									Expression.Constant(paramType, typeof(Type))
								),
								paramType
							);
						}
						else
						{
							result[i] = argument;
						}
					}
				}
			}
			return result;
		}

		internal static bool AreCompatible(Type argType, Type paramType)
		{
			bool compatible = false;

			if (argType == paramType)
			{
				compatible = true;
			}
			else if (paramType == typeof(int))
			{
				compatible = argType == typeof(int);
			}
			else if (paramType == typeof(bool))
			{
				compatible = argType == typeof(bool);
			}
			else if (paramType == typeof(double) || paramType == typeof(decimal))
			{
				compatible = argType == typeof(int) || argType == typeof(double) || argType == typeof(decimal);
			}
			else if (paramType == typeof(string))
			{
				compatible = argType == typeof(string);
			}
			else if (paramType == typeof(DateTime))
			{
				compatible = argType == typeof(DateTime);
			}
			else if (TypeOfCollection(paramType) == typeof(List<>) || TypeOfCollection(paramType) == typeof(IEnumerable<>) || TypeOfCollection(paramType).IsArray)
			{
				compatible = false;
				if (paramType.ContainsGenericParameters)
				{
					if (paramType.GetGenericArguments().Length == 1)
					{
						compatible = argType.GetGenericArguments()[0] == paramType.GetGenericArguments()[0];
						if (compatible)
						{
							var argGenericTypeDef = TypeOfCollection(argType);
							var paramGenericTypeDef = TypeOfCollection(paramType);

							compatible =
						   (
							   argGenericTypeDef == typeof(IEnumerable<>) ||
							   argGenericTypeDef == typeof(List<>) ||
							   argGenericTypeDef.IsArray
							)
							&&
							(
							   paramGenericTypeDef == typeof(IEnumerable<>) ||
							   paramGenericTypeDef == typeof(List<>) ||
							   paramGenericTypeDef.IsArray
							);
						}
					}
				}
				else
				{
					var argElementType = TypeOfCollectionElement(argType);
					var paramElementType = TypeOfCollectionElement(paramType);
					compatible = AreCompatible(argElementType, paramElementType);
					if (compatible)
					{
						var argGenericTypeDef = TypeOfCollection(argType);
						var paramGenericTypeDef = TypeOfCollection(paramType);

						compatible =
							(
								argGenericTypeDef == typeof(IEnumerable<>) ||
								argGenericTypeDef == typeof(List<>) ||
								argGenericTypeDef.IsArray
							 )
							 &&
							 (
								paramGenericTypeDef == typeof(IEnumerable<>) ||
								paramGenericTypeDef == typeof(List<>) ||
								paramGenericTypeDef.IsArray
							 );
					}
				}
			}
			else
			{
				compatible = argType == paramType || paramType.IsAssignableFrom(argType);
			}

			return compatible;
		}

		internal static Type TypeOfCollection(Type collection)
		{
			if (collection != null)
			{
				if (collection.IsArray) return collection;
				if (collection.IsGenericType)
				{
					var genericDef = collection.GetGenericTypeDefinition();
					if (genericDef == typeof(List<>)) return genericDef;
					if (genericDef == typeof(IEnumerable<>)) return genericDef;
				}
			}
			return typeof(object);
		}

		internal static Type TypeOfCollectionElement(Type collection)
		{
			if (collection.IsArray)
			{
				return collection.GetElementType();
			}
			else if (collection.IsGenericType)
			{
				return collection.GetGenericArguments()[0];
			}
			else
			{
				return null;
			}
		}

		protected Type PromotesTo(Type leftType, Type rightType)
		{
			if (leftType == typeof(int) && rightType == typeof(int))
				return typeof(int);

			if (leftType == typeof(int) && rightType == typeof(double))
				return typeof(double);

			if (leftType == typeof(int) && rightType == typeof(decimal))
				return typeof(decimal);

			if (leftType == typeof(double) && rightType == typeof(int))
				return typeof(double);

			if (leftType == typeof(double) && rightType == typeof(double))
				return typeof(double);

			if (leftType == typeof(double) && rightType == typeof(decimal))
				return typeof(decimal);

			if (leftType == typeof(decimal) && rightType == typeof(int))
				return typeof(decimal);

			if (leftType == typeof(decimal) && rightType == typeof(double))
				return typeof(decimal);

			if (leftType == typeof(decimal) && rightType == typeof(decimal))
				return typeof(decimal);

			throw new LanguageException($"Binary operation {this.GetType().Name} cannot combine {leftType.Name} and {rightType.Name}");
		}

	}
	abstract class BinaryAstExpression : AstExpression
	{
		protected readonly AstExpression e1;
		protected readonly AstExpression e2;

		protected BinaryAstExpression(AstExpression e1, AstExpression e2)
		{
			this.e1 = e1;
			this.e2 = e2;
		}

		internal AstExpression LeftExpression => e1;
		internal AstExpression RightExpression => e2;

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			e1.PreparePatternMatching(patternAst, ref position);
			e2.PreparePatternMatching(patternAst, ref position);
		}
	}
	abstract class UnaryAstExpression : AstExpression
	{
		protected readonly AstExpression e;

		protected UnaryAstExpression(AstExpression e)
		{
			this.e = e;
		}

		internal AstExpression AstExpression => e;

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			e.PreparePatternMatching(patternAst, ref position);
		}
	}

}
