using System;

namespace Puppeteer
{
	public class ActorFactory
	{

		public static T Create<T>(string name) where T : Actor
		{
			// Use reflection to instantiate the requested type.
			return (T)Activator.CreateInstance(typeof(T), name)!;
		}
	}
}
