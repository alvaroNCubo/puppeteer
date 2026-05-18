using System;
using System.Diagnostics;
using System.Reflection;

namespace Puppeteer
{
	public class ActorV2 : Actor
	{
		public event Action<ActorExecutionError> ExecutionFailed;

		public ActorV2(string name) : base(name)
		{
		}

		public ActorV2(string name, params Assembly[] libraryAssemblies)
			: base(name, libraryAssemblies)
		{
		}

		public ActorV2(ActorV1 actor) : base(actor)
		{
		}

		internal void RaiseExecutionFailed(ActorExecutionError error)
		{
			ExecutionFailed?.Invoke(error);
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		public ActorV2Invocation Using(string scriptForChk, string scriptForCmd)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(scriptForChk);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(scriptForCmd);

			return new ActorV2Invocation(this, scriptForChk, scriptForCmd);
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		public ActorV2Invocation Using(string script)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(script);

			return new ActorV2Invocation(this, script);
		}

		public RentedParameter RentedParameters()
		{
			var parameters = Handler.ParametersPool.Rent();
			return new RentedParameter(this, parameters);
		}
	}

	public sealed class RentedParameter : IDisposable
	{
		internal readonly ActorV2 Actor;
		private bool _disposed = false;

		internal Parameters Parameters { get; }

		internal RentedParameter(ActorV2 actor, Parameters parameters)
		{
			Actor = actor ?? throw new ArgumentNullException(nameof(actor));
			Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
		}

		public Parameter this[string parameterName]
		{
			get
			{
				return Parameters[parameterName];
			}
		}

		public void Dispose()
		{
			if (!_disposed)
			{
				Actor.Handler.ParametersPool.Return(Parameters);
				_disposed = true;
			}
		}
	}



}
