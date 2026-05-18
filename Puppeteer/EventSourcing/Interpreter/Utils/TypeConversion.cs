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

			object resultado = null;
			if (actual == typeof(int) && target == typeof(double))
			{
				resultado = (double)value;
			}
			else if (actual == typeof(int) && target == typeof(decimal))
			{
				resultado = Convert.ToDecimal((int)value);
			}
			else if (actual == typeof(double) && target == typeof(decimal))
			{
				resultado = Convert.ToDecimal((double)value);
			}
			else if (actual == typeof(decimal) && target == typeof(double))
			{
				resultado = System.Decimal.ToDouble((decimal)value);
			}
			else if (target.IsArray && actual == typeof(List<int>))
			{
				List<int> valorConTipo = value as List<int>;
				int[] res = new int[valorConTipo.Count];
				for (int i = 0; i < valorConTipo.Count; i++) res[i] = valorConTipo[i];
				return res;
			}
			else if (target.IsArray && actual == typeof(List<string>))
			{
				List<string> valorConTipo = value as List<string>;
				string[] res = new string[valorConTipo.Count];
				for (int i = 0; i < valorConTipo.Count; i++) res[i] = valorConTipo[i];
				return res;
			}
			else if (target.IsArray && actual == typeof(List<DateTime>))
			{
				List<DateTime> valorConTipo = value as List<DateTime>;
				DateTime[] res = new DateTime[valorConTipo.Count];
				for (int i = 0; i < valorConTipo.Count; i++) res[i] = valorConTipo[i];
				return res;
			}
			else if (target.IsArray && actual == typeof(List<double>))
			{
				List<double> valorConTipo = value as List<double>;

				if (target == typeof(double))
				{
					double[] res = new double[valorConTipo.Count];
					for (int i = 0; i < valorConTipo.Count; i++) res[i] = valorConTipo[i];
					return res;
				}
				else if (target == typeof(Decimal))
				{
					Decimal[] res = new Decimal[valorConTipo.Count];
					for (int i = 0; i < valorConTipo.Count; i++) res[i] = (Decimal)valorConTipo[i];
					return res;
				}
			}
			else if (target.IsArray && actual == typeof(List<bool>))
			{
				List<bool> valorConTipo = value as List<bool>;
				bool[] res = new bool[valorConTipo.Count];
				for (int i = 0; i < valorConTipo.Count; i++) res[i] = valorConTipo[i];
				return res;
			}
			else if (target.IsArray && actual.IsGenericType && actual.GetGenericTypeDefinition() == typeof(List<>) && actual.GetGenericArguments().Length == 1)
			{
				Type listType = actual.GetGenericArguments()[0];
				IList valorConTipo = (IList)value;
				System.Array res = Array.CreateInstance(listType, valorConTipo.Count);
				for (int i = 0; i < valorConTipo.Count; i++) res.SetValue(valorConTipo[i], i);
				return res;
			}
			else if (target.IsArray && actual == typeof(List<Decimal>))
			{
				List<Decimal> valorConTipo = value as List<Decimal>;

				if (target == typeof(double))
				{
					double[] res = new double[valorConTipo.Count];
					for (int i = 0; i < valorConTipo.Count; i++) res[i] = (double)valorConTipo[i];
					return res;
				}
				else if (target == typeof(Decimal))
				{
					Decimal[] res = new Decimal[valorConTipo.Count];
					for (int i = 0; i < valorConTipo.Count; i++) res[i] = valorConTipo[i];
					return res;
				}
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(int))
			{
				int[] valorConTipo = (int[])value;

				List<int> res = new List<int>(valorConTipo.Length);
				for (int i = 0; i < valorConTipo.Length; i++) res.Add(valorConTipo[i]);
				return res;

			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(string))
			{
				string[] valorConTipo = (string[])value;

				List<string> res = new List<string>(valorConTipo.Length);
				for (int i = 0; i < valorConTipo.Length; i++) res.Add(valorConTipo[i]);
				return res;
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(DateTime))
			{
				DateTime[] valorConTipo = (DateTime[])value;

				List<DateTime> res = new List<DateTime>(valorConTipo.Length);
				for (int i = 0; i < valorConTipo.Length; i++) res.Add(valorConTipo[i]);
				return res;
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(double))
			{
				double[] valorConTipo = (double[])value;

				if (target == typeof(double) || target.GenericTypeArguments[0] == typeof(double))
				{
					List<double> res = new List<double>(valorConTipo.Length);
					for (int i = 0; i < valorConTipo.Length; i++) res.Add(valorConTipo[i]);
					return res;
				}
				else if (target == typeof(Decimal))
				{
					List<Decimal> res = new List<Decimal>(valorConTipo.Length);
					for (int i = 0; i < valorConTipo.Length; i++) res.Add((Decimal)valorConTipo[i]);
					return res;
				}
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(bool))
			{
				bool[] valorConTipo = (bool[])value;

				List<bool> res = new List<bool>(valorConTipo.Length);
				for (int i = 0; i < valorConTipo.Length; i++) res.Add(valorConTipo[i]);
				return res;
			}
			else if (target.IsGenericType && actual.IsArray && actual.GetElementType() == typeof(Decimal))
			{
				Decimal[] valorConTipo = (Decimal[])value;
				if (target == typeof(double))
				{
					List<double> res = new List<double>(valorConTipo.Length);
					for (int i = 0; i < valorConTipo.Length; i++) res.Add((double)valorConTipo[i]);
					return res;
				}
				else if (target == typeof(Decimal) || target.GenericTypeArguments[0] == typeof(Decimal))
				{
					List<Decimal> res = new List<Decimal>(valorConTipo.Length);
					for (int i = 0; i < valorConTipo.Length; i++) res.Add(valorConTipo[i]);
					return res;
				}
			}
			else if (target.IsGenericType && actual.IsArray && !actual.GetElementType().IsPrimitive)
			{
				Type arrayType = actual.GetElementType();
				Type listType = typeof(List<>);
				object[] valorConTipo = (object[])value;
				Type genericType = listType.MakeGenericType(arrayType);
				IList res = (IList)Activator.CreateInstance(genericType);
				for (int i = 0; i < valorConTipo.Length; i++) res.Add(valorConTipo[i]);
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
				}
				else if (target.IsArray && actual.IsArray && actual != target)
				{
					if (target == typeof(double))
					{
						Decimal[] valorConTipo = (Decimal[])value;
						double[] res = new double[valorConTipo.Length];
						for (int i = 0; i < valorConTipo.Length; i++) res[i] = (double)valorConTipo[i];
						return res;
					}
					else if (target == typeof(Decimal))
					{
						double[] valorConTipo = (double[])value;
						Decimal[] res = new Decimal[valorConTipo.Length];
						for (int i = 0; i < valorConTipo.Length; i++) res[i] = (Decimal)valorConTipo[i];
						return res;
					}
				}
				resultado = value;
			}
			return resultado;
		}

	}
}
