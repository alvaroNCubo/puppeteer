using System;

namespace Puppeteer
{
	// Helper temporal expuesto al DSL de Reactions.Where como instance global 'time'.
	// Permite escribir time.Days(14), time.Hours(3), etc. que retornan TimeSpan
	// para comparaciones con (@Now - SeekName.@Now).
	public class Temporal
	{
		public TimeSpan Days(int n)
		{
			return TimeSpan.FromDays(n);
		}

		public TimeSpan Hours(int n)
		{
			return TimeSpan.FromHours(n);
		}

		public TimeSpan Minutes(int n)
		{
			return TimeSpan.FromMinutes(n);
		}

		public TimeSpan Seconds(int n)
		{
			return TimeSpan.FromSeconds(n);
		}

		public TimeSpan Weeks(int n)
		{
			return TimeSpan.FromDays(n * 7);
		}
	}
}
