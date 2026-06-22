using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Shadow Replay — S1. In-process result of the actor.Shadow(cfg) primitive:
	// a derived-laboratory actor isolated from the primary. It reads the primary's
	// real journal by replay (SyncUntil) but writes to its OWN storage and produces
	// ZERO external effect (Tells neutralized; not registered as a destination of the
	// primary's Materialization).
	//
	// Ontology (design §3.0): the Shadow is a PRIMITIVE of the actor, NOT a subtype of
	// Performance. For deployment (K8s pod with HTTP console, S6) a ShadowPerformance
	// (Choreography) HOSTS it by composition. This class is the in-process face —
	// quick experiments, agent loops, point-in-time bug-repro.
	//
	// TTL kill-all: Dispose() tears down the shadow (graceful shutdown of reactions +
	// cleanup of the storage for the backends that support it). The TTL via K8s Job is
	// S6, not here.
	public sealed class Shadow : IDisposable
	{
		private readonly Actor shadowActor;
		private readonly ActorHandler shadowHandler;
		private readonly ShadowConfig config;
		private bool disposed;
		private int diffCounter;

		internal Shadow(Actor shadowActor, ShadowConfig config)
		{
			ArgumentNullException.ThrowIfNull(shadowActor);
			ArgumentNullException.ThrowIfNull(config);
			this.shadowActor = shadowActor;
			this.shadowHandler = shadowActor.Handler;
			this.config = config;
		}

		// The shadow actor (V1/V2 family same as the primary). Exposed to register
		// experimental reactions (shadow.Actor.Reactions.DefineReaction(...)) and, in
		// V1, to drive it with PerformCmdAsync.
		public Actor Actor => shadowActor;

		// Shadow reactions — same Theme A API pointing at the shadow.
		public Reactions Reactions => shadowActor.Reactions;

		// The shadow's current EntryId (its own timeline, divergent after fork).
		public long CurrentEntryId => shadowHandler.CurrentEntryId;

		public ShadowMode Mode => config.Mode;
		public TimeSpan? Ttl => config.Ttl;

		// SyncUntil(toEntryId): replay of the primary's journal from GENESIS up to
		// toEntryId inclusive, applied to the shadow's own storage. A ceiling, not a
		// floor. After this the shadow is forked and accepts local commands.
		public void SyncUntil(long toEntryId)
		{
			ThrowIfDisposed();
			shadowHandler.SyncUntil(toEntryId);
		}

		// Shadow Replay — S3. Enables skip-preview (dry-run of Elide): the shadow's Elide
		// reactions capture in Reaction.WouldSkip the EntryIds they would mark, WITHOUT
		// committing the elision to any journal. For inspecting "what would be elided"
		// over real replicated data, with no effect.
		public void EnableSkipPreview()
		{
			ThrowIfDisposed();
			shadowHandler.EnableSkipPreview();
		}

		// Shadow Replay — S4. Elision-impact diff (the "killer move"): for a candidate
		// set of EntryIds to elide, compares the observable outputs (queries) between
		// rehydrating the journal WITHOUT elision and WITH the elision. An empty diff
		// (result.IsSafe) => eliding that set changes no observation = safe with respect
		// to the provided observers. Builds two isolated twins (shadow-of-shadow of THIS
		// shadow's journal) and discards them at the end. Turns "is it safe to elide
		// this?" from a static domain judgment into a measurable property.
		public ElisionImpactResult ElisionImpactDiff(long[] candidateSkipEntryIds, params string[] observationQueries)
		{
			ThrowIfDisposed();
			ArgumentNullException.ThrowIfNull(candidateSkipEntryIds);
			ArgumentNullException.ThrowIfNull(observationQueries);
			if (observationQueries.Length == 0) throw new LanguageException("ElisionImpactDiff needs at least one observation query to compare.");

			long head = shadowHandler.CurrentEntryId;

			// Unique names per call (the twins are their own storage; reused names
			// would risk residual state in per-name backends like InMemory).
			int n = ++diffCounter;
			ActorHandler twinFull = shadowHandler.CreateShadow(new ShadowConfig(config.Id + "-s4full-" + n, config.ShadowStorageType, config.ShadowStorageConnection));
			ActorHandler twinElided = shadowHandler.CreateShadow(new ShadowConfig(config.Id + "-s4elided-" + n, config.ShadowStorageType, config.ShadowStorageConnection));
			try
			{
				twinFull.SeedElided(head, Array.Empty<long>());
				twinElided.SeedElided(head, candidateSkipEntryIds);

				List<ElisionObservationDiff> diffs = new List<ElisionObservationDiff>();
				foreach (string q in observationQueries)
				{
					if (q == null) throw new ArgumentNullException(nameof(observationQueries), "An observation query cannot be null.");
					string withoutElision = twinFull.PerformQry(q, new Parameters());
					string withElision = twinElided.PerformQry(q, new Parameters());
					if (!string.Equals(withoutElision, withElision, StringComparison.Ordinal))
						diffs.Add(new ElisionObservationDiff(q, withoutElision, withElision));
				}

				return new ElisionImpactResult(diffs);
			}
			finally
			{
				twinFull.GracefulExit();
				twinFull.TryClearShadowStorage();
				twinElided.GracefulExit();
				twinElided.TryClearShadowStorage();
			}
		}

		// Local command driver against the shadow (its own storage). Useful for
		// inducing the experiment's local divergence. V1 path (ActorV1) — the async
		// flow is the handler's public one. For V2, the caller uses shadow.Actor
		// (ActorV2) and its Using(...)/Dispatch surface.
		public string PerformCmd(string script, string ip, string user)
		{
			ThrowIfDisposed();
			if (shadowHandler.IsShadowingActive) throw new LanguageException("Cannot drive a shadow with local commands while continuous shadowing is active — continuous mirror and fork are mutually exclusive. Call StopShadowing() first.");
			ArgumentNullException.ThrowIfNull(script);
			if (ip == null) throw new ArgumentNullException(nameof(ip));
			if (user == null) throw new ArgumentNullException(nameof(user));
			return shadowHandler.PerformCmd(script, ip, user);
		}

		// Read-only query against the shadow's state.
		public string PerformQry(string script)
		{
			ThrowIfDisposed();
			ArgumentNullException.ThrowIfNull(script);
			return shadowHandler.PerformQry(script, new Parameters());
		}

		// Shadow Replay — S2. Continuous shadowing: starts a background mirror that
		// follows the primary's head in near-real-time (incremental pull of new records
		// + rehydration). Mutually exclusive with the SyncUntil fork — while active,
		// local PerformCmd is rejected. Stop via StopShadowing() or Dispose().
		public void StartShadowing()
		{
			ThrowIfDisposed();
			shadowHandler.StartShadowing();
		}

		// Stops the continuous mirror (idempotent). After this the shadow again
		// accepts local commands (fork).
		public void StopShadowing()
		{
			ThrowIfDisposed();
			shadowHandler.StopShadowing();
		}

		public bool IsShadowing => shadowHandler.IsShadowingActive;

		// TTL kill-all (S1): graceful shutdown of the shadow's reactions and cleanup
		// of its own storage. Idempotent. The teardown via K8s Job (activeDeadlineSeconds)
		// is S6.
		public void Dispose()
		{
			if (disposed) return;
			disposed = true;

			shadowHandler.StopShadowing();
			shadowHandler.GracefulExit();
			shadowHandler.TryClearShadowStorage();
		}

		private void ThrowIfDisposed()
		{
			if (disposed) throw new LanguageException("This shadow has been disposed (TTL kill-all). Build a new one via actor.Shadow(cfg).");
		}
	}
}
