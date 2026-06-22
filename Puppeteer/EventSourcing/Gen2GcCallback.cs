using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;

namespace Puppeteer.EventSourcing
{
	// Fires a callback on every Gen2 GC collection (memory pressure), via the BCL
	// re-registered-finalizer trick — the same pattern as System.Buffers.ArrayPool.
	// The BY-SHAPE pool uses it to invoke Trim() (idle decay) without a timer or a
	// wall clock: the decay happens exactly when there is memory pressure, and it
	// never touches the hot path (it runs on the finalizer thread).
	//
	// Holds a WEAK reference to the target: the callback does NOT prevent the target
	// (and therefore this callback) from being collected. When the target dies — e.g.
	// the ActorHandler that owns the pool — the callback stops re-registering and dies.
	internal sealed class Gen2GcCallback : CriticalFinalizerObject
	{
		private readonly Func<object, bool> _callback;
		private GCHandle _weakTarget;

		private Gen2GcCallback(Func<object, bool> callback, object target)
		{
			_callback = callback;
			_weakTarget = GCHandle.Alloc(target, GCHandleType.Weak);
		}

		// callback receives the target and returns true to keep re-registering.
		internal static void Register(Func<object, bool> callback, object target)
		{
			ArgumentNullException.ThrowIfNull(callback);
			ArgumentNullException.ThrowIfNull(target);
			new Gen2GcCallback(callback, target);
		}

		~Gen2GcCallback()
		{
			if (Environment.HasShutdownStarted)
			{
				_weakTarget.Free();
				return;
			}

			object target = _weakTarget.Target;
			if (target == null)
			{
				// The target was collected: let the callback die.
				_weakTarget.Free();
				return;
			}

			bool reRegister = true;
			try
			{
				reRegister = _callback(target);
			}
			finally
			{
				if (reRegister) GC.ReRegisterForFinalize(this);
				else _weakTarget.Free();
			}
		}
	}
}
