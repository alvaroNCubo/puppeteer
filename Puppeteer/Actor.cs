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

		// Per-actor logger. Default is a ConsoleLogger useful in development
		// (Error -> stderr, Debug -> stdout). The host injects its impl via
		// UseLogger(...); the sink lives in ActorHandler, not in a process-wide
		// singleton. Two actors in the same process can have different loggers
		// without contaminating each other. Choreography passes this sink to the
		// transport it builds in ConfigureTransport.
		public IPuppeteerLogger Logger => Handler.Logger;
		public void UseLogger(IPuppeteerLogger logger) => Handler.UseLogger(logger);

		// Paper 5 / Materialize v2 — Phase 0. Manages destinations for state
		// transfer (Register / Deregister / List). The monotonic per-destination
		// watermark (ConfirmoUntil) arrives in Phase 1.
		public Materialization Materialization => Handler.Materialization;

		// Journal-outbox emit — delivery side. Drives at-least-once delivery of
		// messages recorded by `.Outbox.Emit(...)` Reactions to an IOutboxSink.
		// See notes/reactions-outbox-emit.md.
		public OutboxRelay Outbox => Handler.OutboxRelay;

		// Read-only inspection surface for CLI / AI / MCP / test-harness use.
		// Separated from the domain DSL by construction: the verbs live in an
		// interface that EVERY actor exposes by virtue of being Puppeteer, not by
		// virtue of being a specific domain type. Read-only by contract — writing
		// goes through Perform / Tell, never here. Enables the AI-native CLI shadow
		// mode: an AI that has only Introspection cannot mutate the primary's
		// journal even if it wants to. Stage 1 (signed 2026-05-31): only ShowEntry.
		public Puppeteer.EventSourcing.IActorIntrospection Introspection => Handler;

		// The actor journal's current head (EntryId of the last written record).
		// Exposed publicly so external hosts (puppeteer-cli, ShadowPerformance,
		// follower harness) know how far to sync / mirror without needing
		// InternalsVisibleTo. The equivalent on Shadow was already public (Shadow.CurrentEntryId).
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

		// Configure storage WITHOUT rehydration. Read-only path for introspection
		// tools (puppeteer-cli, AI-attach, MCP) that need to open an arbitrary
		// journal from disk without loading the domain. Only the IActorIntrospection
		// verbs (ShowEntry and following) are safe after this call — Perform / Tell /
		// Reactions will fail because the actor was not rehydrated.
		public void ConfigureStorageForIntrospection(DatabaseType dbType, string connectionString)
		{
			Handler.ConfigureStorageForIntrospection(dbType, connectionString);
		}

		public void GracefulExit()
		{
			Handler.GracefulExit();
		}

		// Distill: physically materializes the journal's elisions. Replaces the old
		// PerformTrim of Actor V1. Trim(DateTime) still exists separately for date-based
		// preservation; Distill operates on the logical elision accumulated by reactions
		// with MarkAsSkip.
		//
		// Materialize v2 / Phase 1 (decision D1 #9): fluent API with the DistillCommand
		// builder. The mandatory terminator is .Now(). Valid patterns:
		//   actor.Distill().Now();                  — without invariant (legacy).
		//   actor.Distill().Until(N).Now();         — Materialize-then-Distill invariant.
		//   actor.Distill().Forced().Now();         — escape hatch (D1 #7).
		// Without destinations registered via actor.Materialization.Register, .Now() runs
		// as before; with destinations, it requires .Until(N) (all confirmed >= N) or
		// .Forced(). See DistillCommand.cs for the complete semantics.
		public DistillCommand Distill()
		{
			return new DistillCommand(Handler);
		}

		// Shadow Replay — S1 (handoff_shadow_S1_implementation.md / design §3.0).
		// Facade of the primitive: produces a derived-laboratory actor isolated from
		// this production actor. It reads the real journal (replay via SyncUntil) but
		// writes to its OWN storage and produces ZERO external effect. The Shadow is a
		// primitive of the actor, NOT a subtype of Performance; a ShadowPerformance
		// hosts it by composition for deployment.
		//
		// The primary's reactions are NOT cloned automatically (the builder is
		// imperative). The caller re-declares the ones it wants to observe + experimental
		// ones via cfg.ConfigureReactions, with the same Theme A API pointing at the shadow.
		public Shadow Shadow(ShadowConfig cfg)
		{
			ArgumentNullException.ThrowIfNull(cfg);
			ActorHandler shadowHandler = Handler.CreateShadow(cfg);
			return new Shadow(shadowHandler.ShadowActor, cfg);
		}
	}
}
