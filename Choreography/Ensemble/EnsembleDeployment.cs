using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Theater;
using Puppeteer;

namespace Choreography.Ensemble
{
    public enum DeploymentPhase
    {
        FullyLocal,
        ProxyAll,
        Migrating
    }

    // The NODE altitude of the fractal route (medium -> NODE -> actor -> verb).
    // Where EnsemblePerformance.ConsumeFrom owns the actor altitude — the
    // ActorSelector picking which performer a signal animates — this owns the
    // altitude above it: which node hosts that performer. ResolveLocation is that
    // discriminator, and it composes two things in order:
    //
    //   1. the routing table — explicit OPERATIONAL state (an actor being
    //      migrated is Draining; a migrated actor is Local). These are overrides
    //      and always win.
    //   2. the NodePlacement STRATEGY — where an actor the table does not pin
    //      goes. Pluggable: phase-based (the built-in default), consistent
    //      hashing, a range table, or a placement reading an SM-backed table
    //      (the elastic corner, route ∘ SM). Injecting the strategy is how the
    //      corner becomes expressible without a new subsystem.
    public class EnsembleDeployment<T> where T : Performance
    {
        private readonly EnsemblePerformance<T> ensemble;
        private readonly IRemoteEnsembleProxy remoteProxy;
        private readonly ConcurrentDictionary<string, ActorLocation> routingTable = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> drainingGates = new(StringComparer.OrdinalIgnoreCase);
        private readonly NodePlacement placement;

        private DeploymentPhase phase = DeploymentPhase.FullyLocal;

        public DeploymentPhase Phase => phase;

        // placement == null uses the built-in phase-based strategy: an actor the
        // table does not pin is Remote under ProxyAll, Local otherwise. Pass a
        // placement to swap the strategy (consistent hashing, SM-backed table,
        // …) without touching the operational-override path.
        public EnsembleDeployment(EnsemblePerformance<T> ensemble, IRemoteEnsembleProxy remoteProxy, NodePlacement placement = null)
        {
            this.ensemble = ensemble ?? throw new ArgumentNullException(nameof(ensemble));
            this.remoteProxy = remoteProxy ?? throw new ArgumentNullException(nameof(remoteProxy));
            this.placement = placement ?? PhaseBasedPlacement;
        }

        // The built-in strategy — the node facet as it behaved before it was
        // named: unknown actors follow the deployment phase.
        private ActorLocation PhaseBasedPlacement(string actorId)
            => phase == DeploymentPhase.ProxyAll ? ActorLocation.Remote : ActorLocation.Local;

        public ActorLocation ResolveLocation(string actorId)
        {
            if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentNullException(nameof(actorId));

            // Operational overrides (migration state) win over the strategy.
            if (routingTable.TryGetValue(actorId, out var location))
                return location;

            return placement(actorId);
        }

        public async Task MigrateToLocalAsync(string actorId, DatabaseType dbType, string connectionString, TimeSpan drainTimeout)
        {
            if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentNullException(nameof(actorId));

            routingTable[actorId] = ActorLocation.Draining;
            var gate = new ManualResetEventSlim(false);
            drainingGates[actorId] = gate;

            await Task.Delay(drainTimeout);

            long remoteEntryId = await remoteProxy.GetCurrentEntryId(actorId);

            var performance = ensemble.GetOrCreate(actorId);
            performance.ConfigureStorage(dbType, connectionString);
            performance.Start();
            performance.CatchUpFromJournal(remoteEntryId);

            routingTable[actorId] = ActorLocation.Local;
            gate.Set();
            drainingGates.TryRemove(actorId, out _);

            await remoteProxy.NotifyEviction(actorId);
        }

        public void WaitIfDraining(string actorId)
        {
            if (drainingGates.TryGetValue(actorId, out var gate))
            {
                gate.Wait();
            }
        }

        public async Task<string> RouteCommand(string actorId, string script, string ip, string user, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentNullException(nameof(actorId));

            var location = ResolveLocation(actorId);

            switch (location)
            {
                case ActorLocation.Draining:
                    WaitIfDraining(actorId);
                    return await RouteCommand(actorId, script, ip, user, now);

                case ActorLocation.Remote:
                    return await remoteProxy.ForwardCommand(actorId, script, ip, user, now);

                case ActorLocation.Local:
                default:
                    var performance = ensemble.GetOrCreate(actorId);
                    return performance.Name;
            }
        }

        public async Task<string> RouteQuery(string actorId, string script)
        {
            if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentNullException(nameof(actorId));

            var location = ResolveLocation(actorId);

            switch (location)
            {
                case ActorLocation.Remote:
                    return await remoteProxy.ForwardQuery(actorId, script);

                case ActorLocation.Draining:
                    WaitIfDraining(actorId);
                    return await RouteQuery(actorId, script);

                case ActorLocation.Local:
                default:
                    var performance = ensemble.GetOrCreate(actorId);
                    return performance.Name;
            }
        }

        // Write an operational override into the routing table: an explicit pin
        // that wins over the placement strategy. This is what migration records
        // (Draining while a handoff is in flight, Local once it lands); exposing
        // it names the table's write path — the strategy answers "where does an
        // unpinned actor go", the pins answer "where is THIS actor, right now,
        // because an operation moved it".
        public void PinLocation(string actorId, ActorLocation location)
        {
            if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentNullException(nameof(actorId));
            routingTable[actorId] = location;
        }

        public void SetPhase(DeploymentPhase newPhase)
        {
            this.phase = newPhase;
        }
    }
}
