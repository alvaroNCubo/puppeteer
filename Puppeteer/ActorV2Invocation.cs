using System;
using System.Diagnostics;
using Puppeteer.EventSourcing.Playbill;

namespace Puppeteer
{
	public readonly ref struct ActorV2Invocation
	{
		private readonly ActorV2 _actor;
		private readonly string _scriptForChk;
		private readonly string _script;
		private readonly Parameters _parameters;
		private readonly bool _parametersAutoRented;
		// True when _parameters comes from a Rent BY SHAPE (key = _script):
		// WithParameters(configure) or the auto-rent of the Perform*. False for the
		// external RentedParameter lease, which is rented/returned keyless. Governs
		// whether the Return goes to the keyed pool or the keyless one.
		private readonly bool _parametersKeyed;

		// Playbill context — null when the invocation has no playbill attached
		// (typical for perf.Actor.Using(...) legacy path or for Performances that
		// never called .Playbill(...)). Constructed via Performance.Using(...) for
		// the Playbill-aware path.
		private readonly Playbill _playbill;
		private readonly string _playbillSchemaName;
		private readonly Parameters _playbillValues;

		internal ActorV2Invocation(ActorV2 actor, string script)
		{
			_actor = actor;
			_scriptForChk = null;
			_script = script;
			_parameters = null;
			_parametersAutoRented = false;
			_parametersKeyed = false;
			_playbill = null;
			_playbillSchemaName = null;
			_playbillValues = null;
		}

		internal ActorV2Invocation(ActorV2 actor, string scriptForChk, string script)
		{
			_actor = actor;
			_scriptForChk = scriptForChk;
			_script = script;
			_parameters = null;
			_parametersAutoRented = false;
			_parametersKeyed = false;
			_playbill = null;
			_playbillSchemaName = null;
			_playbillValues = null;
		}

		// Playbill-aware constructors — used by Performance.Using(...) to attach
		// the Playbill context. The Performance is responsible for ensuring the
		// schemaName is registered in the Playbill before construction.
		internal ActorV2Invocation(ActorV2 actor, string script, Playbill playbill, string playbillSchemaName)
		{
			_actor = actor;
			_scriptForChk = null;
			_script = script;
			_parameters = null;
			_parametersAutoRented = false;
			_parametersKeyed = false;
			_playbill = playbill;
			_playbillSchemaName = playbillSchemaName;
			_playbillValues = null;
		}

		internal ActorV2Invocation(ActorV2 actor, string scriptForChk, string script, Playbill playbill, string playbillSchemaName)
		{
			_actor = actor;
			_scriptForChk = scriptForChk;
			_script = script;
			_parameters = null;
			_parametersAutoRented = false;
			_parametersKeyed = false;
			_playbill = playbill;
			_playbillSchemaName = playbillSchemaName;
			_playbillValues = null;
		}

		private ActorV2Invocation(ActorV2 actor, string scriptForChk, string script,
			Parameters parameters, bool parametersAutoRented, bool parametersKeyed,
			Playbill playbill, string playbillSchemaName, Parameters playbillValues)
		{
			_actor = actor;
			_scriptForChk = scriptForChk;
			_script = script;
			_parameters = parameters;
			_parametersAutoRented = parametersAutoRented;
			_parametersKeyed = parametersKeyed;
			_playbill = playbill;
			_playbillSchemaName = playbillSchemaName;
			_playbillValues = playbillValues;
		}

#if PUPPETEER_HIDE_INTERNALS
		[DebuggerHidden]
#endif
		public ActorV2Invocation WithParameters(Action<Parameters> configure)
		{
			ArgumentNullException.ThrowIfNull(configure);

			// Rent BY SHAPE with key = _script: the parameter shape is invariant
			// per operation, so the rented instance keeps its slots and configure
			// only overwrites values.
			var parameters = _actor.Handler.ParametersPool.Rent(_script);
			configure(parameters);

			return new ActorV2Invocation(_actor, _scriptForChk, _script, parameters, parametersAutoRented: false, parametersKeyed: true,
				_playbill, _playbillSchemaName, _playbillValues);
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

			// External lease: rented/returned keyless (RentedParameter has no script
			// in its Rent), hence parametersKeyed: false.
			return new ActorV2Invocation(_actor, _scriptForChk, _script, parameters, parametersAutoRented: false, parametersKeyed: false,
				_playbill, _playbillSchemaName, _playbillValues);
		}

		// Playbill values setter. Only valid when this Invocation was constructed
		// via Performance.Using(...) with a playbill attached. Builds a fresh
		// Parameters instance from the schema's canonical declarations text and
		// hands it to the dev to configure via the V2 indexer pattern.
		public ActorV2Invocation WithPlaybill(Action<Parameters> configure)
		{
			ArgumentNullException.ThrowIfNull(configure);
			if (_playbill == null) throw new LanguageException("WithPlaybill called but Performance has no Playbill schema. Declare one via Performance.Playbill(...) first.");
			if (_playbillSchemaName == null) throw new LanguageException("WithPlaybill called but no schema is currently selected on the Performance.");

			string declarations = _playbill.GetSchemaDeclarations(_playbillSchemaName);
			if (declarations == null) throw new LanguageException($"Playbill schema '{_playbillSchemaName}' is not registered.");

			// Strip '?' from optional field names before passing to Parameters parser.
			// The '?' is a Playbill marker and lives only in the canonical declarations
			// text — V2's Parameters parser treats it as an unknown character.
			string parseableDeclarations = declarations.Replace("?:", ":");
			var playbillValues = new Parameters(parseableDeclarations);

			// Pre-initialize every declared field with its type default. The shared
			// Parameters serializer rejects null Values, so any optional field the
			// dev does NOT set explicitly inside `configure` must arrive at
			// SerializeForTransport with a non-null value. The dev's configure
			// callback then overwrites whatever fields it cares about; the rest
			// stays at type default and round-trips cleanly through the wire.
			PreInitializeDefaults(playbillValues);

			configure(playbillValues);

			return new ActorV2Invocation(_actor, _scriptForChk, _script, _parameters, _parametersAutoRented, _parametersKeyed,
				_playbill, _playbillSchemaName, playbillValues);
		}

		public string PerformCheckThenCommand()
		{
			if (string.IsNullOrEmpty(_script)) throw new LanguageException($"{nameof(PerformCheckThenCommand)} must be used with a script to be executed.");
			if (string.IsNullOrEmpty(_scriptForChk)) throw new LanguageException($"{nameof(PerformCheckThenCommand)} should not be used with a check script that has a check condition. Use PerformCheckThenCommand instead.");

			var parameters = _parameters;
			var autoRented = _parametersAutoRented;
			if (parameters == null)
			{
				parameters = _actor.Handler.ParametersPool.Rent(_script);
				autoRented = true;
			}

			try
			{
				string output = _actor.Handler.PerformCheckThenCmd(_scriptForChk, _script, parameters);
				WritePlaybillIfNeeded();
				return output;
			}
			catch (Exception ex)
			{
				_actor.RaiseExecutionFailed(new ActorExecutionError(
					_actor.Name, _script, parameters, isQuery: false, DateTime.UtcNow, ex));
				throw;
			}
			finally
			{
				if (parameters != null)
				{
					// Keyed when the instance came from a Rent BY SHAPE (auto-rent or
					// WithParameters(configure)); keyless for the external
					// RentedParameter lease. The key is _script (shape invariant per operation).
					if (autoRented || _parametersKeyed)
					{
						_actor.Handler.ParametersPool.Return(_script, parameters);
					}
					else
					{
						_actor.Handler.ParametersPool.Return(parameters);
					}
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
				parameters = _actor.Handler.ParametersPool.Rent(_script);
				autoRented = true;
			}

			try
			{
				string output = _actor.Handler.PerformCmd(_script, parameters);
				WritePlaybillIfNeeded();
				return output;
			}
			catch (Exception ex)
			{
				_actor.RaiseExecutionFailed(new ActorExecutionError(
					_actor.Name, _script, parameters, isQuery: false, DateTime.UtcNow, ex));
				throw;
			}
			finally
			{
				if (parameters != null)
				{
					// Keyed when the instance came from a Rent BY SHAPE (auto-rent or
					// WithParameters(configure)); keyless for the external
					// RentedParameter lease. The key is _script (shape invariant per operation).
					if (autoRented || _parametersKeyed)
					{
						_actor.Handler.ParametersPool.Return(_script, parameters);
					}
					else
					{
						_actor.Handler.ParametersPool.Return(parameters);
					}
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
				parameters = _actor.Handler.ParametersPool.Rent(_script);
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
				if (parameters != null)
				{
					// Keyed when the instance came from a Rent BY SHAPE (auto-rent or
					// WithParameters(configure)); keyless for the external
					// RentedParameter lease. The key is _script (shape invariant per operation).
					if (autoRented || _parametersKeyed)
					{
						_actor.Handler.ParametersPool.Return(_script, parameters);
					}
					else
					{
						_actor.Handler.ParametersPool.Return(parameters);
					}
				}
			}
		}

		// Second-write: after the actor's journal write succeeds, persist the
		// playbill record using the EntryId the handler just allocated. Let-it-fail
		// policy (signed in project_playbill_design.md): if this write throws,
		// the journal entry remains and the exception propagates to the caller —
		// gap detection via LEFT JOIN forensic query.
		private void WritePlaybillIfNeeded()
		{
			if (_playbill == null) return;
			if (_playbillValues == null) return; // dev did not chain WithPlaybill — permissive policy

			long entryId = _actor.Handler.EntryId;
			_playbill.WriteRecord(entryId, _playbillSchemaName, _playbillValues);
		}

		private static void PreInitializeDefaults(Parameters values)
		{
			foreach (var p in values)
			{
				// Playbill final refactor: there is no longer a SystemParameter (including Now) — everything is user.
				var t = p.ParameterType;
				if (t == typeof(string)) values[p.Name, typeof(string)] = string.Empty;
				else if (t == typeof(int)) values[p.Name, typeof(int)] = 0;
				else if (t == typeof(long)) values[p.Name, typeof(long)] = 0L;
				else if (t == typeof(bool)) values[p.Name, typeof(bool)] = false;
				else if (t == typeof(decimal)) values[p.Name, typeof(decimal)] = 0m;
				else if (t == typeof(double)) values[p.Name, typeof(double)] = 0.0;
				else if (t == typeof(DateTime)) values[p.Name, typeof(DateTime)] = DateTime.MinValue;
			}
		}
	}
}
