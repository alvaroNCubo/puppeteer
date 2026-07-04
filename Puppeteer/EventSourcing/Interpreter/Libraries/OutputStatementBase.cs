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

		internal override void Write(StringBuilder result, int tabs, DatabaseType databaseType)
		{
			foreach (var item in items)
			{
				item.Write(result, tabs, databaseType);
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

		protected abstract string GetCommandName();
		protected abstract Output GetTargetBuffer(ExecutionOutput output);

		// Whether this filtered output statement is kept in the authored render
		// (Program.ConvertToAuthoredString, under AuthoredRenderScope). Prints
		// override this to true so they survive in the once-written Action body;
		// expose stays false because its data is journaled through its own
		// exposeData channel, not the body text.
		protected virtual bool PreservedInAuthoredBody => false;

		internal OutputStatementIndividual(AstExpression expression, String alias, bool wasFiltered)
		{
			this.expression = expression;
			this.alias = alias;
			base.WasFiltered = wasFiltered;
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
			var result = expression.Execute();
			buffer.Append(alias.AsSpan(), result);
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam, ParameterExpression outputParam)
		{
			Expression result = expression.ExecuteExpression(parametersParam);
			var resultType = result.Type;

			if (
				(resultType.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(resultType)) &&
				(
					resultType != typeof(string) &&
					resultType != typeof(string[]) &&
					resultType != typeof(int[]) &&
					resultType != typeof(bool[]) &&
					resultType != typeof(double[]) &&
					resultType != typeof(decimal[]) &&
					resultType != typeof(DateTime[])
				)
			)
			{
				return CreateAppendExpressionForGenericCollection(result, resultType, outputParam);
			}
			else if (
				resultType != typeof(string) &&
				typeof(object).IsAssignableFrom(resultType) &&
				resultType.IsClass &&
				!resultType.IsArray &&
				typeof(System.Collections.IEnumerable).IsAssignableFrom(resultType)
			)
			{
				return CreateAppendExpressionForEnumerableClass(result, resultType, outputParam);
			}
			else
			{
				return CreateAppendExpressionForType(result, resultType, outputParam);
			}
		}

		private Expression CreateAppendExpressionForGenericCollection(Expression result, Type resultType, ParameterExpression outputParam)
		{
			Expression castedCollection;
			Type elementType = resultType.IsArray
				? resultType.GetElementType()
				: (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(List<>) ? resultType.GetGenericArguments()[0] : typeof(object));

			if (resultType.IsArray)
			{
				var castMethod = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "Cast" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				var toArrayMethod = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "ToArray" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				var castCall = Expression.Call(castMethod, result);
				castedCollection = Expression.Call(toArrayMethod, castCall);
			}
			else if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(List<>))
			{
				var castMethod = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "Cast" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				var toListMethod = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "ToList" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				var castCall = Expression.Call(castMethod, result);
				castedCollection = Expression.Call(toListMethod, castCall);
			}
			else
			{
				var toObjectEnumerable = typeof(Enumerable)
					.GetMethods()
					.First(m => m.Name == "Cast" && m.GetParameters().Length == 1)
					.MakeGenericMethod(typeof(object));
				castedCollection = Expression.Call(toObjectEnumerable, result);
			}

			Type collectionType = castedCollection.Type;
			var outputAppendMethod = GetAppendMethodForType(collectionType);

			return Expression.IfThen(
				Expression.Property(outputParam, EstaEscribiendoProperty),
				Expression.Call(
					outputParam,
					outputAppendMethod,
					Expression.Call(AsSpanMethod, Expression.Constant(alias)),
					castedCollection
				)
			);
		}

		private Expression CreateAppendExpressionForEnumerableClass(Expression result, Type resultType, ParameterExpression outputParam)
		{
			result = Expression.Condition(
				Expression.Equal(result, Expression.Constant(null, resultType)),
				Expression.Constant(string.Empty),
				Expression.Call(result, resultType.GetMethod("ToString", Type.EmptyTypes))
			);

			return Expression.IfThen(
				Expression.Property(outputParam, EstaEscribiendoProperty),
				Expression.Call(
					outputParam,
					AppendStringMethod,
					Expression.Call(AsSpanMethod, Expression.Constant(alias)),
					result
				)
			);
		}

		private Expression CreateAppendExpressionForType(Expression result, Type resultType, ParameterExpression outputParam)
		{
			var outputAppendMethod = GetAppendMethodForType(resultType);

			if (resultType.IsEnum)
			{
				outputAppendMethod = AppendStringMethod;
				result = Expression.Call(result, typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes));
			}
			else if (outputAppendMethod == null && resultType.IsClass && resultType != typeof(string))
			{
				outputAppendMethod = AppendObjectMethod;
			}

			return Expression.IfThen(
				Expression.Property(outputParam, EstaEscribiendoProperty),
				Expression.Call(
					outputParam,
					outputAppendMethod,
					Expression.Call(AsSpanMethod, Expression.Constant(alias)),
					result
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

		internal override void Write(StringBuilder result, int tabs, DatabaseType databaseType)
		{
			if (WasFiltered && !(AuthoredRenderScope.Active && PreservedInAuthoredBody)) return;
			if (tabs > 0) result.Append(GenerateTabs(tabs));
			result.Append(GetCommandName());
			result.Append(' ');
			expression.write(result, databaseType);
			result.Append(' ');
			// The alias is a string literal — render it through the same path as every
			// other string literal (LiteralString.Write) so it is properly quoted,
			// escaped per DatabaseType, and a fixed point under parse -> render. The
			// earlier hand-rolled form emitted the internal  / sentinels
			// directly, which the SQL Server storage writer post-processes but the
			// in-memory / render paths do not — leaving raw sentinels that accreted a
			// character on every render -> parse cycle (surfaced once the Action body
			// began carrying prints in the journal).
			LiteralString.Write(result, alias, databaseType);
			result.Append(';');
			result.Append('\r');
		}
	}
}
