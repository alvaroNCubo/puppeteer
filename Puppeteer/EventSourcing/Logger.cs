using log4net;
using System;
using System.Threading;

namespace Puppeteer.EventSourcing
{
	internal sealed class Loggers
	{
		public static Loggers loggers = new Loggers();

		private Loggers()
		{
			Db = new Logger();
		}
		internal Logger Db { get; set; }

		internal static Loggers GetIntance()
		{
			return loggers;
		}
	}

	internal sealed class Logger
	{
		private ILog logger = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

		internal Logger()
		{
		}

		internal void Error(string message, Exception e)
		{
			ThreadPool.QueueUserWorkItem(task => logger.Error(message, e));
		}

		internal void Debug(string message)
		{
			ThreadPool.QueueUserWorkItem(task => logger.Debug(message));
		}

		internal void SetLogger(ILog log)
		{
			this.logger = log;
		}
	}
}
