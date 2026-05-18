using Puppeteer.EventSourcing.Follower;
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
		private readonly AstExpression indice;

		internal SubscriptAstExpression(AstExpression coleccion, AstExpression indice)
		{
			ArgumentNullException.ThrowIfNull(coleccion);
			ArgumentNullException.ThrowIfNull(indice);
			this.coleccion = coleccion;
			this.indice = indice;
		}

		internal override Type ComputeType()
		{
			Type collectionType = coleccion.ComputeType();
			if (collectionType == null)
				throw new LanguageException("Cannot determine the collection type for the subscript operator '[]'.");

			if (collectionType.IsArray)
				return collectionType.GetElementType();

			if (collectionType.IsGenericType)
			{
				var genericDef = collectionType.GetGenericTypeDefinition();
				if (genericDef == typeof(List<>) || genericDef == typeof(IList<>))
					return collectionType.GetGenericArguments()[0];
			}

			var indexerProp = collectionType.GetProperty("Item", new[] { typeof(int) });
			if (indexerProp != null)
				return indexerProp.PropertyType;

			throw new LanguageException($"Type '{collectionType.Name}' does not support the subscript operator '[]'.");
		}

		internal override void ValidateStatically()
		{
			coleccion.ValidateStatically();
			indice.ValidateStatically();

			Type indexType = indice.ComputeType();
			if (indexType != typeof(int))
				throw new LanguageException($"The index of the subscript operator '[]' must be of type int, but found type '{indexType.Name}'.");

			ForcedType = ComputeType();
		}

		internal override object Execute()
		{
			object col = coleccion.Execute();
			object idx = indice.Execute();

			if (col == null)
				throw new LanguageException("The collection is null when trying to access it by index.");

			int i = (int)idx;

			if (col is IList list)
				return list[i];

			if (col is Array arr)
				return arr.GetValue(i);

			var indexerProp = col.GetType().GetProperty("Item", new[] { typeof(int) });
			if (indexerProp != null)
				return indexerProp.GetValue(col, new object[] { i });

			throw new LanguageException($"Type '{col.GetType().Name}' does not support the subscript operator '[]'.");
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			Expression colExpr = coleccion.ExecuteExpression(parametersParam);
			Expression idxExpr = indice.ExecuteExpression(parametersParam);

			if (idxExpr.Type != typeof(int))
				idxExpr = Expression.Convert(idxExpr, typeof(int));

			Type collectionType = colExpr.Type;

			if (collectionType.IsArray)
				return Expression.ArrayIndex(colExpr, idxExpr);

			if (collectionType.IsGenericType)
			{
				var genericDef = collectionType.GetGenericTypeDefinition();
				if (genericDef == typeof(List<>) || genericDef == typeof(IList<>))
				{
					var indexerProp = collectionType.GetProperty("Item", new[] { typeof(int) });
					return Expression.MakeIndex(colExpr, indexerProp, new[] { idxExpr });
				}
			}

			var itemProp = collectionType.GetProperty("Item", new[] { typeof(int) });
			if (itemProp != null)
				return Expression.MakeIndex(colExpr, itemProp, new[] { idxExpr });

			throw new LanguageException($"Type '{collectionType.Name}' does not support the subscript operator '[]' in compiled mode.");
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			coleccion.write(resultado, databaseType);
			resultado.Append('[');
			indice.write(resultado, databaseType);
			resultado.Append(']');
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			coleccion.Visit(v);
			indice.Visit(v);
		}

		internal override void PreparePatternMatching(PatternListNode patternAst, ref int position)
		{
			coleccion.PreparePatternMatching(patternAst, ref position);
			indice.PreparePatternMatching(patternAst, ref position);
		}
	}
}
