using System;
using System.Threading;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// Ambient render scope that tells OutputStatementIndividual.Write to keep
	// print statements in the rendered text instead of eliding them as filtered
	// output. Off by default: the canonical render (Program.ConvertToString, the
	// action cache key, and repeated Script rows) stays print-free so identity
	// and journal density are unchanged. Program.ConvertToAuthoredString turns it
	// on for the single, once-written Action (Define) body, so a developer's
	// prints survive in the journal for readability without bloating the log.
	//
	// Backed by AsyncLocal to match FormatterContext and flow across await
	// boundaries, although the authored render itself is synchronous.
	internal static class AuthoredRenderScope
	{
		private static readonly AsyncLocal<bool> _active = new AsyncLocal<bool>();

		internal static bool Active => _active.Value;

		internal static IDisposable Enter()
		{
			bool prev = _active.Value;
			_active.Value = true;
			return new Restorer(prev);
		}

		private sealed class Restorer : IDisposable
		{
			private readonly bool prev;
			private bool disposed;

			internal Restorer(bool prev)
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
