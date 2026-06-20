using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.Follower;
using System;
using System.Reflection;

namespace Puppeteer
{
	public enum CompilationModePolicy
	{
		Automatic,
		AlwaysCompiled,
		AlwaysInterpreted
	}

	public abstract class Actor
	{
		internal readonly ActorHandler Handler;

		public CompilationModePolicy CompiledModePolicy = CompilationModePolicy.Automatic;

		protected internal Actor(string name)
			: this(name, Array.Empty<Assembly>())
		{
		}

		protected internal Actor(string name, params Assembly[] libraryAssemblies)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			ArgumentNullException.ThrowIfNull(libraryAssemblies);

			Handler = new ActorHandler(this, name, libraryAssemblies);
		}

		protected internal Actor(Actor actor)
		{
			ArgumentNullException.ThrowIfNull(actor);
			if (actor.Handler == null) throw new LanguageException($"Actor {actor.Name} handler cannot be null.");

			Handler = actor.Handler;
		}

		public string Name => Handler.Name;

		public Reactions Reactions => Handler.Reactions;

		// Logger per-actor. Default es un ConsoleLogger util en desarrollo
		// (Error -> stderr, Debug -> stdout). El host inyecta su impl via
		// UseLogger(...); el sink vive en ActorHandler, no en un singleton
		// process-wide. Dos actores en el mismo proceso pueden tener loggers
		// distintos sin contaminarse. Choreography pasa este sink al transport
		// que construye en ConfigureTransport.
		public IPuppeteerLogger Logger => Handler.Logger;
		public void UseLogger(IPuppeteerLogger logger) => Handler.UseLogger(logger);

		// Paper 5 / Materialize v2 — Fase 0. Administra destinations para
		// transferencia de estado (Register / Deregister / List). El watermark
		// monotonico por destination (ConfirmoUntil) llega en Fase 1.
		public Materialization Materialization => Handler.Materialization;

		// Superficie read-only de inspeccion para uso CLI / IA / MCP / test-harness.
		// Separada del DSL del dominio por construccion: los verbos viven en una
		// interfaz que TODO actor expone por ser Puppeteer, no por ser Banco /
		// Tetris / etc. Read-only por contrato — escribir va por Perform / Tell,
		// nunca por aqui. Habilita el modo shadow del CLI IA-native: una IA que
		// solo tiene Introspection no puede mutar el journal del primary aunque
		// quiera. Etapa 1 (firmada 2026-05-31): solo ShowEntry.
		public Puppeteer.EventSourcing.IActorIntrospection Introspection => Handler;

		// Head actual del journal del actor (EntryId del ultimo registro escrito).
		// Expuesto publico para que hosts externos (puppeteer-cli, ShadowPerformance,
		// follower harness) sepan hasta donde sincronizar / mirrorear sin necesitar
		// InternalsVisibleTo. El equivalente en Shadow ya estaba publico (Shadow.CurrentEntryId).
		public long CurrentEntryId => Handler.CurrentEntryId;

		public Assembly[] LibraryAssemblies => Handler.LibraryAssemblies;

		internal ActorHandler GetHandler() => Handler;

		// Lab-only public path to the otherwise-internal storage configuration.
		// Mirrors what `StageHook.InitializeStorage` does for the Choreography host;
		// this overload exposes the same capability to lab tests without the
		// InternalsVisibleTo/signing plumbing of `Handler.EventSourcingStorage`.
		public void ConfigureStorage(DatabaseType dbType, string connectionString)
		{
			Handler.EventSourcingStorage(dbType, connectionString);
		}

		// Configure storage SIN rehidratacion. Path read-only para herramientas de
		// introspeccion (puppeteer-cli, IA-attach, MCP) que necesitan abrir un
		// journal cualquiera de disco sin cargar el dominio. Solo los verbos de
		// IActorIntrospection (ShowEntry y siguientes) son seguros despues de esta
		// llamada — Perform / Tell / Reactions fallaran porque el actor no fue
		// rehidratado.
		public void ConfigureStorageForIntrospection(DatabaseType dbType, string connectionString)
		{
			Handler.ConfigureStorageForIntrospection(dbType, connectionString);
		}

		public void GracefulExit()
		{
			Handler.GracefulExit();
		}

		// Distill: materializa fisicamente las elisiones del journal. Reemplaza al viejo
		// PerformTrim de Actor V1. Trim(DateTime) sigue existiendo aparte para preservacion
		// por fecha; Distill opera sobre la elision logica acumulada por reactions con
		// MarkAsSkip.
		//
		// Materialize v2 / Fase 1 (decision D1 #9): API fluida con builder DistillCommand.
		// El terminator obligatorio es .Now(). Patrones validos:
		//   actor.Distill().Now();                  — sin invariante (legacy).
		//   actor.Distill().Until(N).Now();         — invariante Materialize-then-Distill.
		//   actor.Distill().Forced().Now();         — escape hatch (D1 #7).
		// Sin destinations registradas via actor.Materialization.Register, .Now() ejecuta
		// como antes; con destinations, requiere .Until(N) (todas confirmaron >= N) o
		// .Forced(). Ver DistillCommand.cs para semantica completa.
		public DistillCommand Distill()
		{
			return new DistillCommand(Handler);
		}

		// Shadow Replay — S1 (handoff_shadow_S1_implementation.md / design §3.0).
		// Fachada del primitivo: produce un actor derivado-laboratorio aislado de
		// este actor de produccion. Lee el journal real (replay via SyncUntil) pero
		// escribe en su PROPIO storage y produce CERO efecto externo. El Shadow es un
		// primitivo del actor, NO un subtipo de Performance; un ShadowPerformance lo
		// hospeda por composicion para deployment.
		//
		// Las reactions del primary NO se clonan automaticamente (el builder es
		// imperativo). El caller re-declara las que quiera observar + experimentales
		// via cfg.ConfigureReactions, con la misma API de Tema A apuntando al shadow.
		public Shadow Shadow(ShadowConfig cfg)
		{
			ArgumentNullException.ThrowIfNull(cfg);
			ActorHandler shadowHandler = Handler.CreateShadow(cfg);
			return new Shadow(shadowHandler.ShadowActor, cfg);
		}
	}
}
