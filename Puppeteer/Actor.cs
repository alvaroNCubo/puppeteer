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

		// Paper 5 / Materialize v2 — Fase 0. Administra destinations para
		// transferencia de estado (Register / Deregister / List). El watermark
		// monotonico por destination (ConfirmoUntil) llega en Fase 1.
		public Materialization Materialization => Handler.Materialization;

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
	}
}
