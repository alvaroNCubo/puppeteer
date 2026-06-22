using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Formatters
{
	/// <summary>
	/// JSON byte-emission strategy. Preserves byte-for-byte the legacy
	/// hardcoded output of Output.cs (including the empty-document collapse
	/// "{}" → "", the EWI sidecar shape "EWI":[{...}], and the for-block
	/// rollback of empty iterations).
	///
	/// <para>
	/// State lives in instance fields. The framework pools JsonFormatter
	/// instances 1:1 with Output instances; <see cref="Reset"/> nulls all
	/// mutable state before the next Rent.
	/// </para>
	/// </summary>
	public sealed class JsonFormatter : IOutputFormatter
	{
		private StringBuilder sink;
		private bool needsComma;
		private bool inForIteration;
		private Stack<StringBuilder> levels;
		private Stack<bool> nestedInForIteration;
		private Stack<bool> nestedNeedsComma;

		private static readonly CultureInfo USculture = new CultureInfo("en-US");

		static JsonFormatter()
		{
			CultureInfo.DefaultThreadCurrentCulture = USculture;
			CultureInfo.DefaultThreadCurrentUICulture = USculture;
		}

		// ── Pool lifecycle ─────────────────────────────────────────────────

		public IOutputFormatter CreateNew() => new JsonFormatter();

		public void Reset()
		{
			sink = null;
			needsComma = false;
			inForIteration = false;
			levels?.Clear();
			nestedInForIteration?.Clear();
			nestedNeedsComma?.Clear();
		}

		public bool CollapseEmptyToString => true;

		public bool IsDocumentEmpty => sink != null && sink.Length == 1;

		// ── Document lifecycle ─────────────────────────────────────────────

		public void BeginDocument(StringBuilder sink)
		{
			this.sink = sink;
			sink.Append('{');
			needsComma = false;
			inForIteration = false;
		}

		public void EndDocument()
		{
			sink.Append('}');
		}

		// ── Collection (for-block) ─────────────────────────────────────────

		public void BeginCollection()
		{
			if (levels == null)
			{
				levels = new Stack<StringBuilder>();
				nestedInForIteration = new Stack<bool>();
				nestedNeedsComma = new Stack<bool>();
			}
			// Preserve legacy mechanic from Output.PushState +
			// InitializeFreshState: snapshot a COPY of current sink onto
			// the stack, then reset the existing sink to a fresh "{".
			// The same StringBuilder reference stays in `sink`; only its
			// content is shuffled across the stack.
			var snapshot = new StringBuilder();
			snapshot.Append(sink);
			levels.Push(snapshot);
			nestedInForIteration.Push(inForIteration);
			nestedNeedsComma.Push(needsComma);

			sink.Clear();
			sink.Append('{');
			inForIteration = false;
			needsComma = false;
		}

		public void EndCollection(ReadOnlySpan<char> collectionName)
		{
			if (sink.Length > 1)
			{
				// Capture inner content, pop snapshot, merge as
				// "collectionName":[inner] into the restored sink.
				var innerContent = new StringBuilder();
				innerContent.Append(sink);
				sink.Clear();
				sink.Append(levels.Pop());
				inForIteration = nestedInForIteration.Pop();
				needsComma = nestedNeedsComma.Pop();
				WritePair(collectionName, innerContent);
			}
			else
			{
				// Empty for-block — just pop and discard.
				sink.Clear();
				sink.Append(levels.Pop());
				inForIteration = nestedInForIteration.Pop();
				needsComma = nestedNeedsComma.Pop();
			}
		}

		public void BeginCollectionItem()
		{
			inForIteration = true;
			if (sink.Length > 1)
			{
				sink.Append(',').Append('{');
			}
			needsComma = false;
		}

		public void EndCollectionItem()
		{
			if (sink.Length > 1)
			{
				bool emptyIteration =
					sink[sink.Length - 2] == ',' &&
					sink[sink.Length - 1] == '{';
				if (emptyIteration)
				{
					sink.Remove(sink.Length - 2, 2);
				}
				else
				{
					sink.Append('}');
				}
			}
			inForIteration = false;
		}

		// ── Field (scalar) ─────────────────────────────────────────────────

		public void Field(ReadOnlySpan<char> name, bool value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append(value ? "true" : "false");
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, int value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append(value);
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, long value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append(value);
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, double value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':');
			var valorStr = value.ToString("0.######################");
			sink.Append(valorStr);
			if (valorStr.IndexOf('.') == -1) sink.Append(".0");
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, decimal value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':');
			var valorStr = value.ToString("0.######################");
			sink.Append(valorStr);
			if (valorStr.IndexOf('.') == -1) sink.Append(".0");
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, string value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':');
			if (value == null)
			{
				sink.Append("null");
			}
			else
			{
				sink.Append('"');
				EscapeString(value);
				sink.Append('"');
			}
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, DateTime value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append('"');
			if (value.Hour == 0 && value.Minute == 0 && value.Second == 0)
				sink.Append(value.ToString("MM/dd/yyyy"));
			else
				sink.Append(value.ToString("MM/dd/yyyy HH:mm:ss"));
			sink.Append('"');
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, char value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			WriteAlias(name);
			sink.Append('"').Append(':').Append('"').Append(value).Append('"');
			needsComma = true;
		}

		// ── Field (collection of primitives) ───────────────────────────────

		public void Field(ReadOnlySpan<char> name, IEnumerable<int> values)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append('[');
			bool needsCommaArr = false;
			foreach (var value in values)
			{
				if (needsCommaArr) sink.Append(',');
				sink.Append(value);
				needsCommaArr = true;
			}
			sink.Append(']');
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<long> values)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append('[');
			bool needsCommaArr = false;
			foreach (var value in values)
			{
				if (needsCommaArr) sink.Append(',');
				sink.Append(value);
				needsCommaArr = true;
			}
			sink.Append(']');
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<double> values)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append('[');
			bool needsCommaArr = false;
			foreach (var value in values)
			{
				if (needsCommaArr) sink.Append(',');
				sink.Append(value);
				needsCommaArr = true;
			}
			sink.Append(']');
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<decimal> values)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append('[');
			bool needsCommaArr = false;
			foreach (var value in values)
			{
				if (needsCommaArr) sink.Append(',');
				sink.Append(value);
				needsCommaArr = true;
			}
			sink.Append(']');
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<string> values)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append('[');
			bool needsCommaArr = false;
			foreach (var value in values)
			{
				if (needsCommaArr) sink.Append(',');
				sink.Append('"');
				EscapeString(value);
				sink.Append('"');
				needsCommaArr = true;
			}
			sink.Append(']');
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<bool> values)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append('[');
			bool needsCommaArr = false;
			foreach (var value in values)
			{
				if (needsCommaArr) sink.Append(',');
				sink.Append(value ? "true" : "false");
				needsCommaArr = true;
			}
			sink.Append(']');
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<DateTime> values)
		{
			// Legacy behavior preserved byte-for-byte: in Output.cs:783-816
			// the per-element body appends BOTH a formatted-string AND the
			// raw DateTime value (via the implicit StringBuilder.Append(object)
			// overload that calls value.ToString()). That double-emit is a
			// long-standing quirk that some tests may depend on; do not
			// "fix" without first verifying every snapshot test.
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append('[');
			bool needsCommaArr = false;
			foreach (var value in values)
			{
				if (needsCommaArr) sink.Append(',');
				sink.Append('"');
				if (value.Hour == 0 && value.Minute == 0 && value.Second == 0)
				{
					sink.Append(value.ToString("MM/dd/yyyy"));
				}
				else
				{
					sink.Append(value.ToString("MM/dd/yyyy HH:mm:ss"));
				}
				sink.Append(value);   // preserve legacy double-emit (see comment above)
				sink.Append('"');
				needsCommaArr = true;
			}
			sink.Append(']');
			needsComma = true;
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<object> values)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(name);
			sink.Append('"').Append(':').Append('[');
			bool needsCommaArr = false;
			foreach (var value in values)
			{
				if (needsCommaArr) sink.Append(',');
				if (value is int intValue)
				{
					sink.Append(value);
				}
				else if (value is double doubleValue)
				{
					sink.Append(doubleValue);
				}
				else if (value is decimal decimaValue)
				{
					sink.Append(decimaValue);
				}
				else if (value is string stringValue)
				{
					sink.Append('"');
					EscapeString(stringValue);
					sink.Append('"');
				}
				else if (value is DateTime dateTimeValue)
				{
					if (dateTimeValue.Hour == 0 && dateTimeValue.Minute == 0 && dateTimeValue.Second == 0)
					{
						sink.Append(dateTimeValue.ToString("MM/dd/yyyy"));
					}
					else
					{
						sink.Append(dateTimeValue.ToString("MM/dd/yyyy HH:mm:ss"));
					}
				}
				else if (value is bool boolValue)
				{
					sink.Append(boolValue ? "true" : "false");
				}
				else
				{
					MethodInfo posiblePrint = value.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault(x => x.Name.ToLower() == "print" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(StringBuilder));
					if (posiblePrint != null)
					{
						StringBuilder outputTemp = new StringBuilder();
						posiblePrint.Invoke(value, new object[] { outputTemp });
						sink.Append(outputTemp.ToString());
					}
					else
					{
						sink.Append(value.ToString());
					}
				}
				needsCommaArr = true;
			}
			sink.Append(']');
			needsComma = true;
		}

		// ── Field (fallback runtime-dispatch) ──────────────────────────────

		public void Field(ReadOnlySpan<char> name, object values)
		{
			if (values == null)
			{
				Field(name, (string)null);
				return;
			}

			var type = values.GetType();
			if (type == typeof(int))
			{
				Field(name, (int)values);
			}
			else if (type == typeof(string))
			{
				Field(name, (string)values);
			}
			else if (type == typeof(double))
			{
				Field(name, (double)values);
			}
			else if (type == typeof(decimal))
			{
				Field(name, (decimal)values);
			}
			else if (type == typeof(DateTime))
			{
				Field(name, (DateTime)values);
			}
			else if (type == typeof(bool))
			{
				Field(name, (bool)values);
			}
			else if (type.IsEnum)
			{
				Field(name, values.ToString());
			}
			else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
			{
				Type elementType = type.GetGenericArguments()[0];
				if (elementType == typeof(int))
				{
					Field(name, (IEnumerable<int>)values);
				}
				else if (elementType == typeof(string))
				{
					Field(name, (IEnumerable<string>)values);
				}
				else if (elementType == typeof(double))
				{
					Field(name, (IEnumerable<double>)values);
				}
				else if (elementType == typeof(DateTime))
				{
					Field(name, (IEnumerable<DateTime>)values);
				}
				else if (elementType == typeof(bool))
				{
					Field(name, (IEnumerable<bool>)values);
				}
				else
				{
					Field(name, (IEnumerable<object>)values);
				}
			}
			else if (type.IsArray)
			{
				Type elementType = type.GetElementType();
				if (elementType == typeof(int))
				{
					Field(name, (IEnumerable<int>)values);
				}
				else if (elementType == typeof(string))
				{
					Field(name, (IEnumerable<string>)values);
				}
				else if (elementType == typeof(double))
				{
					Field(name, (IEnumerable<double>)values);
				}
				else if (elementType == typeof(DateTime))
				{
					Field(name, (IEnumerable<DateTime>)values);
				}
				else if (elementType == typeof(bool))
				{
					Field(name, (IEnumerable<bool>)values);
				}
				else
				{
					Field(name, (IEnumerable<object>)values);
				}
			}
			else
			{
				MethodInfo posiblePrint = values.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault(x => x.Name.ToLower() == "print" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(StringBuilder));
				if (posiblePrint != null)
				{
					StringBuilder outputTemp = new StringBuilder();
					posiblePrint.Invoke(values, new object[] { outputTemp });
					WritePairExp(name, outputTemp.ToString());
				}
				else
				{
					WritePairExp(name, values);
				}
			}
		}

		// ── EWIs ───────────────────────────────────────────────────────────

		public void BeginEwis()
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			WriteAlias("EWI");
			sink.Append('"').Append(':').Append('[');
			needsComma = false;
		}

		public void Ewi(string type, string value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('{').Append('"');
			WriteAlias(type);
			sink.Append('"').Append(':').Append('"');
			EscapeString(value);
			sink.Append('"').Append('}');
			needsComma = true;
		}

		public void EndEwis()
		{
			sink.Append(']');
			needsComma = true;
		}

		// ── Raw splice (JSON-only escape hatch for V1 EvalStatement) ───────

		public void RawSplice(string stream, int startIndex, int count)
		{
			if (needsComma) sink.Append(',');
			sink.Append(stream, startIndex, count);
			needsComma = true;
		}

		// ── Private helpers ────────────────────────────────────────────────

		private void EscapeString(ReadOnlySpan<char> value)
		{
			foreach (char c in value)
			{
				switch (c)
				{
					case '\\':
						sink.Append('\\');
						sink.Append('\\');
						break;
					case '"':
						sink.Append('\\');
						sink.Append('"');
						break;
					case '\b':
						sink.Append("\\b");
						break;
					case '\f':
						sink.Append("\\f");
						break;
					case '\n':
						sink.Append("\\n");
						break;
					case '\r':
						sink.Append("\\r");
						break;
					case '\t':
						sink.Append("\\t");
						break;
					default:
						if (char.IsControl(c))
						{
							sink.Append("\\u");
							sink.Append(((int)c).ToString("x4"));
						}
						else
						{
							sink.Append(c);
						}
						break;
				}
			}
		}

		private void WriteAlias(ReadOnlySpan<char> alias)
		{
			foreach (char c in alias)
			{
				switch (c)
				{
					case '\\':
						sink.Append('\\');
						sink.Append('\\');
						break;
					case '"':
						sink.Append('\\');
						sink.Append('"');
						break;
					default:
						sink.Append(c);
						break;
				}
			}
		}

		private void WritePair(ReadOnlySpan<char> alias, StringBuilder text)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			WriteAlias(alias);
			sink.Append('"').Append(':').Append('[');
			sink.Append(text);
			sink.Append(']');
			needsComma = true;
		}

		private void WritePairExp(ReadOnlySpan<char> alias, object value)
		{
			if (needsComma) sink.Append(',');
			sink.Append('"');
			EscapeString(alias);
			sink.Append('"').Append(':');
			sink.Append(value.ToString());
			needsComma = true;
		}
	}
}
