namespace Choreography.Ensemble
{
	// The NODE facet of the route: actor id -> where that actor lives. Routing is
	// fractal (medium -> NODE -> actor -> verb -> args) and each altitude is a
	// discriminator. ActorSelector (Choreography.Input) names the actor altitude
	// (signal -> which performer); this names the altitude ABOVE it (which node
	// hosts that performer). Placement is a function of actor IDENTITY, not of the
	// raw signal — consistent hashing, rendezvous hashing, and a range table all
	// place actorX on nodeN by hashing/looking up X. So the route composes as
	//
	//     placement ∘ selector       (node altitude ∘ actor altitude)
	//
	// then everything below is the per-actor seam, unchanged.
	//
	// It lives in Ensemble, not Input, because it speaks ActorLocation — the node
	// altitude is a deployment concern; the input seam below it knows nothing of
	// nodes.
	//
	// A placement is a STRATEGY, and naming it makes the strategy swappable:
	// phase-based (the built-in default), consistent-hashing, a static range
	// table, or — the elastic corner — a placement that reads a table which is
	// itself a replicated single-writer actor. That last one is route ∘ SM: the
	// same shape a range-sharded store (CockroachDB, TiKV, Spanner) or a control
	// plane (etcd behind a scheduler) already builds, expressed here as one
	// delegate instead of a subsystem.
	//
	// ActorLocation is binary here (Local / Remote / Draining) because the current
	// remote surface is a single IRemoteEnsembleProxy. The natural generalization
	// is actorId -> NodeId over N peers; the binary case is the 2-node instance.
	public delegate ActorLocation NodePlacement(string actorId);
}
