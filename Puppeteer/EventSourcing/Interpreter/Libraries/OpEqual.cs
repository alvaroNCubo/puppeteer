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

			var type1 = e1.ComputeType();
			var type2 = e2.ComputeType();

			// Supported primitive types
			bool isPrimitive(Type t) =>
				t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal) ||
				t == typeof(DateTime) || t == typeof(bool);

			// Comparison between a primitive and object (in any order)
			bool primitivoYObject =
				(isPrimitive(type1) && type2 == typeof(object)) ||
				(isPrimitive(type2) && type1 == typeof(object));

			// Comparison between primitive collections and object collections (in any order)
			bool primitiveAndObjectCollection = false;
			var col1 = AstExpression.TypeOfCollection(type1);
			var col2 = AstExpression.TypeOfCollection(type2);
			if ((col1.IsArray || col1.IsGenericType) && (col2.IsArray || col2.IsGenericType))
			{
				var elem1 = AstExpression.TypeOfCollectionElement(type1);
				var elem2 = AstExpression.TypeOfCollectionElement(type2);
				primitiveAndObjectCollection =
					(isPrimitive(elem1) && elem2 == typeof(object)) ||
					(isPrimitive(elem2) && elem1 == typeof(object));
			}
			// Comparison between a collection and object (in any order)
			bool objectCollection =
				((col1.IsArray || col1.IsGenericType) && type2 == typeof(object)) ||
				((col2.IsArray || col2.IsGenericType) && type1 == typeof(object));

			// Allow comparison between compatible numeric types
			bool ambosNumericos = IsPromotableNumeric(type1) && IsPromotableNumeric(type2);

			// Allow comparison between strings
			bool ambosString = type1 == typeof(string) && type2 == typeof(string);

			// Allow comparison between DateTime values
			bool ambosDateTime = type1 == typeof(DateTime) && type2 == typeof(DateTime);

			// Allow comparison between booleans
			bool ambosBool = type1 == typeof(bool) && type2 == typeof(bool);

			// Allow comparison between collections of the same element type or numeric collections
			bool bothCollections = false;
			if ((col1.IsArray || col1.IsGenericType) && (col2.IsArray || col2.IsGenericType))
			{
				var elem1 = AstExpression.TypeOfCollectionElement(type1);
				var elem2 = AstExpression.TypeOfCollectionElement(type2);
				bothCollections = (elem1 == elem2) ||
				((elem1 == typeof(int) || elem1 == typeof(double) || elem1 == typeof(decimal)) &&
				(elem2 == typeof(int) || elem2 == typeof(double) || elem2 == typeof(decimal)));
			}

			// Allow reference comparison for classes (except string and collections)
			bool bothReference = type1 == type2 && (type1.IsClass || type1.IsInterface)
			&& type1 != typeof(string)
			&& !typeof(System.Collections.IEnumerable).IsAssignableFrom(type1);

			// Allow comparison between object and class, class and object, object and object
			bool objectAndClass =
			(type1 == typeof(object) && type2.IsClass && type2 != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type2)) ||
			(type2 == typeof(object) && type1.IsClass && type1 != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type1)) ||
			(type1 == typeof(object) && type2 == typeof(object));

			// Special case: nullable parameter == null
			bool nullableParamVsNull =
				(e1 is LiteralNull && e2 is Id id2n && id2n.IsNullableParameter) ||
				(e2 is LiteralNull && e1 is Id id1n && id1n.IsNullableParameter);

			if (!(ambosNumericos || ambosString || ambosDateTime || ambosBool || bothCollections || bothReference || objectAndClass
				|| primitivoYObject || primitiveAndObjectCollection || objectCollection || nullableParamVsNull))
			{
				throw new LanguageException($"Cannot compare types '{type1}' and '{type2}' with '=='.");
			}

			ForcedType = typeof(bool);
		}

		internal override object Execute()
		{
			object object1 = e1.Execute();
			object object2 = e2.Execute();

			if (object1 == object2) return true;

			Type type1 = object1 == null ? null : object1.GetType();
			Type type2 = object2 == null ? null : object2.GetType();

			if (type1 != null && type2 != null && IsPromotableNumeric(type1) && IsPromotableNumeric(type2))
			{
				Type promoted = PromotesTo(type1, type2);
				object a = CoerceNumericValue(object1, promoted);
				object b = CoerceNumericValue(object2, promoted);
				if (promoted == typeof(int)) return (int)a == (int)b;
				if (promoted == typeof(long)) return (long)a == (long)b;
				if (promoted == typeof(double)) return (double)a == (double)b;
				return (decimal)a == (decimal)b;
			}

			if (type1 == typeof(string) && type2 == typeof(string))
				return (string)object1 == (string)object2;

			if (type1 == typeof(DateTime) && type2 == typeof(DateTime))
				return (DateTime)object1 == (DateTime)object2;

			if (type1 == typeof(bool) && type2 == typeof(bool))
				return (bool)object1 == (bool)object2;

			// Numeric collection handling with conversion
			var colType1 = AstExpression.TypeOfCollection(type1);
			var colType2 = AstExpression.TypeOfCollection(type2);
			if ((colType1.IsArray || colType1.IsGenericType) && (colType2.IsArray || colType2.IsGenericType))
			{
				Type elemType1 = AstExpression.TypeOfCollectionElement(type1);
				Type elemType2 = AstExpression.TypeOfCollectionElement(type2);

				// If both are numeric (int, double, decimal), compare element-by-element with conversion
				if (IsNumericType(elemType1) && IsNumericType(elemType2))
				{
					return SequenceCompareNumeric(ToObjectEnumerable(object1), ToObjectEnumerable(object2));
				}
				// If they are the same type, use the original comparer
				if (elemType1 == elemType2)
				{
					if (elemType1 == typeof(int))
						return SequenceCompare<int>((IEnumerable<int>)object1, (IEnumerable<int>)object2);
					else if (elemType1 == typeof(string))
						return SequenceCompare<string>((IEnumerable<string>)object1, (IEnumerable<string>)object2);
					else if (elemType1 == typeof(double))
						return SequenceCompare<double>((IEnumerable<double>)object1, (IEnumerable<double>)object2);
					else if (elemType1 == typeof(bool))
						return SequenceCompare<bool>((IEnumerable<bool>)object1, (IEnumerable<bool>)object2);
					else if (elemType1 == typeof(decimal))
						return SequenceCompare<decimal>((IEnumerable<decimal>)object1, (IEnumerable<decimal>)object2);
					else if (elemType1 == typeof(DateTime))
						return SequenceCompare<DateTime>((IEnumerable<DateTime>)object1, (IEnumerable<DateTime>)object2);
					else
						return SequenceCompare<object>((IEnumerable<object>)object1, (IEnumerable<object>)object2);
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
					Type elemType1 = AstExpression.TypeOfCollectionElement(type1);
					Type elemType2 = AstExpression.TypeOfCollectionElement(type2);

					// If both are numeric (int, double, decimal), compare element-by-element with conversion
					if (IsNumericType(elemType1) && IsNumericType(elemType2))
					{
						// Convert both to IEnumerable<object>
						IEnumerable<object> enum1 = ToObjectEnumerable(object1);
						IEnumerable<object> enum2 = ToObjectEnumerable(object2);
						return SequenceCompareNumeric(enum1, enum2);
					}
					// If they are the same type, use the original comparer
					if (elemType1 == elemType2)
					{
						if (elemType1 == typeof(int))
							return SequenceCompare<int>(ToTypedEnumerable<int>(object1), ToTypedEnumerable<int>(object2));
						else if (elemType1 == typeof(string))
							return SequenceCompare<string>(ToTypedEnumerable<string>(object1), ToTypedEnumerable<string>(object2));
						else if (elemType1 == typeof(double))
							return SequenceCompare<double>(ToTypedEnumerable<double>(object1), ToTypedEnumerable<double>(object2));
						else if (elemType1 == typeof(bool))
							return SequenceCompare<bool>(ToTypedEnumerable<bool>(object1), ToTypedEnumerable<bool>(object2));
						else if (elemType1 == typeof(decimal))
							return SequenceCompare<decimal>(ToTypedEnumerable<decimal>(object1), ToTypedEnumerable<decimal>(object2));
						else if (elemType1 == typeof(DateTime))
							return SequenceCompare<DateTime>(ToTypedEnumerable<DateTime>(object1), ToTypedEnumerable<DateTime>(object2));
						else
							return SequenceCompare<object>(ToObjectEnumerable(object1), ToObjectEnumerable(object2));
					}
					else
					{
						return false;
					}
				}

				return false;
			}

			return (object2 != null && object2.Equals(object1)) || (object1 != null && object1.Equals(object2));
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
					var objectField = typeof(VariableSymbol).GetField(
						nameof(VariableSymbol.value),
						System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
					var rawObject = Expression.Field(paramDecl, objectField);
					return Expression.Equal(rawObject, Expression.Constant(null, typeof(object)));
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
			return type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal);
		}

		private static Type GetWidestNumericType(Type t1, Type t2)
		{
			if (t1 == typeof(decimal) || t2 == typeof(decimal))
				return typeof(decimal);
			if (t1 == typeof(double) || t2 == typeof(double))
				return typeof(double);
			if (t1 == typeof(long) || t2 == typeof(long))
				return typeof(long);
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

			if (!IsNumericType(ta) || !IsNumericType(tb))
			{
				// If they are not compatible numeric types, they are not equal
				return false;
			}

			// Compare at the widest common numeric type (long now included, between int and double).
			Type widest = GetWidestNumericType(ta, tb);
			if (widest == typeof(int)) return Convert.ToInt32(a) == Convert.ToInt32(b);
			if (widest == typeof(long)) return Convert.ToInt64(a) == Convert.ToInt64(b);
			if (widest == typeof(double)) return Convert.ToDouble(a) == Convert.ToDouble(b);
			return Convert.ToDecimal(a) == Convert.ToDecimal(b);
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

		internal override void write(StringBuilder result, DatabaseType databaseType)
		{
			e1.write(result, databaseType);
			result.Append(" == ");
			e2.write(result, databaseType);
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
