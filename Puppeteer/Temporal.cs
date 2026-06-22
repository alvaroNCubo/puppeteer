using System;

namespace Puppeteer
{
	// Temporal helper exposed to the Reactions.Where DSL as the global instance 'time'.
	// Allows writing time.Days(14), time.Hours(3), etc. that return TimeSpan
	// for comparisons with (@Now - SeekName.@Now).
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
