using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Puppeteer;
using Puppeteer.EventSourcing.Interpreter.Formatters;
using Puppeteer.EventSourcing.Playbill;

namespace Choreography.Theater
{
    public class PerformanceV1 : Performance
    {
        // Fase 4.5 backward compat: V1 tambien soporta Playbill para preservar
        // la capacidad de auditar ip/user (que historicamente vivian como
        // columnas Ip/[User] del journal y ahora viven en el sidecar Playbill).
        // Mismo patron que V2 — auto-provision, schema registry, second-write.
        private Playbill playbill;
        private string currentPlaybillSchemaName;
        // Conveniencia: si el caller no provee libraries, se asume el assembly desde
        // donde se invoco el ctor. Util cuando dominio e interfaz viven en el mismo
        // proyecto. El path idiomatico es pasar las DLLs de dominio explicitamente
        // (ver el ctor con params Assembly[]).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public PerformanceV1(string actorName)
            : this(actorName, new[] { Assembly.GetCallingAssembly() })
        {
        }

        public PerformanceV1(string actorName, params Assembly[] libraryAssemblies)
            : base(actorName, libraryAssemblies)
        {
        }

        protected override Actor CreateActor(string actorName)
        {
            return LibraryAssemblies != null && LibraryAssemblies.Length > 0
                ? new ActorV1(actorName, LibraryAssemblies)
                : new ActorV1(actorName);
        }

        internal ActorV1 GetActorV1() => (ActorV1)ActorInstance;

        // Shadow del Logger base para preservar PerformanceV1 en la cadena fluent.
        public new PerformanceV1 Logger(IPuppeteerLogger logger)
        {
            base.Logger(logger);
            return this;
        }

        // B.3.4: configure automatic Script → Action promotion threshold for
        // legacy V1 endpoints hosted by this PerformanceV1. Delegates to the
        // underlying ActorV1.InternalAutomaticPromotion; see ActorV1 for the
        // mechanism rationale. Per Alvaro's signed shape "p.InternalAutomaticPromotion(5)".
        public PerformanceV1 InternalAutomaticPromotion(int threshold)
        {
            GetActorV1().InternalAutomaticPromotion(threshold);
            return this;
        }

        // PerformanceV1 is the legacy actor surface; per Alvaro firma 2026-05-19
        // it does NOT expose Formatter API and always emits JSON (the legacy
        // contract that EvalStatement V1 relies on for its {} slice mechanic).
        // We defensively push a null context so any outer V2 context does not
        // bleed into V1 execution.

        public string PerformCmd(string script, string ip, string user)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (user == null) throw new ArgumentNullException(nameof(user));

            LastActivity = DateTime.Now;
            using (FormatterContext.Push(null))
            {
                return hook.PerformCmd(script, DateTime.Now, ip, user);
            }
        }

        public string PerformCmd(string script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));

            LastActivity = DateTime.Now;
            using (FormatterContext.Push(null))
            {
                return hook.PerformCmd(script);
            }
        }

        public string PerformQry(string script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));

            LastActivity = DateTime.Now;
            // Fase 4.5 refactor Playbill: ip/user dejaron de inyectarse como parametros del script.
            var p = new Parameters();
            using (FormatterContext.Push(null))
            {
                return hook.PerformQry(script, p);
            }
        }

        // ── Playbill API (V1 backward compat — Fase 4.5) ────────────────────

        public PerformanceV1 Playbill(string schemaName, Action<PlaybillSchemaBuilder> build)
        {
            if (schemaName == null) throw new ArgumentNullException(nameof(schemaName));
            if (build == null) throw new ArgumentNullException(nameof(build));
            if (!storageConfigured) throw new InvalidOperationException("Storage not configured. Call ConfigureStorage before Playbill.");

            if (playbill == null)
            {
                playbill = new Playbill(dbType, connectionString, Name, ActorInstance.Logger);
            }

            var builder = new PlaybillSchemaBuilder();
            build(builder);
            string declarations = builder.BuildDeclarations();
            playbill.RegisterSchema(schemaName, declarations);
            currentPlaybillSchemaName = schemaName;
            return this;
        }

        // Playbill-aware fluent invocation entry para V1. Como V1 necesita
        // ip/user para concatenarlos al script (domain values), se pasan
        // explicitos en Using. WithPlaybill agrega audit values en paralelo;
        // el dev puede usar los mismos valores que ip/user o distintos.
        public PerformanceV1Invocation Using(string script, string ip, string user)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (user == null) throw new ArgumentNullException(nameof(user));
            return new PerformanceV1Invocation(this, script, ip, user, playbill, currentPlaybillSchemaName);
        }

        // Internal accessor used by PerformanceV1Invocation to delegate the
        // actual journal write (preserves the FormatterContext push + legacy
        // PerformCmd path).
        internal string PerformCmdInternal(string script, string ip, string user)
        {
            LastActivity = DateTime.Now;
            using (FormatterContext.Push(null))
            {
                return hook.PerformCmd(script, DateTime.Now, ip, user);
            }
        }
    }
}
