using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Choreography.Dispatch;
using Choreography.Saga;
using Puppeteer;
using Puppeteer.EventSourcing.Follower;
using Puppeteer.EventSourcing.Interpreter.Formatters;
using Puppeteer.EventSourcing.Playbill;

namespace Choreography.Theater
{
    public class PerformanceV2 : Performance
    {
        private readonly ActorV2 actorV2;
        private IOutputFormatter formatterPrototype;  // null = default JsonFormatter

        // Authoring front-end (input-side mirror of formatterPrototype): the
        // transpiler that lowers a domain notation into a Puppeteer DSL command
        // body for PerformCheckThenEnact. Defaults to Identity so every
        // Performance always carries one; an N-projection wrapper can override it
        // via .Transpiler(...) to expose a different authoring notation over the
        // same shared actor/journal.
        private INotationTranspiler transpilerPrototype = IdentityTranspiler.Instance;

        // Playbill state — lazily constructed when .Playbill(name, builder) is
        // called for the first time. Auto-provision: backend creates its tables/
        // files at construction. Null = audit-off (legitimate per design memo).
        private Playbill playbill;
        private string currentPlaybillSchemaName;

        // Convenience: if the caller does not provide libraries, the assembly from
        // which the ctor was invoked is assumed. Useful when domain and interface live in
        // the same project. The idiomatic path is to pass the domain DLLs explicitly
        // (see the ctor with params Assembly[]).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public PerformanceV2(string actorName)
            : this(actorName, new[] { Assembly.GetCallingAssembly() })
        {
        }

        public PerformanceV2(string actorName, params Assembly[] libraryAssemblies)
            : base(actorName, libraryAssemblies)
        {
            actorV2 = (ActorV2)ActorInstance;
        }

        public PerformanceV2(PerformanceV1 source) : base(source)
        {
            actorV2 = new ActorV2(source.GetActorV1());
            hook = new StageHook(actorV2);
            ActorInstance = actorV2;
        }

        /// <summary>
        /// N-projection ctor (Paper 9 substrate): construct a wrapper that
        /// SHARES the underlying actor, journal hook, and storage config
        /// with the <paramref name="source"/>, but maintains its own
        /// formatter. A single command executed via the source writes once
        /// to the journal; queries executed via this wrapper read the
        /// resulting state and render in this wrapper's format.
        ///
        /// <para>
        /// The shared references are the actorV2 handle, the StageHook
        /// (which owns the Diary, lock, and reactions), and the storage
        /// configuration. This wrapper does NOT own those — disposing it
        /// must NOT dispose the source.
        /// </para>
        /// </summary>
        public PerformanceV2(PerformanceV2 source) : base(source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            // Share the actor and hook references — no new actor, no new
            // hook, no new journal write path.
            actorV2 = source.actorV2;
            hook = source.hook;
            ActorInstance = source.ActorInstance;
            // formatterPrototype starts null; caller can override with .Formatter()
        }

        protected override Actor CreateActor(string actorName)
        {
            return LibraryAssemblies != null && LibraryAssemblies.Length > 0
                ? new ActorV2(actorName, LibraryAssemblies)
                : new ActorV2(actorName);
        }

        public ActorV2 Actor => actorV2;

        // ── Formatter API (V2-only, fluent) ────────────────────────────────

        /// <summary>
        /// Install the formatter prototype for this Performance. Each
        /// subsequent PerformCmd/PerformQry call on this instance renders
        /// its output through a per-Output instance of this prototype
        /// (via the framework-controlled CreateNew + Reset lifecycle).
        /// </summary>
        /// <param name="prototype">A formatter prototype (e.g.
        /// <c>new JsonFormatter()</c>, <c>new ToonFormatter()</c>,
        /// <c>new XmlFormatter()</c>). Pass <c>null</c> to revert to the
        /// default (JsonFormatter).</param>
        /// <returns>this, for fluent chaining.</returns>
        public PerformanceV2 Formatter(IOutputFormatter prototype)
        {
            this.formatterPrototype = prototype;
            return this;
        }

        // Install the authoring transpiler for this Performance (input-side
        // mirror of Formatter). The transpiler lowers a domain notation into a
        // Puppeteer DSL command body at author-time; only the transpiled body is
        // journaled, never the transpiler. Pair with the N-projection ctor to
        // expose several authoring notations over one shared actor/journal:
        // new PerformanceV2(basePerformance).Transpiler(bracketTranspiler).
        public PerformanceV2 Transpiler(INotationTranspiler prototype)
        {
            this.transpilerPrototype = prototype ?? throw new ArgumentNullException(nameof(prototype));
            return this;
        }

        // Shadow of the base Logger to preserve PerformanceV2 in the fluent chain
        // (so it can be chained with the V2-typed Formatter/ConfigureStorage/Start).
        public new PerformanceV2 Logger(IPuppeteerLogger logger)
        {
            base.Logger(logger);
            return this;
        }

        // ── Fluent shadowing of base ConfigureStorage / Start ─────────────
        // Base methods stay void; V2 hides them with `new` returning
        // PerformanceV2 so the fluent chain preserves V2 type info for
        // chaining .Formatter() at the end.

        public new PerformanceV2 ConfigureStorage(DatabaseType dbType, string connectionString)
        {
            base.ConfigureStorage(dbType, connectionString);
            return this;
        }

        public new PerformanceV2 Start(bool asFollower = false)
        {
            base.Start(asFollower);
            return this;
        }

        // ── Script execution (V2 surface) ──────────────────────────────────

        public string PerformCmd(string script, string ip, string user)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (user == null) throw new ArgumentNullException(nameof(user));

