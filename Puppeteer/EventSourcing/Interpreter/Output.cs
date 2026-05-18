using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Puppeteer.EventSourcing.Interpreter
{

	internal class Output
	{
		private readonly bool escribirSalida = true;
		private bool enUnFor = false;
		private bool necesitaComa = false;
		private StringBuilder output;
		private Stack<StringBuilder> outputLevels;
		private Stack<bool> nivelesEnUnFor;
		private Stack<bool> nivelesNecesitaComa;
		private List<Tuple<string, string>> ewis;

		private static readonly CultureInfo USculture = new CultureInfo("en-US");

		private Output(bool conSalida)
		{
			CultureInfo.DefaultThreadCurrentCulture = USculture;
			CultureInfo.DefaultThreadCurrentUICulture = USculture;

			this.escribirSalida = conSalida;

			if (escribirSalida)
			{
				output = new StringBuilder().Append('{');
				ewis = new List<Tuple<string, string>>();
			}
			enUnFor = false;
			necesitaComa = false;
		}

		// Per-thread pool. The previous shared ConcurrentStack<Output> design
		// looked thread-safe at the collection level (atomic TryPop/Push) but
		// leaked Output instances across threads — a parallel PerformQuery
		// sweep on one thread could corrupt an Output's StringBuilder via
		// concurrent Clear+Append on the same instance, and the corrupted
		// instance would survive in the static pool to poison later (even
		// sequential) tests. The two FlakyInCI tests (ActorV2 thread-safety
		// + Saga joint history) both reproduced the same bug from this root.
		// A ThreadLocal stack keeps the per-call allocation savings without
		// any cross-thread sharing of the rented instance.
		private class OutputPool
		{
			private readonly ThreadLocal<Stack<Output>> _objects = new ThreadLocal<Stack<Output>>(() => new Stack<Output>());
			private readonly bool _conSalida;
			private readonly int _maxPoolSize;

			internal OutputPool(bool conSalida, int maxPoolSize = ActorHandler.MAX_NORMAL_LOAD_POOL_SIZE)
			{
				if (maxPoolSize <= 0) throw new LanguageException($"{nameof(OutputPool)} maxPoolSize {maxPoolSize} must be greater than 0.");

				_conSalida = conSalida;
				_maxPoolSize = maxPoolSize;
			}

			internal Output Rent()
			{
				var stack = _objects.Value;
				if (stack.Count > 0) return stack.Pop();
				return new Output(conSalida: _conSalida);
			}

			internal void Return(Output item)
			{
				ArgumentNullException.ThrowIfNull(item);
				var stack = _objects.Value;
				if (stack.Count < _maxPoolSize)
				{
					item.Clear();
					stack.Push(item);
				}
			}
		}

		private static readonly OutputPool _conSalidaPool = new OutputPool(conSalida: true);
		private static readonly OutputPool _sinSalidaPool = new OutputPool(conSalida: false);

		internal static Output RentWithOutput()
		{
			var result = _conSalidaPool.Rent();
			return result;
		}

		internal static Output RentWithoutOutput()
		{
			var result = _sinSalidaPool.Rent();
			return result;
		}

		internal static void Return(Output rentedSalida)
		{
			if (rentedSalida.escribirSalida)
			{
				_conSalidaPool.Return(rentedSalida);
			}
			else
			{
				_sinSalidaPool.Return(rentedSalida);
			}
		}

		private void InitializeFreshState()
		{
			output.Clear();
			output.Append('{');
			enUnFor = false;
			necesitaComa = false;
		}

		internal void Clear()
		{
			output?.Clear();
			output?.Append('{');
			enUnFor = false;
			necesitaComa = false;
			outputLevels?.Clear();
			nivelesEnUnFor?.Clear();
			nivelesNecesitaComa?.Clear();
			ewis?.Clear();
		}

		internal void Finish()
		{
			if (escribirSalida)
			{
				AppendEWIs();
				output.Append('}');
			}
		}

		private void PushState()
		{
			if (outputLevels == null)
			{
				outputLevels = new Stack<StringBuilder>();
				nivelesEnUnFor = new Stack<bool>();
				nivelesNecesitaComa = new Stack<bool>();
			}
			var newSalida = new StringBuilder();
			newSalida.Append(output);

			outputLevels.Push(newSalida);
			nivelesEnUnFor.Push(enUnFor);
			nivelesNecesitaComa.Push(necesitaComa);
		}

		private void PopState()
		{
			output = outputLevels.Pop();
			enUnFor = nivelesEnUnFor.Pop();
			necesitaComa = nivelesNecesitaComa.Pop();
		}

		internal void OpenFor()
		{
			if (escribirSalida)
			{
				PushState();
				InitializeFreshState();
			}
		}

		internal void CloseFor(string alias)
		{
			if (escribirSalida)
			{
				if (!Vacio())
				{
					StringBuilder salidaAnterior = output;
					PopState();
					if (escribirSalida) WritePair(alias, salidaAnterior);
				}
				else
				{
					PopState();
				}
			}
		}

		internal void BeginForMoveNext()
		{
			enUnFor = true;
			if (escribirSalida)
			{
				if (!Vacio())
				{
					output.Append(',').Append('{');
				}
				necesitaComa = false;
			}
		}

		internal void EndForMoveNext()
		{
			if (escribirSalida)
			{
				if (!Vacio())
				{
					bool elCuerpoDelFORNoHizoSalidaEnEstaIteracion = output[output.Length - 2] == ',' && output[output.Length - 1] == '{';
					if (elCuerpoDelFORNoHizoSalidaEnEstaIteracion)
					{
						output.Remove(output.Length - 2, 2);
					}
					else
					{
						output.Append('}');
					}
				}
			}
			enUnFor = false;
		}

		internal bool IsWriting
		{
			get
			{
				return escribirSalida;
			}
		}

		internal StringBuilder Salidas
		{
			get
			{
				return output;
			}
		}

		internal bool Vacio()
		{
			bool result = output.Length == 1;
			return result;
		}

		internal void Append(ReadOnlySpan<char> alias, bool value)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append(value ? "true" : "false");
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, string value)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				if (value == null)
				{
					output.Append("null");
				}
				else
				{
					output.Append('"');
					EscapeString(value == null ? "null" : value);
					output.Append('"');
				}
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, int value)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append(value);
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, double value)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');

				var valorStr = value.ToString("0.######################");
				output.Append(valorStr);
				if (valorStr.IndexOf('.') == -1) output.Append(".0");
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, long value)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append(value);
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, DateTime value)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append('"');
				if (value.Hour == 00 && value.Minute == 00 && value.Second == 00)
					output.Append(value.ToString("MM/dd/yyyy"));
				else
					output.Append(value.ToString("MM/dd/yyyy HH:mm:ss"));

				output.Append('"');
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, decimal value)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				var valorStr = value.ToString("0.######################");// value.ToString();
				output.Append(valorStr);
				if (valorStr.IndexOf('.') == -1) output.Append(".0");
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, object values)
		{
			if (!escribirSalida) return;

			if (values == null)
			{
				this.Append(alias, (string)values);
				return;
			}

			var type = values.GetType();
			if (type == typeof(int))
			{
				this.Append(alias, (int)values);
			}
			else if (type == typeof(string))
			{
				this.Append(alias, (string)values);
			}
			else if (type == typeof(double))
			{
				this.Append(alias, (double)values);
			}
			else if (type == typeof(decimal))
			{
				this.Append(alias, (decimal)values);
			}
			else if (type == typeof(DateTime))
			{
				this.Append(alias, (DateTime)values);
			}
			else if (type == typeof(bool))
			{
				this.Append(alias, (bool)values);
			}
			else if (type.IsEnum)
			{
				this.Append(alias, values.ToString());
			}
			else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
			{
				Type elementType = type.GetGenericArguments()[0];
				if (elementType == typeof(int))
				{
					this.AppendPrivate(alias, (IEnumerable<int>)values);
				}
				else if (elementType == typeof(string))
				{
					this.AppendPrivate(alias, (IEnumerable<string>)values);
				}
				else if (elementType == typeof(double))
				{
					this.AppendPrivate(alias, (IEnumerable<double>)values);
				}
				else if (elementType == typeof(DateTime))
				{
					this.AppendPrivate(alias, (IEnumerable<DateTime>)values);
				}
				else if (elementType == typeof(bool))
				{
					this.AppendPrivate(alias, (IEnumerable<bool>)values);
				}
				else
				{
					this.AppendPrivate(alias, (IEnumerable<object>)values);
				}
			}
			else if (type.IsArray)
			{
				Type elementType = type.GetElementType();
				if (elementType == typeof(int))
				{
					this.AppendPrivate(alias, (IEnumerable<int>)values);
				}
				else if (elementType == typeof(string))
				{
					this.AppendPrivate(alias, (IEnumerable<string>)values);
				}
				else if (elementType == typeof(double))
				{
					this.AppendPrivate(alias, (IEnumerable<double>)values);
				}
				else if (elementType == typeof(DateTime))
				{
					this.AppendPrivate(alias, (IEnumerable<DateTime>)values);
				}
				else if (elementType == typeof(bool))
				{
					this.AppendPrivate(alias, (IEnumerable<bool>)values);
				}
				else
				{
					this.AppendPrivate(alias, (IEnumerable<object>)values);
				}
			}
			else
			{
				if (escribirSalida)
				{
					MethodInfo posiblePrint = values.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault(x => x.Name.ToLower() == "print" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(StringBuilder));
					if (posiblePrint != null)
					{
						StringBuilder outputTemp = new StringBuilder();
						posiblePrint.Invoke(values, new object[] { outputTemp });
						WritePairExp(alias, outputTemp.ToString());
					}
					else
					{
						WritePairExp(alias, values);
					}
				}
			}
		}

		internal bool HasEWIS()
		{
			return this.escribirSalida && ewis.Count > 0;
		}

		internal void AddEWI(string type, string value)
		{
			if (escribirSalida) ewis.Add(new Tuple<string, string>(type, value));
		}

		internal void AppendEWI(string alias, string value)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(",");
				}
				output.Append('{');
				output.Append('"');
				WriteAlias(alias);
				output.Append('"');
				output.Append(':');
				output.Append('"');
				EscapeString(value);
				output.Append('"');
			}
			necesitaComa = true;
		}
		internal void Append(ReadOnlySpan<char> alias, object[] values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<object> values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<object> values) => this.AppendPrivate(alias, values);

		internal void Append(string alias, object[] values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, List<object> values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, IEnumerable<object> values) => this.AppendPrivate(alias, values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<object> values)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append('[');
				bool necesitaComaArr = false;
				foreach (var value in values)
				{
					if (necesitaComaArr) output.Append(',');
					if (value is int intValue)
					{
						output.Append(value);
					}
					else if (value is double doubleValue)
					{
						output.Append(doubleValue);
					}
					else if (value is decimal decimaValue)
					{
						output.Append(decimaValue);
					}
					else if (value is string stringValue)
					{
						output.Append('"');
						EscapeString(stringValue);
						output.Append('"');
					}
					else if (value is DateTime dateTimeValue)
					{
						if (dateTimeValue.Hour == 0 && dateTimeValue.Minute == 0 && dateTimeValue.Second == 0)
						{
							output.Append(dateTimeValue.ToString("MM/dd/yyyy"));
						}
						else
						{
							output.Append(dateTimeValue.ToString("MM/dd/yyyy HH:mm:ss"));
						}
					}
					else if (value is bool boolValue)
					{
						output.Append(boolValue ? "true" : "false");
					}
					else
					{
						MethodInfo posiblePrint = value.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault(x => x.Name.ToLower() == "print" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(StringBuilder));
						if (posiblePrint != null)
						{
							StringBuilder outputTemp = new StringBuilder();
							posiblePrint.Invoke(value, new object[] { outputTemp });
							output.Append(outputTemp.ToString());
						}
						else
						{
							output.Append(value.ToString());
						}
					}
					necesitaComaArr = true;
				}
				output.Append(']');
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, int[] values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<int> values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<int> values) => this.AppendPrivate(alias, values);

		internal void Append(string alias, int[] values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, List<int> values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, IEnumerable<int> values) => this.AppendPrivate(alias, values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<int> values)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append('[');
				bool necesitaComaArr = false;
				foreach (var value in values)
				{
					if (necesitaComaArr) output.Append(',');
					output.Append(value);
					necesitaComaArr = true;
				}
				output.Append(']');
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, double[] values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<double> values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<double> values) => this.AppendPrivate(alias, values);

		internal void Append(string alias, double[] values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, List<double> values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, IEnumerable<double> values) => this.AppendPrivate(alias, values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<double> values)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append('[');
				bool necesitaComaArr = false;
				foreach (var value in values)
				{
					if (necesitaComaArr) output.Append(',');
					output.Append(value);
					necesitaComaArr = true;
				}
				output.Append(']');
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, decimal[] values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<decimal> values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<decimal> values) => this.AppendPrivate(alias, values);

		internal void Append(string alias, decimal[] values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, List<decimal> values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, IEnumerable<decimal> values) => this.AppendPrivate(alias, values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<decimal> values)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append('[');
				bool necesitaComaArr = false;
				foreach (var value in values)
				{
					if (necesitaComaArr) output.Append(',');
					output.Append(value);
					necesitaComaArr = true;
				}
				output.Append(']');
			}
			necesitaComa = true;
		}


		internal void Append(ReadOnlySpan<char> alias, bool[] values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<bool> values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<bool> values) => this.AppendPrivate(alias, values);

		internal void Append(string alias, bool[] values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, List<bool> values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, IEnumerable<bool> values) => this.AppendPrivate(alias, values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<bool> values)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append('[');
				bool necesitaComaArr = false;
				foreach (var value in values)
				{
					if (necesitaComaArr) output.Append(',');
					output.Append(value ? "true" : "false");
					necesitaComaArr = true;
				}
				output.Append(']');
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, string[] values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<string> values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<string> values) => this.AppendPrivate(alias, values);

		internal void Append(string alias, string[] values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, List<string> values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, IEnumerable<string> values) => this.AppendPrivate(alias, values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<string> values)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append('[');
				bool necesitaComaArr = false;
				foreach (var value in values)
				{
					if (necesitaComaArr) output.Append(',');
					output.Append('"');
					EscapeString(value);
					output.Append('"');
					necesitaComaArr = true;
				}
				output.Append(']');
			}
			necesitaComa = true;
		}

		internal void Append(ReadOnlySpan<char> alias, DateTime[] values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<DateTime> values) => this.AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<DateTime> values) => this.AppendPrivate(alias, values);


		internal void Append(string alias, DateTime[] values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, List<DateTime> values) => this.AppendPrivate(alias, values);
		internal void Append(string alias, IEnumerable<DateTime> values) => this.AppendPrivate(alias, values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<DateTime> values)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				EscapeString(alias);
				output.Append('"');
				output.Append(':');
				output.Append('[');
				bool necesitaComaArr = false;
				foreach (var value in values)
				{
					if (necesitaComaArr) output.Append(',');
					output.Append('"');
					if (value.Hour == 0 && value.Minute == 0 && value.Second == 0)
					{
						output.Append(value.ToString("MM/dd/yyyy"));
					}
					else
					{
						output.Append(value.ToString("MM/dd/yyyy HH:mm:ss"));
					}
					output.Append(value);
					output.Append('"');
					necesitaComaArr = true;
				}
				output.Append(']');
			}
			necesitaComa = true;
		}

		internal void Append(string stream, int startIndex, int count)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append(stream, startIndex, count);
				necesitaComa = true;
			}
		}

		private void WritePair(ReadOnlySpan<char> alias, StringBuilder text)
		{
			if (necesitaComa)
			{
				output.Append(',');
			}
			output.Append('"');
			WriteAlias(alias);
			output.Append('"');
			output.Append(':');
			output.Append('[');
			output.Append(text);
			output.Append(']');
			necesitaComa = true;
		}

		private void EscapeString(ReadOnlySpan<char> value)
		{
			foreach (char c in value)
			{
				switch (c)
				{
					case '\\':
						output.Append('\\');
						output.Append('\\');
						break;
					case '"':
						output.Append('\\');
						output.Append('"');
						break;
					case '\b':
						output.Append("\\b");
						break;
					case '\f':
						output.Append("\\f");
						break;
					case '\n':
						output.Append("\\n");
						break;
					case '\r':
						output.Append("\\r");
						break;
					case '\t':
						output.Append("\\t");
						break;
					default:
						if (char.IsControl(c))
						{
							output.Append("\\u");
							output.Append(((int)c).ToString("x4"));
						}
						else
						{
							output.Append(c);
						}
						break;
				}
			}
		}

		private void WritePairExp(ReadOnlySpan<char> alias, object value)
		{
			if (necesitaComa)
			{
				output.Append(',');
			}
			output.Append('"');
			EscapeString(alias);
			output.Append('"');
			output.Append(':');
			output.Append(value.ToString());
			necesitaComa = true;
		}

		private void WriteAlias(ReadOnlySpan<char> alias)
		{
			foreach (char c in alias)
			{
				switch (c)
				{
					case '\\':
						output.Append('\\');
						output.Append('\\');
						break;
					case '"':
						output.Append('\\');
						output.Append('"');
						break;
					default:
						output.Append(c);
						break;
				}
			}
		}

		private void WritePair(ReadOnlySpan<char> alias, object value)
		{
			if (necesitaComa)
			{
				output.Append(',');
			}
			output.Append('"');
			EscapeString(alias);
			output.Append('"');
			output.Append(':');

			if (value is int intValue)
			{
				output.Append(value);
			}
			else if (value is double doubleValue)
			{
				output.Append(doubleValue);
			}
			else if (value is string stringValue)
			{
				EscapeString(stringValue);
			}
			else if (value is DateTime dateTimeValue)
			{
				if (dateTimeValue.Hour == 0 && dateTimeValue.Minute == 0 && dateTimeValue.Second == 0)
				{
					output.Append(dateTimeValue.ToString("MM/dd/yyyy"));
				}
				else
				{
					output.Append(dateTimeValue.ToString("MM/dd/yyyy HH:mm:ss"));
				}
			}
			else if (value is bool boolValue)
			{
				output.Append(boolValue);
			}
			/*else if (AstExpression.TypeOfCollection(value.GetType()) !=  null)
			{
				var listaValue = value;
				output.Append(boolValue);
			}*/
			else
			{
				output.Append(value.ToString());
			}
			necesitaComa = true;
		}

		internal void Append(string alias, char text)
		{
			if (escribirSalida)
			{
				if (necesitaComa)
				{
					output.Append(',');
				}
				output.Append('"');
				WriteAlias(alias);
				output.Append('"');
				output.Append(':');
				output.Append('"');
				output.Append(text);
				output.Append('"');
				necesitaComa = true;
			}
		}

		private void AppendEWIs()
		{
			if (escribirSalida && ewis.Count > 0)
			{
				if (necesitaComa)
				{
					output.Append(",");
				}
				output.Append('"');
				WriteAlias("EWI");
				output.Append('"');
				output.Append(':');
				output.Append('[');

				necesitaComa = false;
				foreach (Tuple<string, string> ewi in ewis)
				{
					AppendEWI(ewi.Item1, ewi.Item2);
					output.Append('}');
				}

				output.Append(']');
			}
			necesitaComa = true;
		}

		public override string ToString()
		{
			string resultado = "";
			if (escribirSalida)
			{
				resultado = output.ToString();
				if (resultado == "{}") resultado = "";
			}
			return resultado;
		}
	}
}
