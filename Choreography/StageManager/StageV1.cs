using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Transport;
using Puppeteer;

namespace Choreography.StageManager
{
    public class StageV1 : Stage
    {
        public StageV1(PerformerId id, string actorName)
            : base(id, actorName)
        {
        }

        public StageV1(PerformerId id, string actorName, params Assembly[] libraryAssemblies)
            : base(id, actorName, libraryAssemblies)
        {
        }

        protected override Actor CreateActor(string actorName)
        {
            // ActorFactory.Create<ActorV1> uses Activator.CreateInstance with a single arg (name).
            // For multi-assembly go directly to the ActorV1 ctor.
            return LibraryAssemblies != null && LibraryAssemblies.Length > 0
                ? new ActorV1(actorName, LibraryAssemblies)
                : ActorFactory.Create<ActorV1>(actorName);
        }

        // Shadow of the base Logger to preserve StageV1 in the fluent chain.
        public new StageV1 Logger(IPuppeteerLogger logger)
        {
            base.Logger(logger);
            return this;
        }

        public async Task<string> PerformCmd(string script, CancellationToken ct = default)
        {
            EnsureCanWrite();

            if (IsDirector)
            {
                return await Task.Run(() => hook.PerformCmd(script));
            }
            else
            {
                var now = DateTime.Now;
                var msg = new ForwardCommand(Id, Guid.NewGuid(), script, string.Empty,
                    now, "0.0.0.0", "Anonymous");
                return await ForwardToDirector(msg, ct);
            }
        }

        public string PerformQry(string script)
        {
            // Phase 4.5 Playbill refactor: ip/user are no longer injected as script parameters.
            var p = new Parameters();
            return hook.PerformQry(script, p);
        }
    }
}
