using System;
using System.Collections.Generic;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Formatters
{
	/// <summary>
	/// Strategy interface for byte-emission from the DSL interpreter to a sink.
	///
	/// <para>
	/// Formatters are stateful per-instance and pooled by the framework. The
	/// framework controls allocation via <see cref="CreateNew"/> and recycling
	/// via <see cref="Reset"/>. Developers implementing a custom formatter MUST
	/// implement both correctly: the framework reuses instances across documents
	/// to avoid trashing the runtime under high-throughput PerformQuery loads.
	/// </para>
	///
	/// <para>
	/// Lifecycle of a single document:
	/// <see cref="BeginDocument"/> →
	/// ([Field | BeginCollection ... EndCollection]*) →
	/// [BeginEwis Ewi* EndEwis]? →
	/// <see cref="EndDocument"/>
	/// </para>
	/// </summary>
	public interface IOutputFormatter
	{
		// ── Pool lifecycle ─────────────────────────────────────────────────
		// Framework-controlled, never developer-invoked.

		/// <summary>
		/// Produce a fresh instance with the same configuration. Invoked by
		/// the framework only when the Output pool expands.
		/// </summary>
		IOutputFormatter CreateNew();

		/// <summary>
		/// Clear internal state so this instance can be reused for a new
		/// document. Invoked by the framework when an Output returns to the
		/// pool. Implementations should null/reset any field touched between
		/// <see cref="BeginDocument"/> and <see cref="EndDocument"/>.
		/// </summary>
		void Reset();

		// ── Document lifecycle ─────────────────────────────────────────────

		void BeginDocument(StringBuilder sink);
		void EndDocument();

		// ── Collection (for-block in the DSL) ──────────────────────────────
		//
		// DSL `for <expr> { <body> }` maps to:
		//   BeginCollection()
		//     BeginCollectionItem() <body emits Field calls> EndCollectionItem()
		//     [more iterations ...]
		//   EndCollection(collectionName)
		//
		// Today the DSL only knows collectionName at the close site (legacy
		// CloseFor(alias) signature). itemName propagation is deferred to a
		// future phase (XmlFormatter may need it).

		void BeginCollection();
		void EndCollection(ReadOnlySpan<char> collectionName);
		void BeginCollectionItem();
		void EndCollectionItem();

		// ── Field (scalar) ─────────────────────────────────────────────────

		void Field(ReadOnlySpan<char> name, bool value);
		void Field(ReadOnlySpan<char> name, int value);
		void Field(ReadOnlySpan<char> name, long value);
		void Field(ReadOnlySpan<char> name, double value);
		void Field(ReadOnlySpan<char> name, decimal value);
		void Field(ReadOnlySpan<char> name, string value);
		void Field(ReadOnlySpan<char> name, DateTime value);
		void Field(ReadOnlySpan<char> name, char value);

		// ── Field (collection of primitives) ───────────────────────────────

		void Field(ReadOnlySpan<char> name, IEnumerable<int> values);
		void Field(ReadOnlySpan<char> name, IEnumerable<long> values);
		void Field(ReadOnlySpan<char> name, IEnumerable<double> values);
		void Field(ReadOnlySpan<char> name, IEnumerable<decimal> values);
		void Field(ReadOnlySpan<char> name, IEnumerable<string> values);
		void Field(ReadOnlySpan<char> name, IEnumerable<bool> values);
		void Field(ReadOnlySpan<char> name, IEnumerable<DateTime> values);
		void Field(ReadOnlySpan<char> name, IEnumerable<object> values);

		// ── Field (fallback runtime-dispatch) ──────────────────────────────
		// Used when the type isn't known at compile time. Implementations
		// dispatch by runtime type to one of the overloads above, or fall
		// back to a serialization strategy of their choice (enum.ToString,
		// user-supplied print(StringBuilder) method via reflection, etc).

		void Field(ReadOnlySpan<char> name, object value);

		// ── EWIs (Errors / Warnings / Information) sidecar ─────────────────
		//
		// Emitted at the close of a document, before EndDocument(). Each
		// formatter renders EWIs in its own dialect:
		//   JSON: "EWI":[{"type1":"value1"}, ...]
		//   TOON: (Phase 2)
		//   XML:  (Phase 3)

		void BeginEwis();
		void Ewi(string type, string value);
		void EndEwis();

		// ── Raw splice (JSON-only escape hatch) ────────────────────────────
		//
		// Used by EvalStatement (V1, deprecated) to splice a substring of a
		// pre-formatted sub-document into the parent. The slice strips the
		// wrapper characters of the sub-document and embeds its fields at
		// the parent's current cursor. Structurally valid only under JSON;
		// non-JSON formatters MUST throw LanguageException with a clear
		// diagnostic. By construction this method is never reached under
		// Actor V2 paths (V2 grammar rejects eval-as-statement).

		void RawSplice(string stream, int startIndex, int count);

		// ── Document introspection ─────────────────────────────────────────

		/// <summary>
		/// True when no field, collection, or EWI has been emitted in the
		/// current document. Equivalent to the legacy Output.Vacio() check.
		/// </summary>
		bool IsDocumentEmpty { get; }

		/// <summary>
		/// When true, Output.ToString() returns <see cref="string.Empty"/>
		/// if the document is empty (rather than the formatter's "empty
		/// document" literal like JSON's "{}"). JsonFormatter sets this
		/// true to preserve the legacy {} → "" collapse that callers depend
		/// on.
		/// </summary>
		bool CollapseEmptyToString { get; }
	}
}
