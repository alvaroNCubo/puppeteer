using Puppeteer;
using Puppeteer.EventSourcing.Interpreter.Formatters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Choreography.Formatters
{
	/// <summary>
	/// XML byte-emission strategy. Transport-facing formatter (web services,
	/// SOAP, observability pipelines that consume XML). Lives in
	/// Choreography.csproj because Choreography is where transport-adapter
	/// patterns live; Puppeteer.csproj only hosts the formatters Actor V2
	/// standalone needs (Json, Toon).
	///
	/// <para>
	/// Dialect:
	/// <list type="bullet">
	/// <item>Root: <c>&lt;root&gt;...&lt;/root&gt;</c>. No XML declaration
	/// (caller can prepend <c>&lt;?xml ...?&gt;</c> if needed).</item>
	/// <item>Field (escalar): <c>&lt;key&gt;value&lt;/key&gt;</c>.</item>
	/// <item>Null: empty element <c>&lt;key/&gt;</c>.</item>
	/// <item>Inline arrays of primitives: repeated elements with the field
	/// name as tag. <c>&lt;nums&gt;1&lt;/nums&gt;&lt;nums&gt;2&lt;/nums&gt;</c>.</item>
	/// <item>Empty inline array: <c>&lt;nums/&gt;</c>.</item>
	/// <item>Collection (for-block): outer element with the collection
	/// name, repeated <c>&lt;item&gt;...&lt;/item&gt;</c> children.</item>
	/// <item>Empty collection: self-closing <c>&lt;items/&gt;</c>.</item>
	/// <item>Empty iteration silently dropped (matches Json/Toon).</item>
	/// <item>EWIs section: <c>&lt;EWI&gt;&lt;warning&gt;...&lt;/warning&gt;...&lt;/EWI&gt;</c>
	/// — the EWI type becomes the inner tag name.</item>
	/// <item>Pretty-print: 2-space indent per nesting level.</item>
	/// </list>
	/// </para>
	///
	/// <para>
	/// String values, body content, and tag names are XML-escaped:
	/// <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>"</c>, <c>'</c>, and
	/// control chars converted to numeric entities <c>&amp;#xNN;</c>.
	/// </para>
	/// </summary>
	public sealed class XmlFormatter : IOutputFormatter
	{
		private const string RootTag = "root";
		private const string ItemTag = "item";
		private const string EwiTag = "EWI";

		// ── State (per-document, reset at Reset()) ─────────────────────────
		private StringBuilder sink;
		private int indentLevel;
		private bool atLineStart;
		private bool inItem;
		private bool firstFieldInItem;
		private Stack<Frame> levels;

		private readonly struct Frame
		{
			public readonly StringBuilder Sink;
			public readonly int IndentLevel;
			public readonly bool AtLineStart;
			public readonly bool InItem;
			public readonly bool FirstFieldInItem;

			public Frame(StringBuilder sink, int indentLevel, bool atLineStart, bool inItem, bool firstFieldInItem)
			{
				Sink = sink;
				IndentLevel = indentLevel;
				AtLineStart = atLineStart;
				InItem = inItem;
				FirstFieldInItem = firstFieldInItem;
			}
		}

		private static readonly CultureInfo USculture = new CultureInfo("en-US");

		static XmlFormatter()
		{
			CultureInfo.DefaultThreadCurrentCulture = USculture;
			CultureInfo.DefaultThreadCurrentUICulture = USculture;
		}

		// ── Pool lifecycle ─────────────────────────────────────────────────

		public IOutputFormatter CreateNew() => new XmlFormatter();

		public void Reset()
		{
			sink = null;
			indentLevel = 0;
			atLineStart = true;
			inItem = false;
			firstFieldInItem = false;
			levels?.Clear();
		}

		public bool CollapseEmptyToString => true;

		// IsDocumentEmpty: true between BeginDocument and any field/collection
		// emission. The root tag is emitted lazily on first content (or stays
		// self-closing if document ends empty).
		public bool IsDocumentEmpty => sink != null && sink.Length == 0;

		// ── Document lifecycle ─────────────────────────────────────────────

		public void BeginDocument(StringBuilder sink)
		{
			this.sink = sink;
			indentLevel = 0;
			atLineStart = true;
			inItem = false;
			firstFieldInItem = false;
			// Emit "<root>\n" eagerly; if document ends empty we overwrite
			// to "<root/>\n" in EndDocument by checking sink content.
			sink.Append('<').Append(RootTag).Append('>').Append('\n');
			indentLevel = 1;
			atLineStart = true;
		}

		public void EndDocument()
		{
			// If nothing was emitted between BeginDocument and now, collapse
			// "<root>\n" to "<root/>\n".
			if (sink.Length == RootTag.Length + 3 /* "<root>\n" length */)
			{
				sink.Clear();
				sink.Append('<').Append(RootTag).Append("/>").Append('\n');
				return;
			}
			indentLevel = 0;
			sink.Append('<').Append('/').Append(RootTag).Append('>').Append('\n');
		}

		// ── Collection (for-block) ─────────────────────────────────────────

		public void BeginCollection()
		{
			if (levels == null)
			{
				levels = new Stack<Frame>();
			}
			levels.Push(new Frame(sink, indentLevel, atLineStart, inItem, firstFieldInItem));

			// Inner buffer at indent 0; merge at EndCollection re-indents.
			sink = new StringBuilder();
			indentLevel = 0;
			atLineStart = true;
			inItem = false;
			firstFieldInItem = false;
		}

		public void EndCollection(ReadOnlySpan<char> collectionName)
		{
			string inner = sink.ToString();
			var prev = levels.Pop();
			sink = prev.Sink;
			indentLevel = prev.IndentLevel;
			atLineStart = prev.AtLineStart;
			inItem = prev.InItem;
			firstFieldInItem = prev.FirstFieldInItem;

			if (inner.Length == 0)
			{
				// Empty collection — self-closing element at current indent.
				AppendIndent();
				sink.Append('<');
				AppendEscapedTag(collectionName);
				sink.Append("/>").Append('\n');
				atLineStart = true;
				return;
			}

			// Open collection element
			AppendIndent();
			sink.Append('<');
			AppendEscapedTag(collectionName);
			sink.Append('>').Append('\n');

			// Each line of inner needs +1 indent
			AppendIndentedInner(inner);

			// Close collection
			AppendIndent();
			sink.Append("</");
			AppendEscapedTag(collectionName);
			sink.Append('>').Append('\n');
			atLineStart = true;
		}

		private void AppendIndentedInner(string inner)
		{
			int start = 0;
			while (start < inner.Length)
			{
				int nl = inner.IndexOf('\n', start);
				int lineEnd = nl < 0 ? inner.Length : nl;
				if (lineEnd > start)
				{
					AppendIndentLevel(indentLevel + 1);
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
			// If no field was emitted in this iteration, firstFieldInItem
			// remained true → silently drop (no <item> emitted). Otherwise,
			// dedent (counterpart to the indent bump in OpenItemIfNeeded)
			// and close.
			if (!firstFieldInItem)
			{
				indentLevel--;
				AppendIndent();
				sink.Append("</").Append(ItemTag).Append('>').Append('\n');
				atLineStart = true;
			}
			inItem = false;
			firstFieldInItem = false;
		}

		// ── Field prefix logic ─────────────────────────────────────────────

		private void OpenItemIfNeeded()
		{
			// On first field inside an item, emit the opening <item> tag
			// at the current indent, then bump indent for the item body.
			if (inItem && firstFieldInItem)
			{
				AppendIndent();
				sink.Append('<').Append(ItemTag).Append('>').Append('\n');
				indentLevel++;
				firstFieldInItem = false;
				atLineStart = true;
			}
		}

		// ── Indent helpers ─────────────────────────────────────────────────

		private void AppendIndent()
		{
			AppendIndentLevel(indentLevel);
		}

		private void AppendIndentLevel(int level)
		{
			for (int i = 0; i < level; i++) sink.Append("  ");
		}

		// ── Field (escalar) ────────────────────────────────────────────────

		public void Field(ReadOnlySpan<char> name, bool value)
		{
			OpenItemIfNeeded();
			EmitScalarField(name, value ? "true" : "false");
		}

		public void Field(ReadOnlySpan<char> name, int value)
		{
			OpenItemIfNeeded();
			EmitScalarField(name, value.ToString(CultureInfo.InvariantCulture));
		}

		public void Field(ReadOnlySpan<char> name, long value)
		{
			OpenItemIfNeeded();
			EmitScalarField(name, value.ToString(CultureInfo.InvariantCulture));
		}

		public void Field(ReadOnlySpan<char> name, double value)
		{
			OpenItemIfNeeded();
			var s = value.ToString("0.######################", CultureInfo.InvariantCulture);
			if (s.IndexOf('.') == -1) s += ".0";
			EmitScalarField(name, s);
		}

		public void Field(ReadOnlySpan<char> name, decimal value)
		{
			OpenItemIfNeeded();
			var s = value.ToString("0.######################", CultureInfo.InvariantCulture);
			if (s.IndexOf('.') == -1) s += ".0";
			EmitScalarField(name, s);
		}

		public void Field(ReadOnlySpan<char> name, string value)
		{
			OpenItemIfNeeded();
			if (value == null)
			{
				EmitNullField(name);
			}
			else
			{
				EmitEscapedScalarField(name, value);
			}
		}

		public void Field(ReadOnlySpan<char> name, DateTime value)
		{
			OpenItemIfNeeded();
			string s = (value.Hour == 0 && value.Minute == 0 && value.Second == 0)
				? value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
				: value.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
			EmitScalarField(name, s);
		}

		public void Field(ReadOnlySpan<char> name, char value)
		{
			OpenItemIfNeeded();
			EmitEscapedScalarField(name, value.ToString());
		}

		private void EmitScalarField(ReadOnlySpan<char> name, string lexeme)
		{
			AppendIndent();
			sink.Append('<');
			AppendEscapedTag(name);
			sink.Append('>');
			sink.Append(lexeme);    // pre-formatted numeric/bool lexeme — no escape needed
			sink.Append("</");
			AppendEscapedTag(name);
			sink.Append('>').Append('\n');
			atLineStart = true;
		}

		private void EmitEscapedScalarField(ReadOnlySpan<char> name, string value)
		{
			AppendIndent();
			sink.Append('<');
			AppendEscapedTag(name);
			sink.Append('>');
			AppendEscapedText(value);
			sink.Append("</");
			AppendEscapedTag(name);
			sink.Append('>').Append('\n');
			atLineStart = true;
		}

		private void EmitNullField(ReadOnlySpan<char> name)
		{
			AppendIndent();
			sink.Append('<');
			AppendEscapedTag(name);
			sink.Append("/>").Append('\n');
			atLineStart = true;
		}

		// ── Field (inline collections — repeated elements with same tag) ───

		public void Field(ReadOnlySpan<char> name, IEnumerable<int> values)
		{
			OpenItemIfNeeded();
			EmitRepeatedScalars(name, values, v => v.ToString(CultureInfo.InvariantCulture));
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<long> values)
		{
			OpenItemIfNeeded();
			EmitRepeatedScalars(name, values, v => v.ToString(CultureInfo.InvariantCulture));
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<double> values)
		{
			OpenItemIfNeeded();
			EmitRepeatedScalars(name, values, v =>
			{
				var s = v.ToString("0.######################", CultureInfo.InvariantCulture);
				if (s.IndexOf('.') == -1) s += ".0";
				return s;
			});
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<decimal> values)
		{
			OpenItemIfNeeded();
			EmitRepeatedScalars(name, values, v =>
			{
				var s = v.ToString("0.######################", CultureInfo.InvariantCulture);
				if (s.IndexOf('.') == -1) s += ".0";
				return s;
			});
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<string> values)
		{
			OpenItemIfNeeded();
			EmitRepeatedScalars(name, values, v => v, escapeBody: true);
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<bool> values)
		{
			OpenItemIfNeeded();
			EmitRepeatedScalars(name, values, v => v ? "true" : "false");
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<DateTime> values)
		{
			OpenItemIfNeeded();
			EmitRepeatedScalars(name, values, v =>
			{
				return (v.Hour == 0 && v.Minute == 0 && v.Second == 0)
					? v.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
					: v.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
			});
		}

		public void Field(ReadOnlySpan<char> name, IEnumerable<object> values)
		{
			OpenItemIfNeeded();
			// Materialize so we can detect empty.
			var list = values.ToList();
			if (list.Count == 0)
			{
				AppendIndent();
				sink.Append('<');
				AppendEscapedTag(name);
				sink.Append("/>").Append('\n');
				atLineStart = true;
				return;
			}
			foreach (var v in list)
			{
				AppendIndent();
				sink.Append('<');
				AppendEscapedTag(name);
				sink.Append('>');
				WriteRuntimeValueBody(v);
				sink.Append("</");
				AppendEscapedTag(name);
				sink.Append('>').Append('\n');
			}
			atLineStart = true;
		}

		private void EmitRepeatedScalars<T>(ReadOnlySpan<char> name, IEnumerable<T> values, Func<T, string> render, bool escapeBody = false)
		{
			// Materialize to detect empty
			var list = values.ToList();
			if (list.Count == 0)
			{
				AppendIndent();
				sink.Append('<');
				AppendEscapedTag(name);
				sink.Append("/>").Append('\n');
				atLineStart = true;
				return;
			}
			foreach (var v in list)
			{
				AppendIndent();
				sink.Append('<');
				AppendEscapedTag(name);
				sink.Append('>');
				if (escapeBody)
				{
					if (v == null)
					{
						// Null inside collection — emit empty
					}
					else
					{
						AppendEscapedText(render(v));
					}
				}
				else
				{
					sink.Append(render(v));
				}
				sink.Append("</");
				AppendEscapedTag(name);
				sink.Append('>').Append('\n');
			}
			atLineStart = true;
		}

		private void WriteRuntimeValueBody(object value)
		{
			if (value == null) return;
			var type = value.GetType();
			if (type == typeof(int)) sink.Append(((int)value).ToString(CultureInfo.InvariantCulture));
			else if (type == typeof(long)) sink.Append(((long)value).ToString(CultureInfo.InvariantCulture));
			else if (type == typeof(double))
			{
				var s = ((double)value).ToString("0.######################", CultureInfo.InvariantCulture);
				if (s.IndexOf('.') == -1) s += ".0";
				sink.Append(s);
			}
			else if (type == typeof(decimal))
			{
				var s = ((decimal)value).ToString("0.######################", CultureInfo.InvariantCulture);
				if (s.IndexOf('.') == -1) s += ".0";
				sink.Append(s);
			}
			else if (type == typeof(bool)) sink.Append(((bool)value) ? "true" : "false");
			else if (type == typeof(string)) AppendEscapedText((string)value);
			else if (type == typeof(DateTime))
			{
				var dt = (DateTime)value;
				string s = (dt.Hour == 0 && dt.Minute == 0 && dt.Second == 0)
					? dt.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
					: dt.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
				sink.Append(s);
			}
			else AppendEscapedText(value.ToString());
		}

		// ── Field (fallback runtime-dispatch) ──────────────────────────────

		public void Field(ReadOnlySpan<char> name, object value)
		{
			if (value == null) { Field(name, (string)null); return; }
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

			// Last resort
			OpenItemIfNeeded();
			MethodInfo posiblePrint = type
				.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
				.FirstOrDefault(m =>
					m.Name.ToLower() == "print" &&
					m.GetParameters().Length == 1 &&
					m.GetParameters()[0].ParameterType == typeof(StringBuilder));
			AppendIndent();
			sink.Append('<');
			AppendEscapedTag(name);
			sink.Append('>');
			if (posiblePrint != null)
			{
				var tmp = new StringBuilder();
				posiblePrint.Invoke(value, new object[] { tmp });
				// Treat output as raw text; XML-escape it conservatively.
				AppendEscapedText(tmp.ToString());
			}
			else
			{
				AppendEscapedText(value.ToString());
			}
			sink.Append("</");
			AppendEscapedTag(name);
			sink.Append('>').Append('\n');
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

		// ── EWIs ───────────────────────────────────────────────────────────

		public void BeginEwis()
		{
			AppendIndent();
			sink.Append('<').Append(EwiTag).Append('>').Append('\n');
			indentLevel++;
			atLineStart = true;
		}

		public void Ewi(string type, string value)
		{
			AppendIndent();
			sink.Append('<');
			AppendEscapedTag(type);
			sink.Append('>');
			AppendEscapedText(value);
			sink.Append("</");
			AppendEscapedTag(type);
			sink.Append('>').Append('\n');
			atLineStart = true;
		}

		public void EndEwis()
		{
			indentLevel--;
			AppendIndent();
			sink.Append("</").Append(EwiTag).Append('>').Append('\n');
			atLineStart = true;
		}

		// ── Raw splice (JSON-only — reject) ────────────────────────────────

		public void RawSplice(string stream, int startIndex, int count)
		{
			throw new LanguageException(
				"RawSplice is only valid under JsonFormatter. " +
				"This call indicates a V1 eval-as-statement reached a non-JSON " +
				"formatter path, which should be unreachable by construction.");
		}

		// ── XML escaping ───────────────────────────────────────────────────

		// Tag names: must be valid XML names. We do NOT do full validation
		// here (the DSL alias is the source); we only escape characters
		// that would break XML parsing if literally embedded.
		private void AppendEscapedTag(ReadOnlySpan<char> name)
		{
			foreach (var c in name)
			{
				if (c == '<' || c == '>' || c == '&' || c == '"' || c == '\'' || char.IsControl(c) || c == ' ')
				{
					// Replace illegal chars with '_' to keep the document parseable.
					// (Tag names with these chars are invalid XML; this is a
					// best-effort fallback.)
					sink.Append('_');
				}
				else
				{
					sink.Append(c);
				}
			}
		}

		private void AppendEscapedText(string value)
		{
			foreach (var c in value)
			{
				switch (c)
				{
					case '&': sink.Append("&amp;"); break;
					case '<': sink.Append("&lt;"); break;
					case '>': sink.Append("&gt;"); break;
					case '"': sink.Append("&quot;"); break;
					case '\'': sink.Append("&apos;"); break;
					default:
						if (char.IsControl(c) && c != '\t' && c != '\n' && c != '\r')
						{
							sink.Append("&#x");
							sink.Append(((int)c).ToString("x"));
							sink.Append(';');
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
