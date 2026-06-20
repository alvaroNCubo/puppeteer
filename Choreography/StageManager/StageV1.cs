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
            // ActorFactory.Create<ActorV1> usa Activator.CreateInstance con un solo arg (name).
            // Para multi-assembly se va directo al ctor de ActorV1.
            return LibraryAssemblies != null && LibraryAssemblies.Length > 0
                ? new ActorV1(actorName, LibraryAssemblies)
                : ActorFactory.Create<ActorV1>(actorName);
        }

        // Shadow del Logger base para preservar StageV1 en la cadena fluent.
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
            // Fase 4.5 refactor Playbill: ip/user dejaron de inyectarse como parametros del script.
            var p = new Parameters();
            return hook.PerformQry(script, p);
        }
    }
}
