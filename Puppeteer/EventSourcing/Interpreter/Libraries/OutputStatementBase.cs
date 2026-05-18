using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	internal abstract class OutputStatementBase : Statement
	{
		private readonly IEnumerable<OutputStatementIndividual> items;

		internal OutputStatementBase(IEnumerable<OutputStatementIndividual> items)
		{
			ArgumentNullException.ThrowIfNull(items);
			this.items = items;
			if (!this.items.Any()) throw new ArgumentException("The items collection cannot be empty.", nameof(items));
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			foreach (var item in items)
			{
				item.Visit(v);
			}
		}

		internal override void Execute(ExecutionOutput output)
		{
			foreach (var item in items)
			{
				item.Execute(output);
			}
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			var expressions = new List<Expression>();
			foreach (var item in items)
			{
				expressions.Add(item.ExecuteExpression(parametersParam, outputParam));
			}
			return Expression.Block(expressions);
		}

		internal override void ValidateStatically()
		{
			foreach (var item in items)
			{
				item.ValidateStatically();
			}
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			foreach (var item in items)
			{
				item.PreparePatternMatching(patternAst, ref position);
			}
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			foreach (var item in items)
			{
				item.Write(resultado, tabs, databaseType);
			}
		}
	}

	internal abstract class OutputStatementIndividual : Statement
	{
		private AstExpression expression;
		private readonly String alias;

		private static readonly System.Reflection.MethodInfo AsSpanMethod = typeof(MemoryExtensions).GetMethod(nameof(MemoryExtensions.AsSpan), new Type[] { typeof(string) });
		private static readonly System.Reflection.PropertyInfo EstaEscribiendoProperty = typeof(Output).GetProperty(nameof(Output.IsWriting), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
		private static readonly System.Reflection.BindingFlags AppendMethodBindingFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

		private static readonly System.Reflection.MethodInfo AppendBoolMethod = GetAppendMethod(typeof(bool));
		private static readonly System.Reflection.MethodInfo AppendStringMethod = GetAppendMethod(typeof(string));
		private static readonly System.Reflection.MethodInfo AppendIntMethod = GetAppendMethod(typeof(int));
		private static readonly System.Reflection.MethodInfo AppendDoubleMethod = GetAppendMethod(typeof(double));
		private static readonly System.Reflection.MethodInfo AppendLongMethod = GetAppendMethod(typeof(long));
		private static readonly System.Reflection.MethodInfo AppendDateTimeMethod = GetAppendMethod(typeof(DateTime));
		private static readonly System.Reflection.MethodInfo AppendDecimalMethod = GetAppendMethod(typeof(decimal));
		private static readonly System.Reflection.MethodInfo AppendObjectMethod = GetAppendMethod(typeof(object));
		private static readonly System.Reflection.MethodInfo AppendIntArrayMethod = GetAppendMethod(typeof(int[]));
		private static readonly System.Reflection.MethodInfo AppendDoubleArrayMethod = GetAppendMethod(typeof(double[]));
		private static readonly System.Reflection.MethodInfo AppendDecimalArrayMethod = GetAppendMethod(typeof(decimal[]));
		private static readonly System.Reflection.MethodInfo AppendStringArrayMethod = GetAppendMethod(typeof(string[]));
		private static readonly System.Reflection.MethodInfo AppendBoolArrayMethod = GetAppendMethod(typeof(bool[]));
		private static readonly System.Reflection.MethodInfo AppendDateTimeArrayMethod = GetAppendMethod(typeof(DateTime[]));
		private static readonly System.Reflection.MethodInfo AppendObjectArrayMethod = GetAppendMethod(typeof(object[]));
		private static readonly System.Reflection.MethodInfo AppendIntListMethod = GetAppendMethod(typeof(List<int>));
		private static readonly System.Reflection.MethodInfo AppendDoubleListMethod = GetAppendMethod(typeof(List<double>));
		private static readonly System.Reflection.MethodInfo AppendDecimalListMethod = GetAppendMethod(typeof(List<decimal>));
		private static readonly System.Reflection.MethodInfo AppendStringListMethod = GetAppendMethod(typeof(List<string>));
		private static readonly System.Reflection.MethodInfo AppendBoolListMethod = GetAppendMethod(typeof(List<bool>));
		private static readonly System.Reflection.MethodInfo AppendDateTimeListMethod = GetAppendMethod(typeof(List<DateTime>));
		private static readonly System.Reflection.MethodInfo AppendObjectListMethod = GetAppendMethod(typeof(List<object>));
		private static readonly System.Reflection.MethodInfo AppendIntEnumerableMethod = GetAppendMethod(typeof(IEnumerable<int>));
		private static readonly System.Reflection.MethodInfo AppendDoubleEnumerableMethod = GetAppendMethod(typeof(IEnumerable<double>));
		private static readonly System.Reflection.MethodInfo AppendDecimalEnumerableMethod = GetAppendMethod(typeof(IEnumerable<decimal>));
		private static readonly System.Reflection.MethodInfo AppendStringEnumerableMethod = GetAppendMethod(typeof(IEnumerable<string>));
		private static readonly System.Reflection.MethodInfo AppendBoolEnumerableMethod = GetAppendMethod(typeof(IEnumerable<bool>));
		private static readonly System.Reflection.MethodInfo AppendDateTimeEnumerableMethod = GetAppendMethod(typeof(IEnumerable<DateTime>));
		private static readonly System.Reflection.MethodInfo AppendObjectEnumerableMethod = GetAppendMethod(typeof(IEnumerable<object>));

		private static System.Reflection.MethodInfo GetAppendMethod(Type secondParameterType)
		{
			var method = typeof(Output)
				.GetMethods(AppendMethodBindingFlags)
				.FirstOrDefault(m =>
					m.Name == nameof(Output.Append) &&
					m.GetParameters().Length == 2 &&
					m.GetParameters()[0].ParameterType.Name == "ReadOnlySpan`1" &&
					m.GetParameters()[0].ParameterType.GenericTypeArguments[0] == typeof(char) &&
					m.GetParameters()[1].ParameterType == secondParameterType
				);

			if (method == null)
			{
				throw new LanguageException($"Method '{nameof(Output.Append)}' was not found on '{nameof(Output)}' with the expected signature for ReadOnlySpan<char> and '{secondParameterType.FullName}'.");
			}

			return method;
		}

		protected abstract string GetComandoName();
		protected abstract Output GetTargetBuffer(ExecutionOutput output);

		internal OutputStatementIndividual(AstExpression expression, String alias, bool fueFiltrado)
		{
			this.expression = expression;
			this.alias = alias;
			base.FueFiltrado = fueFiltrado;
		}

		internal override void Execute(ExecutionOutput output)
		{
			if (output.IsRehydrating && this is ExposeStatementIndividual)
			{
				return;
			}
			var buffer = GetTargetBuffer(output);
			if (!buffer.IsWriting)
			{
				return;
			}
			var resultado = expression.Execute();
			buffer.Append(alias.AsSpan(), resultado);
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			Expression resultado = expression.ExecuteExpression(parametersParam);
			var resultadoType = resultado.Type;

			if (
				(resultadoType.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(resultadoType)) &&
				(
					resultadoType != typeof(string) &&
					resultadoType != typeof(string[]) &&
					resultadoType != typeof(int[]) &&
					resultadoType != typeof(bool[]) &&
					resultadoType != typeof(double[]) &&
					resultadoType != typeof(decimal[]) &&
					resultadoType != typeof(DateTime[])
				)
			)
			{
				return CreateAppendExpressionForGenericCollection(resultado, resultadoType, outputParam);
			}
			else if (
				resultadoType != typeof(string) &&
				typeof(object).IsAssignableFrom(resultadoType) &&
				resultadoType.IsClass &&
				!resultadoType.IsArray &&
				typeof(System.Collections.IEnumerable).IsAssignableFrom(resultadoType)
			)
			{
				return CreateAppendExpressionForEnumerableClass(resultado, resultadoType, outputParam);
			}
			else
			{
				return CreateAppendExpressionForType(resultado, resultadoType, outputParam);
			}
		}

		private Expression CreateAppendExpressionForGenericCollection(Expression resultado, Type resultadoType, ParameterExpression outputParam)
		{
			Expression castedCollection;
			Type elementType = resultadoType.IsArray
				? resultadoType.GetElementType()
				: (resultadoType.IsGenericType && resultadoType.GetGenericTypeDefinition() == typeof(List<>) ? resultadoType.GetGenericArguments()[0] : typeof(object));

			if (resultadoType.IsArray)
			{
				var castMethod = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "Cast" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				var toArrayMethod = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "ToArray" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				var castCall = Expression.Call(castMethod, resultado);
				castedCollection = Expression.Call(toArrayMethod, castCall);
			}
			else if (resultadoType.IsGenericType && resultadoType.GetGenericTypeDefinition() == typeof(List<>))
			{
				var castMethod = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "Cast" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				var toListMethod = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "ToList" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				var castCall = Expression.Call(castMethod, resultado);
				castedCollection = Expression.Call(toListMethod, castCall);
			}
			else
			{
				var toObjectEnumerable = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "Cast" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				castedCollection = Expression.Call(toObjectEnumerable, resultado);
			}

			Type collectionType = castedCollection.Type;
			var salidaAppendMethod = GetAppendMethodForType(collectionType);

			return Expression.IfThen(
				Expression.Property(outputParam, EstaEscribiendoProperty),
				Expression.Call(
					outputParam,
					salidaAppendMethod,
					Expression.Call(AsSpanMethod, Expression.Constant(alias)),
					castedCollection
				)
			);
		}

		private Expression CreateAppendExpressionForEnumerableClass(Expression resultado, Type resultadoType, ParameterExpression outputParam)
		{
			resultado = Expression.Condition(
				Expression.Equal(resultado, Expression.Constant(null, resultadoType)),
				Expression.Constant(string.Empty),
				Expression.Call(resultado, resultadoType.GetMethod("ToString", Type.EmptyTypes))
			);

			return Expression.IfThen(
				Expression.Property(outputParam, EstaEscribiendoProperty),
				Expression.Call(
					outputParam,
					AppendStringMethod,
					Expression.Call(AsSpanMethod, Expression.Constant(alias)),
					resultado
				)
			);
		}

		private Expression CreateAppendExpressionForType(Expression resultado, Type resultadoType, ParameterExpression outputParam)
		{
			var salidaAppendMethod = GetAppendMethodForType(resultadoType);

			if (resultadoType.IsEnum)
			{
				salidaAppendMethod = AppendStringMethod;
				resultado = Expression.Call(resultado, typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes));
			}
			else if (salidaAppendMethod == null && resultadoType.IsClass && resultadoType != typeof(string))
			{
				salidaAppendMethod = AppendObjectMethod;
			}

			return Expression.IfThen(
				Expression.Property(outputParam, EstaEscribiendoProperty),
				Expression.Call(
					outputParam,
					salidaAppendMethod,
					Expression.Call(AsSpanMethod, Expression.Constant(alias)),
					resultado
				)
			);
		}

		private static System.Reflection.MethodInfo GetAppendMethodForType(Type type)
		{
			if (type == typeof(bool)) return AppendBoolMethod;
			if (type == typeof(string)) return AppendStringMethod;
			if (type == typeof(int)) return AppendIntMethod;
			if (type == typeof(double)) return AppendDoubleMethod;
			if (type == typeof(long)) return AppendLongMethod;
			if (type == typeof(DateTime)) return AppendDateTimeMethod;
			if (type == typeof(decimal)) return AppendDecimalMethod;

			if (type == typeof(int[])) return AppendIntArrayMethod;
			if (type == typeof(double[])) return AppendDoubleArrayMethod;
			if (type == typeof(decimal[])) return AppendDecimalArrayMethod;
			if (type == typeof(string[])) return AppendStringArrayMethod;
			if (type == typeof(bool[])) return AppendBoolArrayMethod;
			if (type == typeof(DateTime[])) return AppendDateTimeArrayMethod;
			if (type == typeof(object[])) return AppendObjectArrayMethod;

			if (type == typeof(List<int>)) return AppendIntListMethod;
			if (type == typeof(List<double>)) return AppendDoubleListMethod;
			if (type == typeof(List<decimal>)) return AppendDecimalListMethod;
			if (type == typeof(List<string>)) return AppendStringListMethod;
			if (type == typeof(List<bool>)) return AppendBoolListMethod;
			if (type == typeof(List<DateTime>)) return AppendDateTimeListMethod;
			if (type == typeof(List<object>)) return AppendObjectListMethod;

			if (type == typeof(IEnumerable<int>)) return AppendIntEnumerableMethod;
			if (type == typeof(IEnumerable<double>)) return AppendDoubleEnumerableMethod;
			if (type == typeof(IEnumerable<decimal>)) return AppendDecimalEnumerableMethod;
			if (type == typeof(IEnumerable<string>)) return AppendStringEnumerableMethod;
			if (type == typeof(IEnumerable<bool>)) return AppendBoolEnumerableMethod;
			if (type == typeof(IEnumerable<DateTime>)) return AppendDateTimeEnumerableMethod;
			if (type == typeof(IEnumerable<object>)) return AppendObjectEnumerableMethod;

			return AppendObjectMethod;
		}

		internal override void ValidateStatically()
		{
			expression.ValidateStatically();
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			expression.PreparePatternMatching(patternAst, ref position);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			expression.Visit(v);
		}

		internal override void Write(StringBuilder resultado, int tabs, DatabaseType databaseType)
		{
			if (FueFiltrado) return;
			if (tabs > 0) resultado.Append(GenerarTabs(tabs));
			resultado.Append(GetComandoName());
			resultado.Append(' ');
			expression.write(resultado, databaseType);
			resultado.Append(' ');
			WriteAlias(resultado);
			resultado.Append(';');
			resultado.Append('\r');
		}

		private void WriteAlias(StringBuilder resultado)
		{
			resultado.Append(LiteralString.SLASH_OR_SINGLE_QUOTED_CHARACTER).Append('\'');
			foreach (char c in alias)
			{
				switch (c)
				{
					case '\"':
						resultado.Append(LiteralString.DOUBLE_QUOTED_CHARACTER).Append('"');
						break;
					case '\'':
						resultado.Append(LiteralString.SLASH_OR_SINGLE_QUOTED_CHARACTER).Append('\'');
						break;
					default:
						resultado.Append(c);
						break;
				}
			}
			resultado.Append(LiteralString.SLASH_OR_SINGLE_QUOTED_CHARACTER).Append('\'');
		}
	}
}
