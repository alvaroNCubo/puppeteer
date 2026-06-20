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

        // Playbill state — lazily constructed when .Playbill(name, builder) is
        // called for the first time. Auto-provision: backend creates its tables/
        // files at construction. Null = audit-off (legitimate per design memo).
        private Playbill playbill;
        private string currentPlaybillSchemaName;

        // Conveniencia: si el caller no provee libraries, se asume el assembly desde
        // donde se invoco el ctor. Util cuando dominio e interfaz viven en el mismo
        // proyecto. El path idiomatico es pasar las DLLs de dominio explicitamente
        // (ver el ctor con params Assembly[]).
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

        // Shadow del Logger base para preservar PerformanceV2 en la cadena fluent
        // (asi se puede encadenar con Formatter/ConfigureStorage/Start V2-tipados).
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
            // Fase 4.5 refactor Playbill: ip/user dejaron de inyectarse como parametros del script.
            // Now lo establece el handler en PerformQry/PerformChk/PerformEmit.
            var p = new Parameters();
            using (FormatterContext.Push(formatterPrototype))
            {
                return hook.PerformQry(script, p);
            }
        }

        // ── Playbill API (V2-only, fluent) ────────────────────────────────

        // Declara un Playbill schema en este Performance. Registra el schema
        // en el PlaybillStore (auto-provisionando el storage si no existe)
        // y lo deja como el schema "actual" para invocaciones subsiguientes
        // via Using(...).
        //
        // Llamado mas de una vez con NOMBRES distintos: cada DefinePlaybill
        // registra su schema; el ultimo queda como el currentSchemaName.
        // Llamado dos veces con el MISMO nombre + misma firma: idempotente.
        // Mismo nombre + firma distinta: LanguageException (drift requiere
        // migracion explicita).
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

        // Variante Playbill-aware del fluent V2. Equivalente a perf.Actor.Using(...)
        // PERO adicionalmente pasa el contexto del Playbill al ActorV2Invocation,
        // habilitando .WithPlaybill(...) y el second-write automatico al persistir.
        //
        // Si el Performance no tiene Playbill configurado, el contexto va null —
        // la invocacion es funcionalmente identica a perf.Actor.Using(script).
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
