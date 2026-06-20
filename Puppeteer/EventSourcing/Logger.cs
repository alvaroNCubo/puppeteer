using System;
using System.Threading;

namespace Puppeteer.EventSourcing
{
	// ConsoleLogger es el default per-actor: ActorHandler lo construye en el
	// field initializer de su `logger`. Util en desarrollo (Error -> stderr,
	// Debug -> stdout via ThreadPool para no bloquear). El host inyecta su
	// impl con actor.UseLogger(...) cuando necesita Serilog, MEL, NLog, etc.
	// El singleton process-wide (la vieja class Loggers) fue retirado en F5
	// del refactor de logger.
	public sealed class ConsoleLogger : IPuppeteerLogger
	{
		public void Error(string message, Exception exception)
		{
			if (message == null) throw new ArgumentNullException(nameof(message));
			if (exception == null) throw new ArgumentNullException(nameof(exception));
			ThreadPool.QueueUserWorkItem(_ =>
			{
				Console.Error.WriteLine($"[Puppeteer ERROR] {message}");
				Console.Error.WriteLine(exception);
			});
		}

		public void Debug(string message)
		{
			if (message == null) throw new ArgumentNullException(nameof(message));
			ThreadPool.QueueUserWorkItem(_ => Console.Out.WriteLine($"[Puppeteer DEBUG] {message}"));
		}
	}
}
