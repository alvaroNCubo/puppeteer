using Puppeteer.EventSourcing;
using System;

namespace Puppeteer
{
	// Shadow Replay — S1 (handoff_shadow_S1_implementation.md). Shadow replay mode:
	// PointInTime = catch-up up to a ceiling and stop (fork); Continuous =
	// follow the real stream near-real-time (S2 — stub in S1).
	public enum ShadowMode
	{
		PointInTime,
		Continuous
	}

	// Configuration of a Shadow. The shadow is a laboratory derivation of a production
	// actor: it reads the real journal (replay) but writes to its OWN storage and
	// produces ZERO external effect (Tells neutralized, not registered as a Materialization
	// destination). See §3.0 of the design.
	//
	// Isolation invariant: the shadow's storage can NEVER be the same as the primary's.
	// ActorHandler.CreateShadow validates the separation by actor name; additionally the
	// shadow runs with a derived name (`<primary>-shadow-<Id>`) that, in the per-name
	// backends (InMemory) and per-path backends (FileSystem), guarantees a distinct
	// storage when the connection coincides.
	public sealed class ShadowConfig
	{
		// Experiment identifier. Combined with the primary's name to produce the
		// shadow actor's name: `<primary>-shadow-<Id>`.
		public string Id { get; }

		// Backend of the shadow's OWN storage.
		public DatabaseType ShadowStorageType { get; }

		// Connection / path of the shadow's own storage. For InMemory any non-empty
		// string works (the backend partitions by actor name). For FileSystem it must
		// be a path DIFFERENT from the primary's.
		public string ShadowStorageConnection { get; }

		// Replay mode. S1 only fully implements PointInTime (via SyncUntil).
		// Continuous (StartShadowing) is S2 and remains a stub.
		public ShadowMode Mode { get; }

		// Optional TTL of the entire shadow (kill-all: storage + reactions). null =
		// no TTL managed by the framework (the host disposes manually). The TTL via
		// K8s Job is S6 and is not implemented here.
		public TimeSpan? Ttl { get; }

		// Optional callback to register reactions on the freshly built shadow.
		// S1: the primary's reaction definitions are not serialized/cloned
		// automatically (the builder is imperative). The host re-declares here the
		// static reactions it wants to observe + the new experimental ones, using
		// the SAME Theme A API pointing at the shadow. If null, the shadow starts
		// without reactions.
		public Action<Actor> ConfigureReactions { get; }

		public ShadowConfig(
			string id,
			DatabaseType shadowStorageType,
			string shadowStorageConnection,
			ShadowMode mode = ShadowMode.PointInTime,
			TimeSpan? ttl = null,
			Action<Actor> configureReactions = null)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(id);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(shadowStorageConnection);
			if (ttl.HasValue && ttl.Value <= TimeSpan.Zero)
				throw new LanguageException($"Shadow TTL must be greater than zero when specified (got {ttl.Value}).");

			this.Id = id;
			this.ShadowStorageType = shadowStorageType;
			this.ShadowStorageConnection = shadowStorageConnection;
			this.Mode = mode;
			this.Ttl = ttl;
			this.ConfigureReactions = configureReactions;
		}
	}
}
