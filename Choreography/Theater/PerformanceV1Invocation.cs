using System;
using Puppeteer;
using Puppeteer.EventSourcing.Playbill;

namespace Choreography.Theater
{
	// Fase 4.5 backward compat: equivalente V1 de ActorV2Invocation, pero
	// preservando la semantica V1 de ip/user como domain globals concatenados
	// al script (no como system parameters en una symbol table). El Playbill
	// se agrega como capa de audit ortogonal — el dev decide si los ip/user
	// que usa para el script Y los valores que pone en .WithPlaybill son los
	// mismos o no.
	//
	// Nota: V1 no tiene un Parameters fluent (solo string ip/user). Para reusar
	// la maquinaria de validacion del Playbill, .WithPlaybill recibe un
	// configure<Parameters> identico al de V2 — la unica diferencia entre las
	// dos APIs es que V1.Using exige ip/user explicitos (necesarios para
	// concatenar al script) mientras V2.Using los toma del symbol table.
	public readonly ref struct PerformanceV1Invocation
	{
		private readonly PerformanceV1 _performance;
		private readonly string _script;
		private readonly string _ip;
		private readonly string _user;

		private readonly Playbill _playbill;
		private readonly string _playbillSchemaName;
		private readonly Parameters _playbillValues;

		internal PerformanceV1Invocation(PerformanceV1 performance, string script, string ip, string user,
			Playbill playbill, string playbillSchemaName)
		{
			_performance = performance;
			_script = script;
			_ip = ip;
			_user = user;
			_playbill = playbill;
			_playbillSchemaName = playbillSchemaName;
			_playbillValues = null;
		}

		private PerformanceV1Invocation(PerformanceV1 performance, string script, string ip, string user,
			Playbill playbill, string playbillSchemaName, Parameters playbillValues)
		{
			_performance = performance;
			_script = script;
			_ip = ip;
			_user = user;
			_playbill = playbill;
			_playbillSchemaName = playbillSchemaName;
			_playbillValues = playbillValues;
		}

		public PerformanceV1Invocation WithPlaybill(Action<Parameters> configure)
		{
			if (configure == null) throw new ArgumentNullException(nameof(configure));
			if (_playbill == null) throw new LanguageException("WithPlaybill called but PerformanceV1 has no Playbill schema. Declare one via Performance.Playbill(...) first.");
			if (_playbillSchemaName == null) throw new LanguageException("WithPlaybill called but no schema is currently selected on the Performance.");

			string declarations = _playbill.GetSchemaDeclarations(_playbillSchemaName);
			if (declarations == null) throw new LanguageException($"Playbill schema '{_playbillSchemaName}' is not registered.");

			string parseableDeclarations = declarations.Replace("?:", ":");
			var playbillValues = new Parameters(parseableDeclarations);
			PreInitializeDefaults(playbillValues);
			configure(playbillValues);

			return new PerformanceV1Invocation(_performance, _script, _ip, _user, _playbill, _playbillSchemaName, playbillValues);
		}

		public string PerformCommand()
		{
			string output = _performance.PerformCmdInternal(_script, _ip, _user);
			WritePlaybillIfNeeded();
			return output;
		}

		private void WritePlaybillIfNeeded()
		{
			if (_playbill == null) return;
			if (_playbillValues == null) return;

			long entryId = _performance.CurrentEntryId;
			_playbill.WriteRecord(entryId, _playbillSchemaName, _playbillValues);
		}

		private static void PreInitializeDefaults(Parameters values)
		{
			foreach (var p in values)
			{
				// Playbill final refactor: ya no hay SystemParameter (incluido Now) — todo es user.
				var t = p.ParameterType;
				if (t == typeof(string)) values[p.Name, typeof(string)] = string.Empty;
				else if (t == typeof(int)) values[p.Name, typeof(int)] = 0;
				else if (t == typeof(bool)) values[p.Name, typeof(bool)] = false;
				else if (t == typeof(decimal)) values[p.Name, typeof(decimal)] = 0m;
				else if (t == typeof(double)) values[p.Name, typeof(double)] = 0.0;
				else if (t == typeof(DateTime)) values[p.Name, typeof(DateTime)] = DateTime.MinValue;
			}
		}
	}
}
