using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;

namespace Puppeteer.EventSourcing
{
	// Dispara un callback en cada coleccion de GC Gen2 (presion de memoria), via el
	// truco BCL de finalizador re-registrado — mismo patron que System.Buffers.ArrayPool.
	// Lo usa el pool POR FORMA para invocar Trim() (decaimiento del idle) sin un timer ni
	// reloj de pared: el decaimiento ocurre justo cuando hay presion de memoria, y nunca
	// toca el hot path (corre en el hilo finalizador).
	//
	// Mantiene una referencia DEBIL al objetivo: el callback NO impide que el objetivo
	// (y por ende este callback) sean recolectados. Cuando el objetivo muere — p.ej. el
	// ActorHandler dueño del pool —, el callback deja de re-registrarse y se extingue.
	internal sealed class Gen2GcCallback : CriticalFinalizerObject
	{
		private readonly Func<object, bool> _callback;
		private GCHandle _weakTarget;

		private Gen2GcCallback(Func<object, bool> callback, object target)
		{
			_callback = callback;
			_weakTarget = GCHandle.Alloc(target, GCHandleType.Weak);
		}

		// callback recibe el objetivo y retorna true para seguir re-registrandose.
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
				// El objetivo fue recolectado: dejar morir el callback.
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
