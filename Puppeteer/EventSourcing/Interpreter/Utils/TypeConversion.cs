using System;
using System.Collections;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Interpreter.Utils
{
	internal class TypeConversion
	{



		internal static object ImplicitCast(object value, Type target)
		{
			Type actual = value.GetType();
			if (actual == target) return value;

			object result = null;
			if (actual == typeof(int) && target == typeof(double))
			{
				result = (double)value;
			}
			else if (actual == typeof(int) && target == typeof(decimal))
			{
				result = Convert.ToDecimal((int)value);
			}
			else if (actual == typeof(double) && target == typeof(decimal))
			{
				result = Convert.ToDecimal((double)value);
			}
			else if (actual == typeof(decimal) && target == typeof(double))
			{
				result = System.Decimal.ToDouble((decimal)value);
			}
			// string -> char: a DTO often carries a char as a single-character string. A length-1
			// string coerces to its one char; any other length is a clear error (never a silent
			// truncation). Mirrors the string<->enum coercion applied at the parameter boundary.
			else if (actual == typeof(string) && target == typeof(char))
			{
				result = StringToChar((string)value);
			}
			// Collection analog: string[] / List<string> / IEnumerable<string> -> char[] or List<char>,
			// coercing each element the same way (length-1 per element, else a clear error). Guarded on a
			// string element source so a List<char>/char[] source falls through to the generic handling below.
			else if (target.IsArray && target.GetElementType() == typeof(char) && IsStringCollection(actual))
			{
				List<char> chars = new List<char>();
				foreach (var element in (IEnumerable)value) chars.Add(StringToChar((string)element));
				char[] res = new char[chars.Count];
				for (int i = 0; i < chars.Count; i++) res[i] = chars[i];
				return res;
			}
			else if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>)
					 && target.GetGenericArguments()[0] == typeof(char) && IsStringCollection(actual))
			{
				List<char> res = new List<char>();
				foreach (var element in (IEnumerable)value) res.Add(StringToChar((string)element));
				return res;
			}
			else if (target.IsArray && actual == typeof(List<int>))
			{
				List<int> typedValue = value as List<int>;
				int[] res = new int[typedValue.Count];
				for (int i = 0; i < typedValue.Count; i++) res[i] = typedValue[i];
				return res;
			}
			else if (target.IsArray && actual == typeof(List<string>))
			{
				List<string> typedValue = value as List<string>;
				string[] res = new string[typedValue.Count];
				for (int i = 0; i < typedValue.Count; i++) res[i] = typedValue[i];
				return res;
			}
			else if (target.IsArray && actual == typeof(List<DateTime>))
			{
				List<DateTime> typedValue = value as List<DateTime>;
				DateTime[] res = new DateTime[typedValue.Count];
				for (int i = 0; i < typedValue.Count; i++) res[i] = typedValue[i];
				return res;
			}
			else if (target.IsArray && actual == typeof(List<double>))
			{
				List<double> typedValue = value as List<double>;

				if (target.GetElementType() == typeof(double))
				{
					double[] res = new double[typedValue.Count];
					for (int i = 0; i < typedValue.Count; i++) res[i] = typedValue[i];
					return res;
				}
				else if (target.GetElementType() == typeof(Decimal))
				{
					Decimal[] res = new Decimal[typedValue.Count];
					for (int i = 0; i < typedValue.Count; i++) res[i] = (Decimal)typedValue[i];
					return res;
				}
			}
			else if (target.IsArray && actual == typeof(List<bool>))
			{
				List<bool> typedValue = value as List<bool>;
				bool[] res = new bool[typedValue.Count];
				for (int i = 0; i < typedValue.Count; i++) res[i] = typedValue[i];
				return res;
			}
			else if (target.IsArray && actual.IsGenericType && actual.GetGenericTypeDefinition() == typeof(List<>) && actual.GetGenericArguments().Length == 1)
			{
				Type listType = actual.GetGenericArguments()[0];
				IList typedValue = (IList)value;
				System.Array res = Array.CreateInstance(listType, typedValue.Count);
				for (int i = 0; i < typedValue.Count; i++) res.SetValue(typedValue[i], i);
				return res;
			}
			else if (target.IsArray && actual == typeof(List<Decimal>))
			{
				List<Decimal> typedValue = value as List<Decimal>;

				if (target.GetElementType() == typeof(double))
				{
					double[] res = new double[typedValue.Count];
					for (int i = 0; i < typedValue.Count; i++) res[i] = (double)typedValue[i];
					return res;
				}
				else if (target.GetElementType() == typeof(Decimal))
				{
					Decimal[] res = new Decimal[typedValue.Count];
					for (int i = 0; i < typedValue.Count; i++) res[i] = typedValue[i];
					return res;
				}
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(int))
			{
				int[] typedValue = (int[])value;

				List<int> res = new List<int>(typedValue.Length);
				for (int i = 0; i < typedValue.Length; i++) res.Add(typedValue[i]);
				return res;

			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(string))
			{
				string[] typedValue = (string[])value;

				List<string> res = new List<string>(typedValue.Length);
				for (int i = 0; i < typedValue.Length; i++) res.Add(typedValue[i]);
				return res;
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(DateTime))
			{
				DateTime[] typedValue = (DateTime[])value;

				List<DateTime> res = new List<DateTime>(typedValue.Length);
				for (int i = 0; i < typedValue.Length; i++) res.Add(typedValue[i]);
				return res;
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(double))
			{
				double[] typedValue = (double[])value;

				if (target == typeof(double) || target.GenericTypeArguments[0] == typeof(double))
				{
					List<double> res = new List<double>(typedValue.Length);
					for (int i = 0; i < typedValue.Length; i++) res.Add(typedValue[i]);
					return res;
				}
				else if (target == typeof(Decimal))
				{
					List<Decimal> res = new List<Decimal>(typedValue.Length);
					for (int i = 0; i < typedValue.Length; i++) res.Add((Decimal)typedValue[i]);
					return res;
				}
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(bool))
			{
				bool[] typedValue = (bool[])value;

				List<bool> res = new List<bool>(typedValue.Length);
				for (int i = 0; i < typedValue.Length; i++) res.Add(typedValue[i]);
				return res;
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(Decimal))
			{
				Decimal[] typedValue = (Decimal[])value;
				if (target == typeof(double))
				{
					List<double> res = new List<double>(typedValue.Length);
					for (int i = 0; i < typedValue.Length; i++) res.Add((double)typedValue[i]);
					return res;
				}
				else if (target == typeof(Decimal) || target.GenericTypeArguments[0] == typeof(Decimal))
				{
					List<Decimal> res = new List<Decimal>(typedValue.Length);
					for (int i = 0; i < typedValue.Length; i++) res.Add(typedValue[i]);
					return res;
				}
			}
			else if (target.IsGenericType && actual.IsArray && !actual.GetElementType().IsPrimitive)
			{
				Type arrayType = actual.GetElementType();
				Type listType = typeof(List<>);
				object[] typedValue = (object[])value;
				Type genericType = listType.MakeGenericType(arrayType);
				IList res = (IList)Activator.CreateInstance(genericType);
				for (int i = 0; i < typedValue.Length; i++) res.Add(typedValue[i]);
				return res;
			}
			else
			{
				if (target.IsGenericType && actual.IsGenericType && actual != target)
				{
					if (target == typeof(List<double>) && actual == typeof(List<Decimal>))
					{
						List<double> res = new List<double>(((List<Decimal>)value).Count);
						foreach (var elementos in (List<Decimal>)value) res.Add((double)elementos);
						return res;
					}
					else if (target == typeof(List<Decimal>) && actual == typeof(List<double>))
					{
						List<Decimal> res = new List<Decimal>(((List<double>)value).Count);
						foreach (var elementos in (List<double>)value) res.Add((Decimal)elementos);
						return res;
					}
					else if (target.GetGenericArguments().Length == 1 && actual.GetGenericArguments().Length == 1 && value is IEnumerable)
					{
						Type targetElem = target.GetGenericArguments()[0];
						Type actualElem = actual.GetGenericArguments()[0];
						if (targetElem != actualElem && IsNumeric(targetElem) && IsNumeric(actualElem))
						{
							Type listType = typeof(List<>).MakeGenericType(targetElem);
							IList res = (IList)Activator.CreateInstance(listType);
							foreach (var elemento in (IEnumerable)value) res.Add(Convert.ChangeType(elemento, targetElem));
							return res;
						}
					}
				}
				else if (target.IsArray && actual.IsArray && actual != target)
				{
					if (target == typeof(double))
					{
						Decimal[] typedValue = (Decimal[])value;
						double[] res = new double[typedValue.Length];
						for (int i = 0; i < typedValue.Length; i++) res[i] = (double)typedValue[i];
						return res;
					}
					else if (target == typeof(Decimal))
					{
						double[] typedValue = (double[])value;
						Decimal[] res = new Decimal[typedValue.Length];
						for (int i = 0; i < typedValue.Length; i++) res[i] = (Decimal)typedValue[i];
						return res;
					}
				}
				result = value;
			}
			return result;
		}

		// True when the value is a collection whose element type is string (string[], List<string>,
		// IEnumerable<string>, ...) — the sources we coerce element-wise into a char collection. A
		// bare string is itself IEnumerable<char>, so it is explicitly excluded.
		private static bool IsStringCollection(Type actual)
		{
			if (actual == typeof(string)) return false;
			if (actual.IsArray) return actual.GetElementType() == typeof(string);
			if (actual.IsGenericType && actual.GetGenericArguments().Length == 1)
				return actual.GetGenericArguments()[0] == typeof(string) && typeof(IEnumerable).IsAssignableFrom(actual);
			return false;
		}

		// A single-character string -> its char. Any other length (0 or >1) is a clear
		// LanguageException rather than a silent truncation to the first character.
		internal static char StringToChar(string value)
		{
			if (value == null) throw new LanguageException("Cannot convert a null string to char.");
			if (value.Length != 1)
				throw new LanguageException($"Cannot convert the string \"{value}\" to a single character: expected exactly 1 character but got {value.Length}.");
			return value[0];
		}

		private static bool IsNumeric(Type t)
		{
			return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
				|| t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort)
				|| t == typeof(float) || t == typeof(double) || t == typeof(decimal);
		}

	}
}
