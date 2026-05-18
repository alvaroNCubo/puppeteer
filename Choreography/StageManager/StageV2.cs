using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Transport;
using Puppeteer;

namespace Choreography.StageManager
{
    public class StageV2 : Stage
    {
        public StageV2(PerformerId id, string actorName)
            : base(id, actorName)
        {
        }

        public StageV2(PerformerId id, string actorName, params Assembly[] libraryAssemblies)
            : base(id, actorName, libraryAssemblies)
        {
        }

        protected override Actor CreateActor(string actorName)
        {
            return LibraryAssemblies != null && LibraryAssemblies.Length > 0
                ? new ActorV2(actorName, LibraryAssemblies)
                : new ActorV2(actorName);
        }

        public async Task<string> PerformCmd(string script, DateTime now, string ip, string user,
            CancellationToken ct = default)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (user == null) throw new ArgumentNullException(nameof(user));

            EnsureCanWrite();

            if (IsDirector)
            {
                return await Task.Run(() => hook.PerformCmd(script, now, ip, user));
            }
            else
            {
                var msg = new ForwardCommand(Id, Guid.NewGuid(), script, string.Empty,
                    now, ip, user);
                return await ForwardToDirector(msg, ct);
            }
        }

        public async Task<string> PerformCmd(string script, CancellationToken ct = default)
        {
            return await PerformCmd(script, DateTime.Now, "0.0.0.0", "Anonymous", ct);
        }

        public async Task<string> PerformCmd(string script, Parameters parameters, DateTime now, string ip, string user,
            CancellationToken ct = default)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (user == null) throw new ArgumentNullException(nameof(user));

            EnsureCanWrite();

            if (IsDirector)
            {
                return await Task.Run(() => hook.PerformCmd(script, parameters, now, ip, user));
            }
            else
            {
                string serializedParameters = parameters.SerializeForTransport(dbType);
                var msg = new ForwardCommand(Id, Guid.NewGuid(), script, serializedParameters,
                    now, ip, user);
                return await ForwardToDirector(msg, ct);
            }
        }

        public async Task<string> PerformCheckThenCommand(string scriptForChk, string scriptForCmd,
            DateTime now, string ip, string user, CancellationToken ct = default)
        {
            if (scriptForChk == null) throw new ArgumentNullException(nameof(scriptForChk));
            if (scriptForCmd == null) throw new ArgumentNullException(nameof(scriptForCmd));
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (user == null) throw new ArgumentNullException(nameof(user));

            EnsureCanWrite();

            if (IsDirector)
            {
                return await Task.Run(() => hook.PerformCheckThenCmd(scriptForChk, scriptForCmd, now, ip, user));
            }
            else
            {
                var msg = new ForwardCommand(Id, Guid.NewGuid(), scriptForChk, scriptForCmd,
                    string.Empty, now, ip, user);
                return await ForwardToDirector(msg, ct);
            }
        }

        public async Task<string> PerformCheckThenCommand(string scriptForChk, string scriptForCmd,
            Parameters parameters, DateTime now, string ip, string user, CancellationToken ct = default)
        {
            if (scriptForChk == null) throw new ArgumentNullException(nameof(scriptForChk));
            if (scriptForCmd == null) throw new ArgumentNullException(nameof(scriptForCmd));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (user == null) throw new ArgumentNullException(nameof(user));

            EnsureCanWrite();

            if (IsDirector)
            {
                return await Task.Run(() => hook.PerformCheckThenCmd(scriptForChk, scriptForCmd, parameters, now, ip, user));
            }
            else
            {
                string serializedParameters = parameters.SerializeForTransport(dbType);
                var msg = new ForwardCommand(Id, Guid.NewGuid(), scriptForChk, scriptForCmd,
                    serializedParameters, now, ip, user);
                return await ForwardToDirector(msg, ct);
            }
        }

        public string PerformQry(string script, Parameters parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            return hook.PerformQry(script, parameters);
        }
    }
}
