using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	class OpEqual : BinaryAstExpression
	{
		internal OpEqual(AstExpression e1, AstExpression e2) : base(e1, e2)
		{
		}

		internal override Type ComputeType()
		{
			return typeof(bool);
		}

		internal override void ValidateStatically()
		{
			e1.ValidateStatically();
			e2.ValidateStatically();

			var tipo1 = e1.ComputeType();
			var tipo2 = e2.ComputeType();

			// Supported primitive types
			bool esPrimitivo(Type t) =>
				t == typeof(int) || t == typeof(double) || t == typeof(decimal) ||
				t == typeof(DateTime) || t == typeof(bool);

			// Comparison between a primitive and object (in any order)
			bool primitivoYObject =
				(esPrimitivo(tipo1) && tipo2 == typeof(object)) ||
				(esPrimitivo(tipo2) && tipo1 == typeof(object));

			// Comparison between primitive collections and object collections (in any order)
			bool coleccionPrimitivoYObject = false;
			var col1 = AstExpression.TypeOfCollection(tipo1);
			var col2 = AstExpression.TypeOfCollection(tipo2);
			if ((col1.IsArray || col1.IsGenericType) && (col2.IsArray || col2.IsGenericType))
			{
				var elem1 = AstExpression.TypeOfCollectionElement(tipo1);
				var elem2 = AstExpression.TypeOfCollectionElement(tipo2);
				coleccionPrimitivoYObject =
					(esPrimitivo(elem1) && elem2 == typeof(object)) ||
					(esPrimitivo(elem2) && elem1 == typeof(object));
			}
			// Comparison between a collection and object (in any order)
			bool coleccionYObject =
				((col1.IsArray || col1.IsGenericType) && tipo2 == typeof(object)) ||
				((col2.IsArray || col2.IsGenericType) && tipo1 == typeof(object));

			// Allow comparison between compatible numeric types
			bool ambosNumericos = (tipo1 == typeof(int) || tipo1 == typeof(double) || tipo1 == typeof(decimal))
			&& (tipo2 == typeof(int) || tipo2 == typeof(double) || tipo2 == typeof(decimal));

			// Allow comparison between strings
			bool ambosString = tipo1 == typeof(string) && tipo2 == typeof(string);

			// Allow comparison between DateTime values
			bool ambosDateTime = tipo1 == typeof(DateTime) && tipo2 == typeof(DateTime);

			// Allow comparison between booleans
			bool ambosBool = tipo1 == typeof(bool) && tipo2 == typeof(bool);

			// Allow comparison between collections of the same element type or numeric collections
			bool ambosColeccion = false;
			if ((col1.IsArray || col1.IsGenericType) && (col2.IsArray || col2.IsGenericType))
			{
				var elem1 = AstExpression.TypeOfCollectionElement(tipo1);
				var elem2 = AstExpression.TypeOfCollectionElement(tipo2);
				ambosColeccion = (elem1 == elem2) ||
				((elem1 == typeof(int) || elem1 == typeof(double) || elem1 == typeof(decimal)) &&
				(elem2 == typeof(int) || elem2 == typeof(double) || elem2 == typeof(decimal)));
			}

			// Allow reference comparison for classes (except string and collections)
			bool ambosReferencia = tipo1 == tipo2 && (tipo1.IsClass || tipo1.IsInterface)
			&& tipo1 != typeof(string)
			&& !typeof(System.Collections.IEnumerable).IsAssignableFrom(tipo1);

			// Allow comparison between object and class, class and object, object and object
			bool objectYClase =
			(tipo1 == typeof(object) && tipo2.IsClass && tipo2 != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(tipo2)) ||
			(tipo2 == typeof(object) && tipo1.IsClass && tipo1 != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(tipo1)) ||
			(tipo1 == typeof(object) && tipo2 == typeof(object));

			// Special case: nullable parameter == null
			bool nullableParamVsNull =
				(e1 is LiteralNull && e2 is Id id2n && id2n.IsNullableParameter) ||
				(e2 is LiteralNull && e1 is Id id1n && id1n.IsNullableParameter);

			if (!(ambosNumericos || ambosString || ambosDateTime || ambosBool || ambosColeccion || ambosReferencia || objectYClase
				|| primitivoYObject || coleccionPrimitivoYObject || coleccionYObject || nullableParamVsNull))
			{
				throw new LanguageException($"Cannot compare types '{tipo1}' and '{tipo2}' with '=='.");
			}

			ForcedType = typeof(bool);
		}

		internal override object Execute()
		{
			object objeto1 = e1.Execute();
			object objeto2 = e2.Execute();

			if (objeto1 == objeto2) return true;

			Type tipo1 = objeto1 == null ? null : objeto1.GetType();
			Type tipo2 = objeto2 == null ? null : objeto2.GetType();

			if (tipo1 == typeof(int) && tipo2 == typeof(int))
				return (int)objeto1 == (int)objeto2;

			if (tipo1 == typeof(int) && tipo2 == typeof(double))
				return Convert.ToDouble(objeto1) == (double)objeto2;

			if (tipo1 == typeof(int) && tipo2 == typeof(decimal))
				return Convert.ToDecimal(objeto1) == (decimal)objeto2;

			if (tipo1 == typeof(double) && tipo2 == typeof(int))
				return (double)objeto1 == Convert.ToDouble(objeto2);

			if (tipo1 == typeof(double) && tipo2 == typeof(double))
				return (double)objeto1 == (double)objeto2;

			if (tipo1 == typeof(double) && tipo2 == typeof(decimal))
				return Convert.ToDecimal(objeto1) == (decimal)objeto2;

			if (tipo1 == typeof(decimal) && tipo2 == typeof(int))
				return (decimal)objeto1 == Convert.ToDecimal(objeto2);

			if (tipo1 == typeof(decimal) && tipo2 == typeof(double))
				return (decimal)objeto1 == Convert.ToDecimal(objeto2);

			if (tipo1 == typeof(decimal) && tipo2 == typeof(decimal))
				return (decimal)objeto1 == (decimal)objeto2;

			if (tipo1 == typeof(string) && tipo2 == typeof(string))
				return (string)objeto1 == (string)objeto2;

			if (tipo1 == typeof(DateTime) && tipo2 == typeof(DateTime))
				return (DateTime)objeto1 == (DateTime)objeto2;

			if (tipo1 == typeof(bool) && tipo2 == typeof(bool))
				return (bool)objeto1 == (bool)objeto2;

			// Numeric collection handling with conversion
			var colType1 = AstExpression.TypeOfCollection(tipo1);
			var colType2 = AstExpression.TypeOfCollection(tipo2);
			if ((colType1.IsArray || colType1.IsGenericType) && (colType2.IsArray || colType2.IsGenericType))
			{
				Type elemType1 = AstExpression.TypeOfCollectionElement(tipo1);
				Type elemType2 = AstExpression.TypeOfCollectionElement(tipo2);

				// If both are numeric (int, double, decimal), compare element-by-element with conversion
				if (IsNumericType(elemType1) && IsNumericType(elemType2))
				{
					return SequenceCompareNumeric(ToObjectEnumerable(objeto1), ToObjectEnumerable(objeto2));
				}
				// If they are the same type, use the original comparer
				if (elemType1 == elemType2)
				{
					if (elemType1 == typeof(int))
						return SequenceCompare<int>((IEnumerable<int>)objeto1, (IEnumerable<int>)objeto2);
					else if (elemType1 == typeof(string))
						return SequenceCompare<string>((IEnumerable<string>)objeto1, (IEnumerable<string>)objeto2);
					else if (elemType1 == typeof(double))
						return SequenceCompare<double>((IEnumerable<double>)objeto1, (IEnumerable<double>)objeto2);
					else if (elemType1 == typeof(bool))
						return SequenceCompare<bool>((IEnumerable<bool>)objeto1, (IEnumerable<bool>)objeto2);
					else if (elemType1 == typeof(decimal))
						return SequenceCompare<decimal>((IEnumerable<decimal>)objeto1, (IEnumerable<decimal>)objeto2);
					else if (elemType1 == typeof(DateTime))
						return SequenceCompare<DateTime>((IEnumerable<DateTime>)objeto1, (IEnumerable<DateTime>)objeto2);
					else
						return SequenceCompare<object>((IEnumerable<object>)objeto1, (IEnumerable<object>)objeto2);
				}
				else
				{
					return false;
				}
			}
			else if (
				(colType1.IsArray || colType1.IsGenericType) ||
				(colType2.IsArray || colType2.IsGenericType)
			)
			{
				// Both are collections (one array and one generic collection, or both generic collections)
				if ((colType1.IsArray || colType1.IsGenericType) && (colType2.IsArray || colType2.IsGenericType))
				{
					Type elemType1 = AstExpression.TypeOfCollectionElement(tipo1);
					Type elemType2 = AstExpression.TypeOfCollectionElement(tipo2);

					// If both are numeric (int, double, decimal), compare element-by-element with conversion
					if (IsNumericType(elemType1) && IsNumericType(elemType2))
					{
						// Convert both to IEnumerable<object>
						IEnumerable<object> enum1 = ToObjectEnumerable(objeto1);
						IEnumerable<object> enum2 = ToObjectEnumerable(objeto2);
						return SequenceCompareNumeric(enum1, enum2);
					}
					// If they are the same type, use the original comparer
					if (elemType1 == elemType2)
					{
						if (elemType1 == typeof(int))
							return SequenceCompare<int>(ToTypedEnumerable<int>(objeto1), ToTypedEnumerable<int>(objeto2));
						else if (elemType1 == typeof(string))
							return SequenceCompare<string>(ToTypedEnumerable<string>(objeto1), ToTypedEnumerable<string>(objeto2));
						else if (elemType1 == typeof(double))
							return SequenceCompare<double>(ToTypedEnumerable<double>(objeto1), ToTypedEnumerable<double>(objeto2));
						else if (elemType1 == typeof(bool))
							return SequenceCompare<bool>(ToTypedEnumerable<bool>(objeto1), ToTypedEnumerable<bool>(objeto2));
						else if (elemType1 == typeof(decimal))
							return SequenceCompare<decimal>(ToTypedEnumerable<decimal>(objeto1), ToTypedEnumerable<decimal>(objeto2));
						else if (elemType1 == typeof(DateTime))
							return SequenceCompare<DateTime>(ToTypedEnumerable<DateTime>(objeto1), ToTypedEnumerable<DateTime>(objeto2));
						else
							return SequenceCompare<object>(ToObjectEnumerable(objeto1), ToObjectEnumerable(objeto2));
					}
					else
					{
						return false;
					}
				}

				return false;
			}

			return (objeto2 != null && objeto2.Equals(objeto1)) || (objeto1 != null && objeto1.Equals(objeto2));
		}

		internal override Expression ExecuteExpression(ParameterExpression parametersParam)
		{
			// Special case: nullable parameter == null
			// Reads VariableSymbol.value directly (type object) to avoid unboxing value types
			{
				Id paramId = null;
				if (e1 is LiteralNull && e2 is Id rightId && rightId.IsNullableParameter)
					paramId = rightId;
				else if (e2 is LiteralNull && e1 is Id leftId && leftId.IsNullableParameter)
					paramId = leftId;

				if (paramId != null)
				{
					var paramDecl = paramId.Parameter.ParameterDeclarationExpression();
					var objetoField = typeof(VariableSymbol).GetField(
						nameof(VariableSymbol.value),
						System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
					var rawObjeto = Expression.Field(paramDecl, objetoField);
					return Expression.Equal(rawObjeto, Expression.Constant(null, typeof(object)));
				}
			}

			var leftExpr = e1.ExecuteExpression(parametersParam);
			var rightExpr = e2.ExecuteExpression(parametersParam);

			// If both are constants, evaluate at compile time
			if (leftExpr is ConstantExpression && rightExpr is ConstantExpression)
			{
				var result = Execute();
				return Expression.Constant(result, typeof(bool));
			}

			var leftType = leftExpr.Type;
			var rightType = rightExpr.Type;

			if (leftType == rightType && (leftType.IsClass || leftType.IsInterface)
				&& leftType != typeof(string)
				&& !typeof(System.Collections.IEnumerable).IsAssignableFrom(leftType))
			{
				return Expression.ReferenceEqual(leftExpr, rightExpr);
			}

			// Numeric type comparison
			if (IsNumericType(leftType) && IsNumericType(rightType))
			{
				Expression leftNum = leftExpr;
				Expression rightNum = rightExpr;
				Type targetType = GetWidestNumericType(leftType, rightType);
				if (leftType != targetType)
					leftNum = Expression.Convert(leftExpr, targetType);
				if (rightType != targetType)
					rightNum = Expression.Convert(rightExpr, targetType);
				return Expression.Equal(leftNum, rightNum);
			}

			// String comparison
			if (leftType == typeof(string) && rightType == typeof(string))
			{
				return Expression.Equal(leftExpr, rightExpr);
			}

			// DateTime comparison
			if (leftType == typeof(DateTime) && rightType == typeof(DateTime))
			{
				return Expression.Equal(leftExpr, rightExpr);
			}

			// Boolean comparison
			if (leftType == typeof(bool) && rightType == typeof(bool))
			{
				return Expression.Equal(leftExpr, rightExpr);
			}

			// Collection comparison
			var colType1 = AstExpression.TypeOfCollection(leftType);
			var colType2 = AstExpression.TypeOfCollection(rightType);
			if ((colType1.IsArray || colType1.IsGenericType) && (colType2.IsArray || colType2.IsGenericType))
			{
				Type elemType1 = AstExpression.TypeOfCollectionElement(leftType);
				Type elemType2 = AstExpression.TypeOfCollectionElement(rightType);

				// Both numeric
				if (IsNumericType(elemType1) && IsNumericType(elemType2))
				{
					return Expression.Call(
						typeof(OpEqual).GetMethod(nameof(SequenceCompareNumeric), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
						Expression.Call(
							typeof(OpEqual).GetMethod(nameof(ToObjectEnumerable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
							Expression.Convert(leftExpr, typeof(object))
						),
						Expression.Call(
							typeof(OpEqual).GetMethod(nameof(ToObjectEnumerable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
							Expression.Convert(rightExpr, typeof(object))
						)
					);
				}
				// Same type
				if (elemType1 == elemType2)
				{
					// Use SequenceCompare<T> instead of BuildSequenceCompareExpression
					var seqCompareMethod = typeof(OpEqual).GetMethod(nameof(SequenceCompare), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
						.MakeGenericMethod(elemType1);
					return Expression.Call(
						seqCompareMethod,
						Expression.Call(
							typeof(OpEqual).GetMethod(nameof(ToTypedEnumerable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
								.MakeGenericMethod(elemType1),
							Expression.Convert(leftExpr, typeof(object))
						),
						Expression.Call(
							typeof(OpEqual).GetMethod(nameof(ToTypedEnumerable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
								.MakeGenericMethod(elemType1),
							Expression.Convert(rightExpr, typeof(object))
						)
					);
				}
				else
				{
					return Expression.Constant(false, typeof(bool));
				}
			}
			else if ((colType1.IsArray || colType1.IsGenericType) || (colType2.IsArray || colType2.IsGenericType))
			{
				if ((colType1.IsArray || colType1.IsGenericType) && (colType2.IsArray || colType2.IsGenericType))
				{
					Type elemType1 = AstExpression.TypeOfCollectionElement(leftType);
					Type elemType2 = AstExpression.TypeOfCollectionElement(rightType);

					if (IsNumericType(elemType1) && IsNumericType(elemType2))
					{
						return Expression.Call(
							typeof(OpEqual).GetMethod(nameof(SequenceCompareNumeric), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
							Expression.Call(
								typeof(OpEqual).GetMethod(nameof(ToObjectEnumerable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
								Expression.Convert(leftExpr, typeof(object))
							),
							Expression.Call(
								typeof(OpEqual).GetMethod(nameof(ToObjectEnumerable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
								Expression.Convert(rightExpr, typeof(object))
							)
						);
					}
					if (elemType1 == elemType2)
					{
						var seqCompareMethod = typeof(OpEqual).GetMethod(nameof(SequenceCompare), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
							.MakeGenericMethod(elemType1);
						return Expression.Call(
							seqCompareMethod,
							Expression.Call(
								typeof(OpEqual).GetMethod(nameof(ToTypedEnumerable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
									.MakeGenericMethod(elemType1),
							Expression.Convert(leftExpr, typeof(object))
						),
						Expression.Call(
							typeof(OpEqual).GetMethod(nameof(ToTypedEnumerable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
								.MakeGenericMethod(elemType1),
							Expression.Convert(rightExpr, typeof(object))
						)
						);
					}
					else
					{
						return Expression.Constant(false, typeof(bool));
					}
				}
				return Expression.Constant(false, typeof(bool));
			}

			// Fallback: .Equals
			return Expression.Call(
				typeof(object).GetMethod(nameof(object.Equals), new[] { typeof(object), typeof(object) }),
				Expression.Convert(leftExpr, typeof(object)),
				Expression.Convert(rightExpr, typeof(object))
			);
		}


		// Expression helpers
		private static bool IsNumericType(Type type)
		{
			return type == typeof(int) || type == typeof(double) || type == typeof(decimal);
		}

		private static Type GetWidestNumericType(Type t1, Type t2)
		{
			if (t1 == typeof(decimal) || t2 == typeof(decimal))
				return typeof(decimal);
			if (t1 == typeof(double) || t2 == typeof(double))
				return typeof(double);
			return typeof(int);
		}

		// Helpers to convert to IEnumerable<T> or IEnumerable<object>
		private static IEnumerable<object> ToObjectEnumerable(object collection)
		{
			if (collection is IEnumerable<object> objEnum)
			{
				foreach (var item in objEnum)
				{
					yield return item;
				}
			}
			else if (collection is System.Collections.IEnumerable enumObj)
			{
				foreach (var item in enumObj)
				{
					yield return item;
				}
			}
		}

		private static IEnumerable<T> ToTypedEnumerable<T>(object collection)
		{
			if (collection is IEnumerable<T> typedEnum)
			{
				foreach (var item in typedEnum)
				{
					yield return item;
				}
			}
			else if (collection is System.Collections.IEnumerable enumObj)
			{
				foreach (var item in enumObj)
				{
					yield return (T)Convert.ChangeType(item, typeof(T));
				}
			}
		}

		// Numeric sequence comparator with conversion
		private static bool SequenceCompareNumeric(IEnumerable<object> source1, IEnumerable<object> source2)
		{
			using (var iterator1 = source1.GetEnumerator())
			using (var iterator2 = source2.GetEnumerator())
			{
				while (true)
				{
					bool next1 = iterator1.MoveNext();
					bool next2 = iterator2.MoveNext();
					if (!next1 && !next2)
						return true;
					if (!next1 || !next2)
						return false;

					if (!NumericEquals(iterator1.Current, iterator2.Current))
						return false;
				}
			}
		}

		// Compares two numeric objects (int, double, decimal) with conversion
		private static bool NumericEquals(object a, object b)
		{
			if (a == null || b == null) return a == b;

			Type ta = a.GetType();
			Type tb = b.GetType();

			if (ta == typeof(int) && tb == typeof(int))
				return (int)a == (int)b;
			if (ta == typeof(int) && tb == typeof(double))
				return Convert.ToDouble(a) == (double)b;
			if (ta == typeof(int) && tb == typeof(decimal))
				return Convert.ToDecimal(a) == (decimal)b;
			if (ta == typeof(double) && tb == typeof(int))
				return (double)a == Convert.ToDouble(b);
			if (ta == typeof(double) || tb == typeof(double))
				return Convert.ToDouble(a) == Convert.ToDouble(b);
			if (ta == typeof(double) && tb == typeof(decimal))
				return Convert.ToDecimal(a) == (decimal)b;
			if (ta == typeof(decimal) && tb == typeof(int))
				return (decimal)a == Convert.ToDecimal(b);
			if (ta == typeof(decimal) && tb == typeof(double))
				return (decimal)a == Convert.ToDecimal(b);
			if (ta == typeof(decimal) || tb == typeof(decimal))
				return Convert.ToDecimal(a) == Convert.ToDecimal(b);
			// If they are not compatible numeric types, they are not equal
			return false;
		}

		private static bool SequenceCompare<T>(IEnumerable<T> source1, IEnumerable<T> source2)
		{
			Comparer<T> elementComparer = Comparer<T>.Default;
			EqualityComparer<T> equalsComparer = EqualityComparer<T>.Default;
			var t = typeof(T);
			var hasComparer = typeof(IComparable).IsAssignableFrom(t);
			using (IEnumerator<T> iterator1 = source1.GetEnumerator())
			using (IEnumerator<T> iterator2 = source2.GetEnumerator())
			{
				while (true)
				{
					bool next1 = iterator1.MoveNext();
					bool next2 = iterator2.MoveNext();
					if (!next1 && !next2) // Both sequences finished
					{
						return true;
					}
					if (!next1) // Only the first sequence has finished
					{
						return false;
					}
					if (!next2) // Only the second sequence has finished
					{
						return false;
					}
					// Both are still going, compare current elements
					if (hasComparer)
					{
						int comparison = elementComparer.Compare(iterator1.Current,
																	iterator2.Current);
						// If elements are non-equal, we're done
						if (comparison != 0)
						{
							return false;
						}
					}
					else
					{
						if (!equalsComparer.Equals(iterator1.Current, iterator2.Current)) return false;
					}
				}
			}
		}

		internal override void write(StringBuilder resultado, DatabaseType databaseType)
		{
			e1.write(resultado, databaseType);
			resultado.Append(" == ");
			e2.write(resultado, databaseType);
		}

		internal override void Visit(ASTVisitor v)
		{
			if (this.GetType() == v.Target)
			{
				v.OnVisit(this);
			}
			e1.Visit(v);
			e2.Visit(v);
		}

	}
}
