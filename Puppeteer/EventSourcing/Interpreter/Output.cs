using Puppeteer.EventSourcing.Interpreter.Formatters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Puppeteer.EventSourcing.Interpreter
{
	/// <summary>
	/// Shim around an <see cref="IOutputFormatter"/> that preserves the legacy
	/// Output API the interpreter and reflection-Expression code paths bind
	/// to. All byte-emission lives in the formatter; this class only:
	/// holds the sink, EWI accumulator, and the active formatter instance;
	/// delegates each method to the formatter; preserves the legacy
	/// "{}" → "" collapse in <see cref="ToString"/>; and gates everything on
	/// <see cref="escribirSalida"/> (the no-output / rehydrating mode).
	///
	/// <para>
	/// The 25+ typed <c>Append(ReadOnlySpan&lt;char&gt;, T)</c> overloads MUST
	/// keep their exact signatures: <c>OutputStatementBase</c> caches them by
	/// reflection at type-load time and the compiled Expression tree binds
	/// directly to them.
	/// </para>
	/// </summary>
	internal class Output
	{
		private readonly bool escribirSalida = true;
		private StringBuilder sink;
		private IOutputFormatter formatter;
		private List<Tuple<string, string>> ewis;

		private static readonly CultureInfo USculture = new CultureInfo("en-US");

		private static readonly IOutputFormatter DefaultPrototype = new JsonFormatter();

		private Output(bool conSalida)
		{
			CultureInfo.DefaultThreadCurrentCulture = USculture;
			CultureInfo.DefaultThreadCurrentUICulture = USculture;

			this.escribirSalida = conSalida;

			if (escribirSalida)
			{
				sink = new StringBuilder();
				ewis = new List<Tuple<string, string>>();
				formatter = new JsonFormatter();
				formatter.BeginDocument(sink);
			}
		}

		/// <summary>
		/// Install the matching formatter for the given prototype, reusing
		/// the existing instance if the type already matches (Reset) or
		/// swapping to a fresh instance via prototype.CreateNew(). Always
		/// re-initializes the sink with formatter.BeginDocument. Called by
		/// the ExecutionOutput pool on Rent.
		/// </summary>
		internal void InstallFormatter(IOutputFormatter prototype)
		{
			if (!escribirSalida) return;
			var actualPrototype = prototype ?? DefaultPrototype;
			if (formatter == null || formatter.GetType() != actualPrototype.GetType())
			{
				formatter = actualPrototype.CreateNew();
			}
			else
			{
				formatter.Reset();
			}
			sink.Clear();
			formatter.BeginDocument(sink);
			ewis.Clear();
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
			// Install the active formatter (if any) — mirrors ExecutionOutputPool.Rent.
			// The compiled-mode path (Program.ExecuteExpression) rents an Output
			// directly (no ExecutionOutput wrapper), so without this call the
			// FormatterContext push from StageV2.PerformQry / PerformCmd would not
			// reach the renderer and output would always fall back to default JSON.
			result.InstallFormatter(Formatters.FormatterContext.Active);
			return result;
		}

		internal static Output RentWithoutOutput()
		{
			var result = _sinSalidaPool.Rent();
			result.InstallFormatter(Formatters.FormatterContext.Active);
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

		// ── Document lifecycle ─────────────────────────────────────────────

		internal void Clear()
		{
			if (escribirSalida)
			{
				formatter.Reset();
				sink.Clear();
				formatter.BeginDocument(sink);
				ewis.Clear();
			}
		}

		internal void Finish()
		{
			if (escribirSalida)
			{
				// EWIs go inside the document, before EndDocument.
				if (ewis.Count > 0)
				{
					formatter.BeginEwis();
					foreach (var ewi in ewis)
					{
						formatter.Ewi(ewi.Item1, ewi.Item2);
					}
					formatter.EndEwis();
				}
				formatter.EndDocument();
			}
		}

		// ── Collection (for-block) ─────────────────────────────────────────

		internal void OpenFor()
		{
			if (escribirSalida) formatter.BeginCollection();
		}

		internal void CloseFor(string alias)
		{
			if (escribirSalida) formatter.EndCollection(alias.AsSpan());
		}

		internal void BeginForMoveNext()
		{
			if (escribirSalida) formatter.BeginCollectionItem();
		}

		internal void EndForMoveNext()
		{
			if (escribirSalida) formatter.EndCollectionItem();
		}

		// ── Document introspection ─────────────────────────────────────────

		internal bool IsWriting => escribirSalida;

		internal StringBuilder Salidas => sink;

		internal bool Vacio() => escribirSalida && formatter.IsDocumentEmpty;

		// ── EWIs (held in Output; formatter renders them) ──────────────────

		internal bool HasEWIS()
		{
			return this.escribirSalida && ewis.Count > 0;
		}

		internal void AddEWI(string type, string value)
		{
			if (escribirSalida) ewis.Add(new Tuple<string, string>(type, value));
		}

		// ── Typed Append overloads (preserve EXACT signatures for the ──────
		// ── reflection cache in OutputStatementBase). ──────────────────────

		internal void Append(ReadOnlySpan<char> alias, bool value)
		{
			if (escribirSalida) formatter.Field(alias, value);
		}

		internal void Append(ReadOnlySpan<char> alias, string value)
		{
			if (escribirSalida) formatter.Field(alias, value);
		}

		internal void Append(ReadOnlySpan<char> alias, int value)
		{
			if (escribirSalida) formatter.Field(alias, value);
		}

		internal void Append(ReadOnlySpan<char> alias, double value)
		{
			if (escribirSalida) formatter.Field(alias, value);
		}

		internal void Append(ReadOnlySpan<char> alias, long value)
		{
			if (escribirSalida) formatter.Field(alias, value);
		}

		internal void Append(ReadOnlySpan<char> alias, DateTime value)
		{
			if (escribirSalida) formatter.Field(alias, value);
		}

		internal void Append(ReadOnlySpan<char> alias, decimal value)
		{
			if (escribirSalida) formatter.Field(alias, value);
		}

		internal void Append(ReadOnlySpan<char> alias, object values)
		{
			if (escribirSalida) formatter.Field(alias, values);
		}

		internal void Append(ReadOnlySpan<char> alias, object[] values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<object> values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<object> values) => AppendPrivate(alias, values);

		internal void Append(string alias, object[] values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, List<object> values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, IEnumerable<object> values) => AppendPrivate(alias.AsSpan(), values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<object> values)
		{
			if (escribirSalida) formatter.Field(alias, values);
		}

		internal void Append(ReadOnlySpan<char> alias, int[] values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<int> values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<int> values) => AppendPrivate(alias, values);

		internal void Append(string alias, int[] values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, List<int> values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, IEnumerable<int> values) => AppendPrivate(alias.AsSpan(), values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<int> values)
		{
			if (escribirSalida) formatter.Field(alias, values);
		}

		internal void Append(ReadOnlySpan<char> alias, double[] values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<double> values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<double> values) => AppendPrivate(alias, values);

		internal void Append(string alias, double[] values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, List<double> values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, IEnumerable<double> values) => AppendPrivate(alias.AsSpan(), values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<double> values)
		{
			if (escribirSalida) formatter.Field(alias, values);
		}

		internal void Append(ReadOnlySpan<char> alias, decimal[] values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<decimal> values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<decimal> values) => AppendPrivate(alias, values);

		internal void Append(string alias, decimal[] values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, List<decimal> values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, IEnumerable<decimal> values) => AppendPrivate(alias.AsSpan(), values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<decimal> values)
		{
			if (escribirSalida) formatter.Field(alias, values);
		}

		internal void Append(ReadOnlySpan<char> alias, bool[] values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<bool> values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<bool> values) => AppendPrivate(alias, values);

		internal void Append(string alias, bool[] values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, List<bool> values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, IEnumerable<bool> values) => AppendPrivate(alias.AsSpan(), values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<bool> values)
		{
			if (escribirSalida) formatter.Field(alias, values);
		}

		internal void Append(ReadOnlySpan<char> alias, string[] values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<string> values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<string> values) => AppendPrivate(alias, values);

		internal void Append(string alias, string[] values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, List<string> values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, IEnumerable<string> values) => AppendPrivate(alias.AsSpan(), values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<string> values)
		{
			if (escribirSalida) formatter.Field(alias, values);
		}

		internal void Append(ReadOnlySpan<char> alias, DateTime[] values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, List<DateTime> values) => AppendPrivate(alias, values);
		internal void Append(ReadOnlySpan<char> alias, IEnumerable<DateTime> values) => AppendPrivate(alias, values);

		internal void Append(string alias, DateTime[] values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, List<DateTime> values) => AppendPrivate(alias.AsSpan(), values);
		internal void Append(string alias, IEnumerable<DateTime> values) => AppendPrivate(alias.AsSpan(), values);

		private void AppendPrivate(ReadOnlySpan<char> alias, IEnumerable<DateTime> values)
		{
			if (escribirSalida) formatter.Field(alias, values);
		}

		// ── Single-char Append (legacy public API) ─────────────────────────

		internal void Append(string alias, char text)
		{
			if (escribirSalida) formatter.Field(alias.AsSpan(), text);
		}

		// ── Raw splice (JSON-only escape hatch used by EvalStatement V1) ───
		//
		// Slice of a string into the sink. Used by EvalStatement.cs:46 to
		// strip the wrapping "{}" of a JSON sub-document and embed its
		// fields at the parent's cursor. Phase 4 will gate this off for
		// non-JSON formatters (V2 grammar already rejects eval-as-statement,
		// so by construction this is never reached under V2 + non-JSON
		// formatter pairing).

		internal void Append(string stream, int startIndex, int count)
		{
			if (escribirSalida) formatter.RawSplice(stream, startIndex, count);
		}

		// ── ToString — preserve legacy "{}" → "" collapse ──────────────────

		public override string ToString()
		{
			if (!escribirSalida) return "";
			var result = sink.ToString();
			if (formatter.CollapseEmptyToString && result == "{}") return "";
			return result;
		}
	}
}
