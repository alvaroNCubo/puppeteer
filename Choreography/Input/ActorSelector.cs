namespace Choreography.Input
{
	// The ACTOR facet of an input route: raw signal -> performer id. Routing is
	// fractal — a message descends a path of discriminators, one per altitude:
	//
	//   medium -> node -> ACTOR -> verb -> arguments
	//
	// InputRouting resolves the verb altitude (signal -> command) for a host whose
	// actor was fixed at wiring time. This delegate names the altitude above it:
	// WHICH performer of an ensemble the signal animates. Until now that segment
	// travelled out-of-band — a loose actor-name parameter pushed by hand into
	// GetOrCreate(id) — so the ensemble received its routing instead of owning it.
	// With the selector, an ensemble consumes from a source the same way a single
	// actor does: the selector picks the performer, InputRouting shapes the
	// command, and the composition (selector, then routing) IS the ensemble's
	// routing role. The ensemble adds exactly one segment to the route; everything
	// below it is the same per-actor seam, unchanged.
	//
	// Returns null to DROP the signal: no performer is created or rehydrated,
	// nothing is enqueued, nothing is idempotency-recorded — mirroring
	// InputRouting's null-drop contract one altitude up.
	public delegate string ActorSelector(InputSignal signal);

	// Stock selectors. ByKey is the natural default: InputSignal.Key is the
	// partition/ordering key the medium carried (a Kafka partition key, a broker
	// record key) — on a partitioned medium the partition key already IS the
	// actor segment of the route, so folding it into the selector is naming an
	// existing practice, not inventing one. A signal whose medium carried no key
	// routes nowhere (drop); it never lands on a performer with an empty name.
	public static class ActorSelectors
	{
		public static readonly ActorSelector ByKey = signal =>
			string.IsNullOrWhiteSpace(signal.Key) ? null : signal.Key;
	}
}
