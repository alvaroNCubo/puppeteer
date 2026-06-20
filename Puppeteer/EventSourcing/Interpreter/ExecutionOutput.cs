using Puppeteer.EventSourcing.Interpreter.Formatters;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Puppeteer.EventSourcing.Interpreter
{
	internal class ExecutionOutput
	{
		private readonly Output printBuffer;
		private readonly Output exposeBuffer;
		private readonly bool escribirSalida;
		private readonly bool isRehydrating;

		private ExecutionOutput(bool conSalida, bool isRehydrating)
		{
			escribirSalida = conSalida;
			this.isRehydrating = isRehydrating;
			printBuffer = conSalida ? Output.RentWithOutput() : Output.RentWithoutOutput();
			exposeBuffer = conSalida ? Output.RentWithOutput() : Output.RentWithoutOutput();
		}

		internal Output PrintBuffer => printBuffer;
		internal Output ExposeBuffer => exposeBuffer;

		internal bool IsWriting => escribirSalida;
		internal bool IsRehydrating => isRehydrating;

		internal void Clear()
		{
			printBuffer.Clear();
			exposeBuffer.Clear();
		}

		/// <summary>
		/// Install the formatter on both inner Outputs. Called by the
		/// pool's Rent path; propagates the active <see cref="FormatterContext"/>
		/// down to byte-emission level.
		/// </summary>
		internal void InstallFormatter(IOutputFormatter prototype)
		{
			printBuffer.InstallFormatter(prototype);
			exposeBuffer.InstallFormatter(prototype);
		}

		internal void Finish()
		{
			printBuffer.Finish();
			exposeBuffer.Finish();
		}

		internal string GetPrintJson() => printBuffer.ToString();
		internal string GetExposeJson() => exposeBuffer.ToString();

		internal bool HasEWIS() => printBuffer.HasEWIS();

		public override string ToString() => GetPrintJson();

		internal void OpenFor()
		{
			printBuffer.OpenFor();
			exposeBuffer.OpenFor();
		}

		internal void BeginForMoveNext()
		{
			printBuffer.BeginForMoveNext();
			exposeBuffer.BeginForMoveNext();
		}

		internal void EndForMoveNext()
		{
			printBuffer.EndForMoveNext();
			exposeBuffer.EndForMoveNext();
		}

		internal void CloseFor(string variableName)
		{
			printBuffer.CloseFor(variableName);
			exposeBuffer.CloseFor(variableName);
		}

		// Per-thread pool. The previous shared ConcurrentStack<ExecutionOutput>
		// design had a fatal double-return bug compounded by cross-thread
		// sharing:
		//   * Return() pushed the ExecutionOutput to one pool while ALSO
		//     pushing its inner printBuffer/exposeBuffer to the OutputPool
		//     (Output.Return) — so the same Output instance was tracked by
		//     two pools at once. A later Rent from either pool could hand
		//     out an Output already aliased to a still-live ExecutionOutput,
		//     producing concurrent StringBuilder mutation from two callers.
		// The fix is two-fold: keep the inner Outputs as the private property
		// of their owning ExecutionOutput (no separate Output.Return), and
		// thread-local the pool so renters never inherit state another thread
		// is still using.
		private class ExecutionOutputPool
		{
			private readonly ThreadLocal<Stack<ExecutionOutput>> _objects = new ThreadLocal<Stack<ExecutionOutput>>(() => new Stack<ExecutionOutput>());
			private readonly bool _conSalida;
			private readonly int _maxPoolSize;

			internal ExecutionOutputPool(bool conSalida, int maxPoolSize = ActorHandler.MAX_NORMAL_LOAD_POOL_SIZE)
			{
				if (maxPoolSize <= 0) throw new LanguageException($"{nameof(ExecutionOutputPool)} maxPoolSize must be greater than 0.");
				_conSalida = conSalida;
				_maxPoolSize = maxPoolSize;
			}

			internal ExecutionOutput Rent(bool isRehydrating)
			{
				var stack = _objects.Value;
				ExecutionOutput item = stack.Count > 0
					? stack.Pop()
					: new ExecutionOutput(conSalida: _conSalida, isRehydrating: isRehydrating);
				// Install the active formatter (if any) on the inner Outputs
				// before handing the ExecutionOutput out. FormatterContext.Active
				// is null when no Push has happened on this thread → defaults
				// to JsonFormatter inside InstallFormatter.
				item.InstallFormatter(FormatterContext.Active);
				return item;
			}

			internal void Return(ExecutionOutput item)
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

		private static readonly ExecutionOutputPool _conSalidaPool = new ExecutionOutputPool(conSalida: true);
		private static readonly ExecutionOutputPool _sinSalidaPool = new ExecutionOutputPool(conSalida: false);

		internal static ExecutionOutput RentWithOutput()
		{
			return _conSalidaPool.Rent(isRehydrating: false);
		}

		internal static ExecutionOutput RentWithoutOutput()
		{
			return _sinSalidaPool.Rent(isRehydrating: true);
		}

		internal static void Return(ExecutionOutput rentedOutput)
		{
			ArgumentNullException.ThrowIfNull(rentedOutput);

			// NOTE: do NOT call Output.Return(rentedOutput.printBuffer) here.
			// The inner Outputs are property-of the ExecutionOutput; they
			// stay attached for its whole lifecycle. Clear() (invoked inside
			// the pool's Return) resets both. Returning them to OutputPool
			// in addition created a double-ownership window that races under
			// parallel PerformQuery.

			if (rentedOutput.escribirSalida)
			{
				_conSalidaPool.Return(rentedOutput);
			}
			else
			{
				_sinSalidaPool.Return(rentedOutput);
			}
		}
	}
}
