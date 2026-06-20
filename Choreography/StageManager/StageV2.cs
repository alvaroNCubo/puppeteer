using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Transport;
using Puppeteer;
using Puppeteer.EventSourcing.Interpreter.Formatters;

namespace Choreography.StageManager
{
    public class StageV2 : Stage
    {
        private IOutputFormatter formatterPrototype;  // null = default JsonFormatter

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

        // ── Formatter API (V2-only, fluent) ─────────────────────────────────
        //
        // Install the formatter prototype for this Stage. Subsequent
        // PerformCmd/PerformQry/PerformCheckThenCommand calls render their
        // output via a per-Output instance of this prototype (CreateNew +
        // Reset lifecycle managed by the ExecutionOutput pool).
        //
        // Limitation (out-of-scope until firma posterior): when this Stage
        // is in Cast role and forwards a command to the Director over the
        // transport, the formatter context does NOT cross the wire. The
        // Director executes with its own formatter context. Each Stage
        // manages its own rendering format independently.

        public StageV2 Formatter(IOutputFormatter prototype)
        {
            this.formatterPrototype = prototype;
            return this;
        }

        // Shadow del Logger base para preservar StageV2 en la cadena fluent
        // (asi se puede encadenar con Formatter/ConfigureStorage V2-tipados).
        public new StageV2 Logger(IPuppeteerLogger logger)
        {
            base.Logger(logger);
            return this;
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
                using (FormatterContext.Push(formatterPrototype))
                {
                    // AsyncLocal flows into Task.Run worker thread.
                    return await Task.Run(() => hook.PerformCmd(script, now, ip, user));
                }
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
                using (FormatterContext.Push(formatterPrototype))
                {
                    return await Task.Run(() => hook.PerformCmd(script, parameters, now, ip, user));
                }
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
                using (FormatterContext.Push(formatterPrototype))
                {
                    return await Task.Run(() => hook.PerformCheckThenCmd(scriptForChk, scriptForCmd, now, ip, user));
                }
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
                using (FormatterContext.Push(formatterPrototype))
                {
                    return await Task.Run(() => hook.PerformCheckThenCmd(scriptForChk, scriptForCmd, parameters, now, ip, user));
                }
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
            using (FormatterContext.Push(formatterPrototype))
            {
                return hook.PerformQry(script, parameters);
            }
        }
    }
}
