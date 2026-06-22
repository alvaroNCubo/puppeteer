using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Formatters
{
	/// <summary>
	/// TOON (Token-Oriented Object Notation) byte-emission strategy.
	/// Indentation-based, no closing brackets, no inter-field commas.
	/// Designed to overlap human-TTY readability with LLM-context efficiency.
	///
	/// <para>
	/// Dialect chosen for Phase 2 (subject to refinement after review
	/// of actual output of canonical examples):
	/// <list type="bullet">
	/// <item>2-space indent per nesting level.</item>
	/// <item>Object: <c>key: value</c> per line.</item>
	/// <item>String values: ALWAYS quoted (to disambiguate from numerics
	/// and to preserve special chars). <c>field: "hello world"</c>.</item>
	/// <item>Primitive values (int/long/double/decimal/bool/DateTime/char):
	/// bare. <c>field: 42</c>, <c>field: true</c>.</item>
	/// <item>Null: <c>field: null</c>.</item>
	/// <item>Arrays of primitives (Field with IEnumerable): inline
	/// <c>field: [v1, v2, v3]</c>.</item>
	/// <item>Collection (for-block): bulleted list of items, each item
	/// introduced by <c>- </c> on its first field. Subsequent fields
	/// indented +2 spaces inside the item.</item>
	/// <item>Empty collection: <c>name: []</c>.</item>
	/// <item>EWIs: section <c>EWI:</c> at the very end of the document
	/// with bulleted entries.</item>
	/// </list>
	/// </para>
	///
	/// <para>
	/// ANSI colors (opt-in via <c>useAnsi</c> ctor flag):
	/// keys = cyan, strings = green, numbers = yellow, booleans = red,
	/// null = gray. Structure (<c>-</c>, <c>:</c>, brackets) stays default.
	/// </para>
	/// </summary>
	public sealed class ToonFormatter : IOutputFormatter
	{
		// ── ANSI escape sequences ─────────────────────────────────────────
		private const string ANSI_RESET = "[0m";
		private const string ANSI_KEY = "[36m";      // cyan
		private const string ANSI_STRING = "[32m";   // green
		private const string ANSI_NUMBER = "[33m";   // yellow
		private const string ANSI_BOOL = "[31m";     // red
		private const string ANSI_NULL = "[90m";     // bright black (gray)

		private readonly bool useAnsi;

		// ── State (per-document, reset at Reset()) ─────────────────────────
		private StringBuilder sink;
		private bool atLineStart;
		private bool inItem;
		private bool firstFieldInItem;
		private Stack<Frame> levels;

		private readonly struct Frame
		{
			public readonly StringBuilder Sink;
			public readonly bool AtLineStart;
			public readonly bool InItem;
			public readonly bool FirstFieldInItem;

			public Frame(StringBuilder sink, bool atLineStart, bool inItem, bool firstFieldInItem)
			{
				Sink = sink;
				AtLineStart = atLineStart;
				InItem = inItem;
				FirstFieldInItem = firstFieldInItem;
			}
		}

		private static readonly CultureInfo USculture = new CultureInfo("en-US");

		static ToonFormatter()
		{
			CultureInfo.DefaultThreadCurrentCulture = USculture;
			CultureInfo.DefaultThreadCurrentUICulture = USculture;
		}

		public ToonFormatter() : this(useAnsi: false) { }

		public ToonFormatter(bool useAnsi)
		{
			this.useAnsi = useAnsi;
		}

		// ── Pool lifecycle ─────────────────────────────────────────────────

		public IOutputFormatter CreateNew() => new ToonFormatter(useAnsi);

		public void Reset()
		{
			sink = null;
			atLineStart = true;
			inItem = false;
			firstFieldInItem = false;
			levels?.Clear();
		}

		// TOON has no document opener, so the sink stays empty until first
		// field. CollapseEmptyToString = true means an empty document
		// returns "" (matches the legacy "{}" → "" behavior).
		public bool CollapseEmptyToString => true;

		public bool IsDocumentEmpty => sink != null && sink.Length == 0;

		// ── Document lifecycle ─────────────────────────────────────────────

		public void BeginDocument(StringBuilder sink)
		{
			this.sink = sink;
			atLineStart = true;
			inItem = false;
			firstFieldInItem = false;
		}

		public void EndDocument()
		{
			// TOON has no document closer.
		}

		// ── Collection (for-block) ─────────────────────────────────────────

		public void BeginCollection()
		{
			if (levels == null)
			{
				levels = new Stack<Frame>();
			}
			levels.Push(new Frame(sink, atLineStart, inItem, firstFieldInItem));

			// Inner buffer at "depth 0" relative; merge at EndCollection
			// re-indents by the appropriate amount.
			sink = new StringBuilder();
			atLineStart = true;
			inItem = false;
			firstFieldInItem = false;
		}

		public void EndCollection(ReadOnlySpan<char> collectionName)
		{
			string inner = sink.ToString();
			var prev = levels.Pop();
			sink = prev.Sink;
			atLineStart = prev.AtLineStart;
			inItem = prev.InItem;
			firstFieldInItem = prev.FirstFieldInItem;

			// Emit "name:" line at current position (with item-prefix rules).
			WriteFieldPrefix();
			WriteKey(collectionName);
			if (inner.Length == 0)
			{
				// Empty collection: "name: []"
				sink.Append(": ");
				WriteStructure('[');
				WriteStructure(']');
				sink.Append('\n');
				atLineStart = true;
				return;
			}
			WriteStructure(':');
			sink.Append('\n');
			atLineStart = true;

			// Inner content is indented: the bullet column gets +2 spaces
			// per nesting level. If we are inside an item already (inItem),
			// add another +2 (the item-internal column).
			string indent = inItem ? "    " : "  ";
			AppendIndentedInner(inner, indent);
		}

		private void AppendIndentedInner(string inner, string indent)
		{
			int start = 0;
			while (start < inner.Length)
			{
				int nl = inner.IndexOf('\n', start);
				int lineEnd = nl < 0 ? inner.Length : nl;
				if (lineEnd > start)
				{
					sink.Append(indent);
					sink.Append(inner, start, lineEnd - start);
					sink.Append('\n');
				}
				if (nl < 0) break;
				start = nl + 1;
			}
		}

		public void BeginCollectionItem()
		{
			inItem = true;
			firstFieldInItem = true;
		}

		public void EndCollectionItem()
		{
			// If the item produced nothing, emit a "- " marker anyway so
			// the iteration is visible? Or silently drop? JsonFormatter
			// rolls back the empty iteration; TOON does the same: if
			// firstFieldInItem is still true, we never emitted anything
			// for this item, so leave the inner sink untouched.
			inItem = false;
			firstFieldInItem = false;
		}

		// ── Field prefix logic (item-aware indentation) ────────────────────

		private void WriteFieldPrefix()
		{
			if (!atLineStart) return;
			if (firstFieldInItem)
			{
				WriteStructure('-');
				sink.Append(' ');
				firstFieldInItem = false;
			}
			else if (inItem)
			{
				sink.Append("  ");
			}
			atLineStart = false;
		}

		// ── Field (scalar) ─────────────────────────────────────────────────

		public void Field(ReadOnlySpan<char> name, bool value)
		{
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			WriteBool(value);
			sink.Append('\n');
			atLineStart = true;
		}

		public void Field(ReadOnlySpan<char> name, int value)
		{
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			WriteNumber(value.ToString(CultureInfo.InvariantCulture));
			sink.Append('\n');
			atLineStart = true;
		}

		public void Field(ReadOnlySpan<char> name, long value)
		{
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			WriteNumber(value.ToString(CultureInfo.InvariantCulture));
			sink.Append('\n');
			atLineStart = true;
		}

		public void Field(ReadOnlySpan<char> name, double value)
		{
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			var s = value.ToString("0.######################", CultureInfo.InvariantCulture);
			if (s.IndexOf('.') == -1) s += ".0";
			WriteNumber(s);
			sink.Append('\n');
			atLineStart = true;
		}

		public void Field(ReadOnlySpan<char> name, decimal value)
		{
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			var s = value.ToString("0.######################", CultureInfo.InvariantCulture);
			if (s.IndexOf('.') == -1) s += ".0";
			WriteNumber(s);
			sink.Append('\n');
			atLineStart = true;
		}

		public void Field(ReadOnlySpan<char> name, string value)
		{
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			if (value == null)
			{
				WriteNull();
			}
			else
			{
				WriteString(value);
			}
			sink.Append('\n');
			atLineStart = true;
		}

		public void Field(ReadOnlySpan<char> name, DateTime value)
		{
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			string s;
			if (value.Hour == 0 && value.Minute == 0 && value.Second == 0)
				s = value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
			else
				s = value.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
			WriteNumber(s);  // date treated as bareword (no quotes)
			sink.Append('\n');
			atLineStart = true;
		}

		public void Field(ReadOnlySpan<char> name, char value)
		{
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			WriteString(value.ToString());
			sink.Append('\n');
			atLineStart = true;
		}

		// ── Field (collection of primitives — inline array) ────────────────

		private void WriteInlineArrayHeader(ReadOnlySpan<char> name)
		{
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			WriteStructure('[');
		}

		private void WriteInlineArrayFooter()
		{
			WriteStructure(']');
			sink.Append('\n');
			atLineStart = true;
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<int> values)
		{
			WriteInlineArrayHeader(name);
			bool first = true;
			foreach (var v in values)
			{
				if (!first) sink.Append(", ");
				WriteNumber(v.ToString(CultureInfo.InvariantCulture));
				first = false;
			}
			WriteInlineArrayFooter();
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<long> values)
		{
			WriteInlineArrayHeader(name);
			bool first = true;
			foreach (var v in values)
			{
				if (!first) sink.Append(", ");
				WriteNumber(v.ToString(CultureInfo.InvariantCulture));
				first = false;
			}
			WriteInlineArrayFooter();
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<double> values)
		{
			WriteInlineArrayHeader(name);
			bool first = true;
			foreach (var v in values)
			{
				if (!first) sink.Append(", ");
				var s = v.ToString("0.######################", CultureInfo.InvariantCulture);
				if (s.IndexOf('.') == -1) s += ".0";
				WriteNumber(s);
				first = false;
			}
			WriteInlineArrayFooter();
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<decimal> values)
		{
			WriteInlineArrayHeader(name);
			bool first = true;
			foreach (var v in values)
			{
				if (!first) sink.Append(", ");
				var s = v.ToString("0.######################", CultureInfo.InvariantCulture);
				if (s.IndexOf('.') == -1) s += ".0";
				WriteNumber(s);
				first = false;
			}
			WriteInlineArrayFooter();
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<string> values)
		{
			WriteInlineArrayHeader(name);
			bool first = true;
			foreach (var v in values)
			{
				if (!first) sink.Append(", ");
				if (v == null) WriteNull();
				else WriteString(v);
				first = false;
			}
			WriteInlineArrayFooter();
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<bool> values)
		{
			WriteInlineArrayHeader(name);
			bool first = true;
			foreach (var v in values)
			{
				if (!first) sink.Append(", ");
				WriteBool(v);
				first = false;
			}
			WriteInlineArrayFooter();
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<DateTime> values)
		{
			WriteInlineArrayHeader(name);
			bool first = true;
			foreach (var v in values)
			{
				if (!first) sink.Append(", ");
				string s;
				if (v.Hour == 0 && v.Minute == 0 && v.Second == 0)
					s = v.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
				else
					s = v.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
				WriteNumber(s);
				first = false;
			}
			WriteInlineArrayFooter();
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<object> values)
		{
			WriteInlineArrayHeader(name);
			bool first = true;
			foreach (var v in values)
			{
				if (!first) sink.Append(", ");
				WriteRuntimeValue(v);
				first = false;
			}
			WriteInlineArrayFooter();
		}

		// ── Field (fallback runtime-dispatch) ──────────────────────────────

		public void Field(ReadOnlySpan<char> name, object value)
		{
			if (value == null)
			{
				Field(name, (string)null);
				return;
			}

			var type = value.GetType();
			if (type == typeof(int)) { Field(name, (int)value); return; }
			if (type == typeof(string)) { Field(name, (string)value); return; }
			if (type == typeof(double)) { Field(name, (double)value); return; }
			if (type == typeof(decimal)) { Field(name, (decimal)value); return; }
			if (type == typeof(DateTime)) { Field(name, (DateTime)value); return; }
			if (type == typeof(bool)) { Field(name, (bool)value); return; }
			if (type == typeof(long)) { Field(name, (long)value); return; }
			if (type == typeof(char)) { Field(name, (char)value); return; }
			if (type.IsEnum) { Field(name, value.ToString()); return; }

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
			{
				Type elementType = type.GetGenericArguments()[0];
				DispatchCollection(name, value, elementType);
				return;
			}
			if (type.IsArray)
			{
				Type elementType = type.GetElementType();
				DispatchCollection(name, value, elementType);
				return;
			}

			// Last resort: a domain object that exposes a print(StringBuilder)
			// method, or just ToString(). The print(StringBuilder) escape
			// hatch is JSON-shaped (the contract is "your print emits JSON
			// fragments"); under TOON we still call it but emit the raw
			// string as-is, prefixed by the key. Not pretty but preserves
			// the legacy contract.
			MethodInfo posiblePrint = type
				.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
				.FirstOrDefault(m =>
					m.Name.ToLower() == "print" &&
					m.GetParameters().Length == 1 &&
					m.GetParameters()[0].ParameterType == typeof(StringBuilder));
			WriteFieldPrefix();
			WriteKey(name);
			WriteStructure(':');
			sink.Append(' ');
			if (posiblePrint != null)
			{
				var tmp = new StringBuilder();
				posiblePrint.Invoke(value, new object[] { tmp });
				sink.Append(tmp.ToString());
			}
			else
			{
				WriteString(value.ToString());
			}
			sink.Append('\n');
			atLineStart = true;
		}

		private void DispatchCollection(ReadOnlySpan<char> name, object collection, Type elementType)
		{
			if (elementType == typeof(int)) Field(name, (IEnumerable<int>)collection);
			else if (elementType == typeof(string)) Field(name, (IEnumerable<string>)collection);
			else if (elementType == typeof(double)) Field(name, (IEnumerable<double>)collection);
			else if (elementType == typeof(decimal)) Field(name, (IEnumerable<decimal>)collection);
			else if (elementType == typeof(DateTime)) Field(name, (IEnumerable<DateTime>)collection);
			else if (elementType == typeof(bool)) Field(name, (IEnumerable<bool>)collection);
			else if (elementType == typeof(long)) Field(name, (IEnumerable<long>)collection);
			else Field(name, (IEnumerable<object>)collection);
		}

		private void WriteRuntimeValue(object value)
		{
			if (value == null) { WriteNull(); return; }
			var type = value.GetType();
			if (type == typeof(int)) WriteNumber(((int)value).ToString(CultureInfo.InvariantCulture));
			else if (type == typeof(long)) WriteNumber(((long)value).ToString(CultureInfo.InvariantCulture));
			else if (type == typeof(double))
			{
				var s = ((double)value).ToString("0.######################", CultureInfo.InvariantCulture);
				if (s.IndexOf('.') == -1) s += ".0";
				WriteNumber(s);
			}
			else if (type == typeof(decimal))
			{
				var s = ((decimal)value).ToString("0.######################", CultureInfo.InvariantCulture);
				if (s.IndexOf('.') == -1) s += ".0";
				WriteNumber(s);
			}
			else if (type == typeof(bool)) WriteBool((bool)value);
			else if (type == typeof(string)) WriteString((string)value);
			else if (type == typeof(DateTime))
			{
				var dt = (DateTime)value;
				string s = (dt.Hour == 0 && dt.Minute == 0 && dt.Second == 0)
					? dt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
					: dt.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
				WriteNumber(s);
			}
			else WriteString(value.ToString());
		}

		// ── EWIs ───────────────────────────────────────────────────────────

		public void BeginEwis()
		{
			WriteFieldPrefix();
			WriteKey("EWI");
			WriteStructure(':');
			sink.Append('\n');
			atLineStart = true;
			// Each Ewi() call writes a bulleted line.
		}

		public void Ewi(string type, string value)
		{
			// "- type: value" with current indent context.
			if (inItem)
			{
				sink.Append("    ");
			}
			else
			{
				sink.Append("  ");
			}
			WriteStructure('-');
			sink.Append(' ');
			WriteKey(type);
			WriteStructure(':');
			sink.Append(' ');
			WriteString(value);
			sink.Append('\n');
			atLineStart = true;
		}

		public void EndEwis()
		{
			// No closing marker.
		}

		// ── Raw splice (JSON-only — non-JSON formatters reject) ────────────

		public void RawSplice(string stream, int startIndex, int count)
		{
			throw new LanguageException(
				"RawSplice is only valid under JsonFormatter. " +
				"This call indicates a V1 eval-as-statement reached a non-JSON " +
				"formatter path, which should be unreachable by construction.");
		}

		// ── Low-level helpers (ANSI-aware token writers) ───────────────────

		private void WriteKey(ReadOnlySpan<char> name)
		{
			if (useAnsi) sink.Append(ANSI_KEY);
			EscapeKey(name);
			if (useAnsi) sink.Append(ANSI_RESET);
		}

		private void WriteString(string value)
		{
			if (useAnsi) sink.Append(ANSI_STRING);
			sink.Append('"');
			EscapeStringValue(value);
			sink.Append('"');
			if (useAnsi) sink.Append(ANSI_RESET);
		}

		private void WriteNumber(string lexeme)
		{
			if (useAnsi) sink.Append(ANSI_NUMBER);
			sink.Append(lexeme);
			if (useAnsi) sink.Append(ANSI_RESET);
		}

		private void WriteBool(bool value)
		{
			if (useAnsi) sink.Append(ANSI_BOOL);
			sink.Append(value ? "true" : "false");
			if (useAnsi) sink.Append(ANSI_RESET);
		}

		private void WriteNull()
		{
			if (useAnsi) sink.Append(ANSI_NULL);
			sink.Append("null");
			if (useAnsi) sink.Append(ANSI_RESET);
		}

		private void WriteStructure(char c)
		{
			// Structure chars (`-`, `:`, `[`, `]`) stay default color.
			sink.Append(c);
		}

		private void EscapeKey(ReadOnlySpan<char> name)
		{
			// Keys: minimal escaping. Replace control chars with \uXXXX,
			// preserve unicode, do NOT quote.
			foreach (var c in name)
			{
				if (c == '\\') sink.Append("\\\\");
				else if (c == '"') sink.Append("\\\"");
				else if (char.IsControl(c))
				{
					sink.Append("\\u");
					sink.Append(((int)c).ToString("x4"));
				}
				else sink.Append(c);
			}
		}

		private void EscapeStringValue(string value)
		{
			foreach (var c in value)
			{
				switch (c)
				{
					case '\\': sink.Append('\\').Append('\\'); break;
					case '"': sink.Append('\\').Append('"'); break;
					case '\b': sink.Append("\\b"); break;
					case '\f': sink.Append("\\f"); break;
					case '\n': sink.Append("\\n"); break;
					case '\r': sink.Append("\\r"); break;
					case '\t': sink.Append("\\t"); break;
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
	}
}
