using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Theater;
using Choreography.Transport;
using Puppeteer;
using Puppeteer.EventSourcing.Interpreter.Formatters;

namespace Choreography.StageManager
{
    public class StageV2 : Stage
    {
        private IOutputFormatter formatterPrototype;  // null = default JsonFormatter
        // Authoring transpiler (input-side mirror of formatterPrototype).
        // Default = Identity so every Stage always carries one. Runs locally at
        // author-time BEFORE the Director/Cast branch, so only the transpiled
        // body crosses the wire and enters the journal — never the notation.
        private INotationTranspiler transpilerPrototype = IdentityTranspiler.Instance;

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
        // Limitation (out-of-scope until later sign-off): when this Stage
        // is in Cast role and forwards a command to the Director over the
        // transport, the formatter context does NOT cross the wire. The
        // Director executes with its own formatter context. Each Stage
        // manages its own rendering format independently.

        public StageV2 Formatter(IOutputFormatter prototype)
        {
            this.formatterPrototype = prototype;
            return this;
        }

        // Install the authoring transpiler for this Stage (input-side mirror of
        // Formatter). Lowers a domain notation into a Puppeteer DSL command body
        // at author-time; only the transpiled body is journaled / forwarded,
        // never the transpiler. Default = Identity.
        public StageV2 Transpiler(INotationTranspiler prototype)
        {
            this.transpilerPrototype = prototype ?? throw new ArgumentNullException(nameof(prototype));
            return this;
        }

        // Shadow of the base Logger to preserve StageV2 in the fluent chain
        // (so it can be chained with V2-typed Formatter/ConfigureStorage).
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

        // ── Enact (capture a transpiled construction as the Action) ────────
        // The transpile runs locally at author-time; the transpiled body then
        // flows through the normal Director-executes / Cast-forwards path, so
        // the wire and the journal only ever see the lowered DSL. Identity
        // transpiler => the notation is already Puppeteer DSL, enacted verbatim.

        public Task<string> PerformEnact(string notation, CancellationToken ct = default)
        {
            if (notation == null) throw new ArgumentNullException(nameof(notation));
            string body = transpilerPrototype.Transpile(notation);
            return PerformCmd(body, ct);
        }

        public Task<string> PerformEnact(string notation, DateTime now, string ip, string user,
            CancellationToken ct = default)
        {
            if (notation == null) throw new ArgumentNullException(nameof(notation));
            string body = transpilerPrototype.Transpile(notation);
            return PerformCmd(body, now, ip, user, ct);
        }

        public Task<string> PerformCheckThenEnact(string check, string notation,
            DateTime now, string ip, string user, CancellationToken ct = default)
        {
            if (check == null) throw new ArgumentNullException(nameof(check));
            if (notation == null) throw new ArgumentNullException(nameof(notation));
            string body = transpilerPrototype.Transpile(notation);
            return PerformCheckThenCommand(check, body, now, ip, user, ct);
        }
    }
}