            LastActivity = DateTime.Now;
            using (FormatterContext.Push(formatterPrototype))
            {
                return hook.PerformCmd(script, DateTime.Now, ip, user);
            }
        }

        public string PerformCmd(string script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));

            LastActivity = DateTime.Now;
            using (FormatterContext.Push(formatterPrototype))
            {
                return hook.PerformCmd(script);
            }
        }

        public string PerformQry(string script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));

            LastActivity = DateTime.Now;
            // Phase 4.5 Playbill refactor: ip/user are no longer injected as script parameters.
            // Now is set by the handler in PerformQry/PerformChk/PerformEmit.
            var p = new Parameters();
            using (FormatterContext.Push(formatterPrototype))
            {
                return hook.PerformQry(script, p);
            }
        }

        // ── Enact (V2 surface): capture a computed command body as the Action ──

        // Enact: lower a domain authoring notation into a Puppeteer DSL command
        // body via this Performance's transpiler (Identity by default), then
        // perform it as a command. The transpiler runs ONLY here, at author-time;
        // only the transpiled body is journaled as the Action, so replay
        // reconstructs the structure WITHOUT the transpiler. With the Identity
        // transpiler the notation is already Puppeteer DSL and is enacted
        // verbatim — which shows the essence is "capture a computed command",
        // not "transpile". The journaled fact is the body, never transform(input).
        public string PerformEnact(string notation)
        {
            if (notation == null) throw new ArgumentNullException(nameof(notation));
            string body = transpilerPrototype.Transpile(notation);
            return PerformCmd(body);
        }

        // CheckThenEnact: felicity-guarded Enact. The check is re-evaluated under
        // the write lock by PerformCheckThenCommand, protecting against the source
        // changing between the author-time transpile and the commit (e.g. a guard
        // comparing current actor state to the notation that was read). The
        // transpile still happens once, here, outside any lock; only the
        // transpiled body is journaled.
        public string PerformCheckThenEnact(string check, string notation)
        {
            if (check == null) throw new ArgumentNullException(nameof(check));
            if (notation == null) throw new ArgumentNullException(nameof(notation));
            string body = transpilerPrototype.Transpile(notation);
            return new ActorV2Invocation(actorV2, check, body, playbill, currentPlaybillSchemaName)
                .PerformCheckThenCommand();
        }

        // CheckThenEnact overload accepting parameters for the check (e.g. the
        // captured notation to compare against, supplied as an In parameter).
        public string PerformCheckThenEnact(string check, string notation, Action<Parameters> configure)
        {
            if (check == null) throw new ArgumentNullException(nameof(check));
            if (notation == null) throw new ArgumentNullException(nameof(notation));
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            string body = transpilerPrototype.Transpile(notation);
            return new ActorV2Invocation(actorV2, check, body, playbill, currentPlaybillSchemaName)
                .WithParameters(configure)
                .PerformCheckThenCommand();
        }

        // ── Playbill API (V2-only, fluent) ────────────────────────────────

        // Declares a Playbill schema on this Performance. Registers the schema
        // in the PlaybillStore (auto-provisioning the storage if it does not exist)
        // and leaves it as the "current" schema for subsequent invocations
        // via Using(...).
        //
        // Called more than once with DIFFERENT names: each DefinePlaybill
        // registers its schema; the last one remains as the currentSchemaName.
        // Called twice with the SAME name + same signature: idempotent.
        // Same name + different signature: LanguageException (drift requires
        // explicit migration).
        public PerformanceV2 Playbill(string schemaName, Action<PlaybillSchemaBuilder> build)
        {
            if (schemaName == null) throw new ArgumentNullException(nameof(schemaName));
            if (build == null) throw new ArgumentNullException(nameof(build));
            if (!storageConfigured) throw new InvalidOperationException("Storage not configured. Call ConfigureStorage before Playbill.");

            if (playbill == null)
            {
                playbill = new Playbill(dbType, connectionString, Name, actorV2.Handler.Logger);
            }

            var builder = new PlaybillSchemaBuilder();
            build(builder);
            string declarations = builder.BuildDeclarations();
            playbill.RegisterSchema(schemaName, declarations);
            currentPlaybillSchemaName = schemaName;
            return this;
        }

        // ── Fluent invocation entry (V2 + Playbill-aware) ─────────────────

        // Playbill-aware variant of the V2 fluent entry. Equivalent to perf.Actor.Using(...)
        // BUT additionally passes the Playbill context to the ActorV2Invocation,
        // enabling .WithPlaybill(...) and the automatic second-write on persist.
        //
        // If the Performance has no Playbill configured, the context is null —
        // the invocation is functionally identical to perf.Actor.Using(script).
        public ActorV2Invocation Using(string script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            return new ActorV2Invocation(actorV2, script, playbill, currentPlaybillSchemaName);
        }

        public ActorV2Invocation Using(string scriptForChk, string scriptForCmd)
        {
            if (scriptForChk == null) throw new ArgumentNullException(nameof(scriptForChk));
            if (scriptForCmd == null) throw new ArgumentNullException(nameof(scriptForCmd));
            return new ActorV2Invocation(actorV2, scriptForChk, scriptForCmd, playbill, currentPlaybillSchemaName);
        }

        // ── Dispatch / Saga (existing) ─────────────────────────────────────

        public Dispatch.Dispatch CreateDispatch(Action<DispatchOptions> configure = null)
        {
            return CreateDispatchInternal(actorV2, configure);
        }

        public SagaDefinition DefineSaga(string name)
        {
            return DefineSagaInternal(name);
        }
    }
}
