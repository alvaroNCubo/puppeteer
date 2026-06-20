using System;

namespace Puppeteer
{
	public interface IPuppeteerLogger
	{
		void Error(string message, Exception exception);
		void Debug(string message);
	}
}
