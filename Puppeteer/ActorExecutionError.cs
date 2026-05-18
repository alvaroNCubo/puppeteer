using System;

namespace Puppeteer
{
	public sealed class ActorExecutionError
	{
		public string ActorName { get; }
		public string Script { get; }
		public Parameters Parameters { get; }
		public bool IsQuery { get; }
		public DateTime Timestamp { get; }
		public Exception Exception { get; }

		public ActorExecutionError(string actorName, string script,
			Parameters parameters, bool isQuery, DateTime timestamp, Exception exception)
		{
			ArgumentNullException.ThrowIfNull(actorName);
			ArgumentNullException.ThrowIfNull(script);
			ArgumentNullException.ThrowIfNull(parameters);
			ArgumentNullException.ThrowIfNull(exception);

			ActorName = actorName;
			Script = script;
			Parameters = parameters;
			IsQuery = isQuery;
			Timestamp = timestamp;
			Exception = exception;
		}
	}
}
