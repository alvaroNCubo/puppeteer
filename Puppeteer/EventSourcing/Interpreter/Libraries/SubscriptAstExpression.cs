using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Interpreter.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class SubscriptAstExpression : AstExpression
	{
		private readonly AstExpression coleccion;
		private readonly AstExpression[] indices;

		internal SubscriptAstExpression(AstExpression coleccion, AstExpression[] indices)
		{
			ArgumentNullException.ThrowIfNull(coleccion);
			ArgumentNullException.ThrowIfNull(indices);
			if (indices.Length == 0)
				throw new LanguageException("The subscript operator '[]' requires at least one index.");
			foreach (AstExpression index in indices)
				ArgumentNullException.ThrowIfNull(index);

			this.coleccion = coleccion;
			this.indices = indices;
		}

		internal override Type ComputeType()
		{
			Type collectionType = coleccion.ComputeType();
			if (collectionType == null)
				throw new LanguageException("Cannot determine the collection type for the subscript operator '[]'.");

			if (collectionType.IsArray)
				return collectionType.GetElementType();

			if (IsBuiltinGenericList(collectionType) && indices.Length == 1)
				return collectionType.GetGenericArguments()[0];

			PropertyInfo indexerProp = ResolveIndexer(collectionType, IndexTypes(), indices);
			if (indexerProp != null)
				return indexerProp.PropertyType;

			throw new LanguageException($"Type '{collectionType.Name}' does not support the subscript operator '[]' with {indices.Length} index(es).");
		}

		internal override void ValidateStatically()
		{
			ValidateCore(asLValue: false);
		}

		internal void ValidateAsLValue()
		{
			ValidateCore(asLValue: true);
		}

		private void ValidateCore(bool asLValue)
		{
			coleccion.ValidateStatically();
			foreach (AstExpression index in indices)
				index.ValidateStatically();

			Type collectionType = coleccion.ComputeType();
			if (collectionType == null)
				throw new LanguageException("Cannot determine the collection type for the subscript operator '[]'.");

			if (collectionType.IsArray)
			{
				int rank = collectionType.GetArrayRank();
				if (rank != indices.Length)
					throw new LanguageException($"The array '{collectionType.Name}' expects {rank} index(es) but {indices.Length} were provided.");
				ValidateAllIndicesAreInt();
			}
			else if (IsBuiltinGenericList(collectionType))
			{
				if (indices.Length != 1)
					throw new LanguageException($"Type '{collectionType.Name}' only supports a single int index with the subscript operator '[]'.");
				ValidateAllIndicesAreInt();
			}
			else
			{
				PropertyInfo indexerProp = ResolveIndexer(collectionType, IndexTypes(), indices);
				if (indexerProp == null)
					throw new LanguageException($"Type '{collectionType.Name}' does not have an indexer '[]' compatible with the index types '{DescribeIndexTypes()}'.");
				if (asLValue && indexerProp.SetMethod == null)
					throw new LanguageException($"The indexer '[]' of type '{collectionType.Name}' is read-only and cannot be assigned.");
			}

			ForcedType = ComputeType();
		}

		internal override object Execute()
		{
			object col = coleccion.Execute();

			if (col == null)
				throw new LanguageException("The collection is null when trying to access it by index.");

			Type collectionType = col.GetType();

			if (col is Array array)
			{
				int[] arrayIndices = new int[indices.Length];
				for (int i = 0; i < indices.Length; i++)
					arrayIndices[i] = (int)indices[i].Execute();
				return array.GetValue(arrayIndices);
			}

			if (col is IList builtinList && IsBuiltinGenericList(collectionType) && indices.Length == 1)
				return builtinList[(int)indices[0].Execute()];

			PropertyInfo indexerProp = ResolveIndexer(collectionType, IndexTypes(), indices);
			if (indexerProp == null)
				throw new LanguageException($"Type '{collectionType.Name}' does not support the subscript operator '[]' with {indices.Length} index(es).");

			object[] keys = BindIndexValues(indexerProp);
			return indexerProp.GetValue(col, keys);
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			Expression colExpr = coleccion.ExecuteExpression(parametersParam);
			Type collectionType = colExpr.Type;

			if (collectionType.IsArray)
			{
				Expression[] arrayIndices = new Expression[indices.Length];
				for (int i = 0; i < indices.Length; i++)
					arrayIndices[i] = CoerceToInt(indices[i].ExecuteExpression(parametersParam));
				return Expression.ArrayIndex(colExpr, arrayIndices);
			}

			if (IsBuiltinGenericList(collectionType) && indices.Length == 1)
			{
				var listIndexer = collectionType.GetProperty("Item", new[] { typeof(int) });
				return Expression.MakeIndex(colExpr, listIndexer, new[] { CoerceToInt(indices[0].ExecuteExpression(parametersParam)) });
			}

			PropertyInfo indexerProp = ResolveIndexer(collectionType, IndexTypes(), indices);
			if (indexerProp == null)
				throw new LanguageException($"Type '{collectionType.Name}' does not support the subscript operator '[]' with {indices.Length} index(es) in compiled mode.");

			ParameterInfo[] indexParams = indexerProp.GetIndexParameters();
			Expression[] keyExprs = new Expression[indices.Length];
			for (int i = 0; i < indices.Length; i++)
			{
				Type paramType = indexParams[i].ParameterType;
				if (paramType.IsEnum && ClassifyEnumArg(indices[i]) != EnumArgKind.NotEnumBindable)
					keyExprs[i] = ParseEnumArgExpression(paramType, indices[i], parametersParam);
				else
					keyExprs[i] = indices[i].ExecuteExpression(parametersParam);
			}

			Expression[] boundKeys = BindValueExpressionsToParameters(indexParams, keyExprs);
			return Expression.MakeIndex(colExpr, indexerProp, boundKeys);
		}

		// Lado izquierdo (L-value) interpretado: coleccion[clave] = value.
		internal void ExecuteAssignment(object value)
		{
			object col = coleccion.Execute();

			if (col == null)
				throw new LanguageException("The collection is null when trying to assign it by index.");

			Type collectionType = col.GetType();

			if (col is Array array)
			{
				int[] arrayIndices = new int[indices.Length];
				for (int i = 0; i < indices.Length; i++)
					arrayIndices[i] = (int)indices[i].Execute();
				array.SetValue(CoerceValue(value, array.GetType().GetElementType()), arrayIndices);
				return;
			}

			if (col is IList builtinList && IsBuiltinGenericList(collectionType) && indices.Length == 1)
			{
				Type elementType = collectionType.GetGenericArguments()[0];
				builtinList[(int)indices[0].Execute()] = CoerceValue(value, elementType);
				return;
			}

			PropertyInfo indexerProp = ResolveIndexer(collectionType, IndexTypes(), indices);
			if (indexerProp == null)
				throw new LanguageException($"Type '{collectionType.Name}' does not support the subscript operator '[]' with {indices.Length} index(es).");
			if (indexerProp.SetMethod == null)
				throw new LanguageException($"The indexer '[]' of type '{collectionType.Name}' is read-only and cannot be assigned.");

			object[] keys = BindIndexValues(indexerProp);
			indexerProp.SetValue(col, CoerceValue(value, indexerProp.PropertyType), keys);
		}

		// Lado izquierdo (L-value) compilado: produce la Expression que escribe value en la celda.
		internal Expression ExecuteAssignmentExpression(ParameterExpression parametersParam, Expression valueExpr)
		{
			ArgumentNullException.ThrowIfNull(valueExpr);

			Expression colExpr = coleccion.ExecuteExpression(parametersParam);
			Type collectionType = colExpr.Type;

			if (collectionType.IsArray)
			{
				Expression[] arrayIndices = new Expression[indices.Length];
				for (int i = 0; i < indices.Length; i++)
					arrayIndices[i] = CoerceToInt(indices[i].ExecuteExpression(parametersParam));
				Expression cell = Expression.ArrayAccess(colExpr, arrayIndices);
				return Expression.Assign(cell, CoerceValueExpression(valueExpr, collectionType.GetElementType()));
			}

			if (IsBuiltinGenericList(collectionType) && indices.Length == 1)
			{
				var listIndexer = collectionType.GetProperty("Item", new[] { typeof(int) });
				Expression cell = Expression.MakeIndex(colExpr, listIndexer, new[] { CoerceToInt(indices[0].ExecuteExpression(parametersParam)) });
				return Expression.Assign(cell, CoerceValueExpression(valueExpr, collectionType.GetGenericArguments()[0]));
			}

			PropertyInfo indexerProp = ResolveIndexer(collectionType, IndexTypes(), indices);
			if (indexerProp == null)
				throw new LanguageException($"Type '{collectionType.Name}' does not support the subscript operator '[]' with {indices.Length} index(es) in compiled mode.");
			if (indexerProp.SetMethod == null)
				throw new LanguageException($"The indexer '[]' of type '{collectionType.Name}' is read-only and cannot be assigned.");

			ParameterInfo[] indexParams = indexerProp.GetIndexParameters();
			Expression[] keyExprs = new Expression[indices.Length];
			for (int i = 0; i < indices.Length; i++)
			{
				Type paramType = indexParams[i].ParameterType;
				if (paramType.IsEnum && ClassifyEnumArg(indices[i]) != EnumArgKind.NotEnumBindable)
					keyExprs[i] = ParseEnumArgExpression(paramType, indices[i], parametersParam);
				else
					keyExprs[i] = indices[i].ExecuteExpression(parametersParam);
			}

			Expression[] boundKeys = BindValueExpressionsToParameters(indexParams, keyExprs);
			Expression indexerCell = Expression.MakeIndex(colExpr, indexerProp, boundKeys);
			return Expression.Assign(indexerCell, CoerceValueExpression(valueExpr, indexerProp.PropertyType));
		}

		private static object CoerceValue(object value, Type targetType)
		{
			if (value == null) return null;
			return TypeConversion.ImplicitCast(value, targetType);
		}

		private static Expression CoerceValueExpression(Expression valueExpr, Type targetType)
		{
			var implicitCastMethod = typeof(TypeConversion).GetMethod(
				nameof(TypeConversion.ImplicitCast),
				BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
			Expression casted = Expression.Call(
				implicitCastMethod,
				Expression.Convert(valueExpr, typeof(object)),
				Expression.Constant(targetType, typeof(Type)));
			return Expression.Convert(casted, targetType);
		}

		private object[] BindIndexValues(PropertyInfo indexerProp)
		{
			ParameterInfo[] indexParams = indexerProp.GetIndexParameters();
			object[] rawArgs = new object[indices.Length];
			for (int i = 0; i < indices.Length; i++)
			{
				Type paramType = indexParams[i].ParameterType;
				if (paramType.IsEnum && ClassifyEnumArg(indices[i]) != EnumArgKind.NotEnumBindable)
					rawArgs[i] = indices[i];
				else
					rawArgs[i] = indices[i].Execute();
			}
			return BindValuesToParameters(indexParams, rawArgs);
		}

		private Type[] IndexTypes()
		{
			Type[] types = new Type[indices.Length];
			for (int i = 0; i < indices.Length; i++)
				types[i] = indices[i].ComputeType();
			return types;
		}

		private void ValidateAllIndicesAreInt()
		{
			foreach (AstExpression index in indices)
			{
				Type indexType = index.ComputeType();
				if (indexType != null && indexType != typeof(object) && !AreCompatible(indexType, typeof(int)))
					throw new LanguageException($"The index of the subscript operator '[]' must be of type int, but found type '{indexType.Name}'.");
			}
		}

		private string DescribeIndexTypes()
		{
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < indices.Length; i++)
			{
				if (i > 0) sb.Append(", ");
				Type t = indices[i].ComputeType();
				sb.Append(t == null ? "unknown" : t.Name);
			}
			return sb.ToString();
		}

		// Resuelve el indexer C# (this[...] -> propiedad con uno o mas parametros de indice) que
		// mejor cuadra con los tipos de los indices. Solo considera indexers cuya aridad coincide.
		// Por cada parametro un indice puede ligar como: exacto (mismo tipo), enum (binding por
		// nombre/simbolo), o coercionable (numerico / IsAssignableFrom). La preferencia es
		// deterministica: gana el candidato con mas parametros que ligan EXACTO; asi this[int] y
		// this[string], o this[int,int] y this[string,string], no son ambiguos. Un indice con tipo
		// indeterminado (null/object) liga contra cualquier parametro (se difiere a runtime).
		private static PropertyInfo ResolveIndexer(Type collectionType, Type[] indexTypes, AstExpression[] indexNodes)
		{
			PropertyInfo best = null;
			int bestExactCount = -1;

			foreach (PropertyInfo prop in collectionType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				ParameterInfo[] indexParams = prop.GetIndexParameters();
				if (indexParams.Length != indexTypes.Length)
					continue;

				int exactCount;
				if (Matches(indexParams, indexTypes, indexNodes, out exactCount) && exactCount > bestExactCount)
				{
					best = prop;
					bestExactCount = exactCount;
				}
			}

			return best;
		}

		private static bool Matches(ParameterInfo[] indexParams, Type[] indexTypes, AstExpression[] indexNodes, out int exactCount)
		{
			exactCount = 0;
			for (int i = 0; i < indexParams.Length; i++)
			{
				Type paramType = indexParams[i].ParameterType;
				Type argType = indexTypes[i];
				bool indeterminate = argType == null || argType == typeof(object);

				if (!indeterminate && paramType == argType)
				{
					exactCount++;
					continue;
				}

				if (paramType.IsEnum && ClassifyEnumArg(indexNodes[i]) != EnumArgKind.NotEnumBindable
					&& IsEnumArgCompatible(paramType, indexNodes[i]))
					continue;

				if (indeterminate)
					continue;

				if (AreCompatible(argType, paramType))
					continue;

				return false;
			}
			return true;
		}

		private static bool IsBuiltinGenericList(Type t)
		{
			if (t == null || !t.IsGenericType) return false;
			var genericDef = t.GetGenericTypeDefinition();
			return genericDef == typeof(List<>) || genericDef == typeof(IList<>);
		}

		private static Expression CoerceToInt(Expression indexExpr)
		{
			return indexExpr.Type == typeof(int) ? indexExpr : Expression.Convert(indexExpr, typeof(int));
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			coleccion.write(resultado, databaseType);
			resultado.Append('[');
			for (int i = 0; i < indices.Length; i++)
			{
				if (i > 0) resultado.Append(',');
				indices[i].write(resultado, databaseType);
			}
			resultado.Append(']');
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			coleccion.Visit(v);
			foreach (AstExpression index in indices)
				index.Visit(v);
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			coleccion.PreparePatternMatching(patternAst, ref position);
			foreach (AstExpression index in indices)
				index.PreparePatternMatching(patternAst, ref position);
		}
	}
}
