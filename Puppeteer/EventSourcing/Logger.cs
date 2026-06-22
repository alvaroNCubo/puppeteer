using System;
using System.Threading;

namespace Puppeteer.EventSourcing
{
	// ConsoleLogger is the per-actor default: ActorHandler constructs it in the
	// field initializer of its `logger`. Useful in development (Error -> stderr,
	// Debug -> stdout via ThreadPool so it does not block). The host injects its
	// own impl with actor.UseLogger(...) when it needs Serilog, MEL, NLog, etc.
	// The process-wide singleton (the old Loggers class) was removed in F5 of
	// the logger refactor.
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
