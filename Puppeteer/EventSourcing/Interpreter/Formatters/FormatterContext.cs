using System;
using System.Threading;

namespace Puppeteer.EventSourcing.Interpreter.Formatters
{
	/// <summary>
	/// Ambient active formatter context for the current execution flow. Set
	/// by Performance V2 / Stage V2 / Ensemble (in Choreography) before
	/// invoking script execution; read by the ExecutionOutput pool on Rent
	/// to install the matching formatter on the inner Outputs.
	///
	/// <para>
	/// Usage (idiomatic — scoped Push via using):
	/// <code>
	/// using (FormatterContext.Push(myFormatterPrototype))
	/// {
	///     return hook.PerformCmd(script);
	/// }
	/// </code>
	/// </para>
	///
	/// <para>
	/// Threading model: backed by <see cref="AsyncLocal{T}"/> so the context
	/// flows correctly across <c>Task.Run</c> and <c>await</c> boundaries.
	/// <c>StageV2.PerformCmd</c> wraps the call in <c>using
	/// FormatterContext.Push(...)</c> before <c>Task.Run</c>; the inner
	/// task sees the same active formatter because AsyncLocal value is
	/// captured by the ExecutionContext that <c>Task.Run</c> forwards.
	/// </para>
	///
	/// <para>
	/// Parallel <c>PerformQuery</c> on multiple threads is safe because
	/// each logical execution flow carries its own AsyncLocal value (each
	/// branch gets its own copy on capture).
	/// </para>
	///
	/// <para>
	/// When no context has been pushed (e.g. direct <c>StageHook</c> calls
	/// from <c>Stage</c> background work, or V1 paths), <see cref="Active"/>
	/// returns <c>null</c> and <c>ExecutionOutput</c> falls back to the
	/// default <c>JsonFormatter</c> — preserving legacy behavior.
	/// </para>
	///
	/// <para>
	/// Wire/remote forwarding limitation: when a Cast <c>StageV2</c>
	/// forwards a command to the Director over the transport, the
	/// formatter context does NOT cross the wire. The Director executes
	/// with its own formatter context (default JSON unless the Director's
	/// own Stage has been configured). Each Stage manages its own
	/// rendering format.
	/// </para>
	/// </summary>
	public static class FormatterContext
	{
		// AsyncLocal (NOT ThreadStatic): the formatter context must flow
		// across Task.Run / await boundaries because StageV2 dispatches
		// hook.PerformCmd via Task.Run. With [ThreadStatic] the value
		// would not propagate to the worker thread.
		private static readonly AsyncLocal<IOutputFormatter> _active = new AsyncLocal<IOutputFormatter>();

		/// <summary>
		/// The currently-active formatter prototype for this logical
		/// execution flow, or <c>null</c> if no context has been pushed.
		/// </summary>
		public static IOutputFormatter Active => _active.Value;

		/// <summary>
		/// Push a formatter prototype as the active context for this
		/// logical execution flow. Returns an <see cref="IDisposable"/>
		/// that restores the prior context when disposed. Always use
		/// within a <c>using</c> block.
		/// </summary>
		/// <param name="prototype">The formatter prototype to make active.
		/// May be <c>null</c> to explicitly clear any outer context
		/// (forces fallback to default JsonFormatter at the ExecutionOutput
		/// Rent point).</param>
		public static IDisposable Push(IOutputFormatter prototype)
		{
			var prev = _active.Value;
			_active.Value = prototype;
			return new Restorer(prev);
		}

		private sealed class Restorer : IDisposable
		{
			private readonly IOutputFormatter prev;
			private bool disposed;

			public Restorer(IOutputFormatter prev)
			{
				this.prev = prev;
			}

			public void Dispose()
			{
				if (disposed) return;
				_active.Value = prev;
				disposed = true;
			}
		}
	}
}
