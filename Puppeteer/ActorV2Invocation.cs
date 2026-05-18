using System;
using System.Diagnostics;

namespace Puppeteer
{
	public readonly ref struct ActorV2Invocation
	{
		private readonly ActorV2 _actor;
		private readonly string _scriptForChk;
		private readonly string _script;
		private readonly Parameters _parameters;
		private readonly bool _parametersAutoRented;

		internal ActorV2Invocation(ActorV2 actor, string script)
		{
			_actor = actor;
			_scriptForChk = null;
			_script = script;
			_parameters = null;
			_parametersAutoRented = false;
		}

		internal ActorV2Invocation(ActorV2 actor, string scriptForChk, string script)
		{
			_actor = actor;
			_scriptForChk = scriptForChk;
			_script = script;
			_parameters = null;
			_parametersAutoRented = false;
		}

		private ActorV2Invocation(ActorV2 actor, string scriptForChk, string script,
			Parameters parameters, bool parametersAutoRented)
		{
			_actor = actor;
			_scriptForChk = scriptForChk;
			_script = script;
			_parameters = parameters;
			_parametersAutoRented = parametersAutoRented;
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		public ActorV2Invocation WithParameters(Action<Parameters> configure)
		{
			ArgumentNullException.ThrowIfNull(configure);

			var parameters = _actor.Handler.ParametersPool.Rent();
			configure(parameters);

			return new ActorV2Invocation(_actor, _scriptForChk, _script, parameters, parametersAutoRented: false);
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		public ActorV2Invocation WithParameters(RentedParameter rentedParameter, Action<Parameters> configure)
		{
			ArgumentNullException.ThrowIfNull(configure);
			if (rentedParameter.Actor != _actor) throw new ArgumentException("LeaseParameter does not belong to this actor.", nameof(rentedParameter));

			var parameters = rentedParameter.Parameters;
			configure(parameters);

			return new ActorV2Invocation(_actor, _scriptForChk, _script, parameters, parametersAutoRented: false);
		}

		public string PerformCheckThenCommand()
		{
			if (string.IsNullOrEmpty(_script)) throw new LanguageException($"{nameof(PerformCheckThenCommand)} must be used with a script to be executed.");
			if (string.IsNullOrEmpty(_scriptForChk)) throw new LanguageException($"{nameof(PerformCheckThenCommand)} should not be used with a check script that has a check condition. Use PerformCheckThenCommand instead.");

			var parameters = _parameters;
			var autoRented = _parametersAutoRented;
			if (parameters == null)
			{
				parameters = _actor.Handler.ParametersPool.Rent();
				autoRented = true;
			}

			try
			{
				return _actor.Handler.PerformCheckThenCmd(_scriptForChk, _script, parameters);
			}
			catch (Exception ex)
			{
				_actor.RaiseExecutionFailed(new ActorExecutionError(
					_actor.Name, _script, parameters, isQuery: false, DateTime.UtcNow, ex));
				throw;
			}
			finally
			{
				if (autoRented)
				{
					_actor.Handler.ParametersPool.Return(parameters);
				}
				else if (parameters != null)
				{
					_actor.Handler.ParametersPool.Return(parameters);
				}
			}
		}

		public string PerformCommand()
		{
			if (string.IsNullOrEmpty(_script)) throw new LanguageException($"{nameof(PerformCommand)} must be used with a script to be executed.");
			if (!string.IsNullOrEmpty(_scriptForChk)) throw new LanguageException("PerformQuery should not be used with a script that has a check condition. Use PerformCheckThenCommand instead.");

			var parameters = _parameters;
			var autoRented = _parametersAutoRented;
			if (parameters == null)
			{
				parameters = _actor.Handler.ParametersPool.Rent();
				autoRented = true;
			}

			try
			{
				return _actor.Handler.PerformCmd(_script, parameters);
			}
			catch (Exception ex)
			{
				_actor.RaiseExecutionFailed(new ActorExecutionError(
					_actor.Name, _script, parameters, isQuery: false, DateTime.UtcNow, ex));
				throw;
			}
			finally
			{
				if (autoRented)
				{
					_actor.Handler.ParametersPool.Return(parameters);
				}
				else if (parameters != null)
				{
					_actor.Handler.ParametersPool.Return(parameters);
				}
			}
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		public string PerformQuery()
		{
			if (string.IsNullOrEmpty(_script)) throw new LanguageException($"{nameof(PerformQuery)} must be used with a script to be executed.");
			if (!string.IsNullOrEmpty(_scriptForChk)) throw new LanguageException("PerformQuery should not be used with a script that has a check condition. Use PerformCheckThenCommand instead.");

			var parameters = _parameters;
			var autoRented = _parametersAutoRented;
			if (parameters == null)
			{
				parameters = _actor.Handler.ParametersPool.Rent();
				autoRented = true;
			}

			try
			{
				return _actor.Handler.PerformQry(_script, parameters);
			}
			catch (Exception ex)
			{
				_actor.RaiseExecutionFailed(new ActorExecutionError(
					_actor.Name, _script, parameters, isQuery: true, DateTime.UtcNow, ex));
				throw;
			}
			finally
			{
				if (autoRented)
				{
					_actor.Handler.ParametersPool.Return(parameters);
				}
				else if (parameters != null)
				{
					_actor.Handler.ParametersPool.Return(parameters);
				}
			}
		}
	}
}
