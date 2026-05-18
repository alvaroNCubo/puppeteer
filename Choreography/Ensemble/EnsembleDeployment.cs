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

    public class EnsembleDeployment<T> where T : Performance
    {
        private readonly EnsemblePerformance<T> ensemble;
        private readonly IRemoteEnsembleProxy remoteProxy;
        private readonly ConcurrentDictionary<string, ActorLocation> routingTable = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> drainingGates = new(StringComparer.OrdinalIgnoreCase);

        private DeploymentPhase phase = DeploymentPhase.FullyLocal;

        public DeploymentPhase Phase => phase;

        public EnsembleDeployment(EnsemblePerformance<T> ensemble, IRemoteEnsembleProxy remoteProxy)
        {
            this.ensemble = ensemble ?? throw new ArgumentNullException(nameof(ensemble));
            this.remoteProxy = remoteProxy ?? throw new ArgumentNullException(nameof(remoteProxy));
        }

        public ActorLocation ResolveLocation(string actorId)
        {
            if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentNullException(nameof(actorId));

            if (routingTable.TryGetValue(actorId, out var location))
                return location;

            return phase == DeploymentPhase.ProxyAll ? ActorLocation.Remote : ActorLocation.Local;
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

        public void SetPhase(DeploymentPhase newPhase)
        {
            this.phase = newPhase;
        }
    }
}
