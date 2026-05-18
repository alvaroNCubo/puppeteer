using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Puppeteer;

namespace Choreography.Theater
{
    public class PerformanceV1 : Performance
    {
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

        public string PerformCmd(string script, string ip, string user)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (user == null) throw new ArgumentNullException(nameof(user));

            LastActivity = DateTime.Now;
            return hook.PerformCmd(script, DateTime.Now, ip, user);
        }

        public string PerformCmd(string script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));

            LastActivity = DateTime.Now;
            return hook.PerformCmd(script);
        }

        public string PerformQry(string script)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));

            LastActivity = DateTime.Now;
            var p = new Parameters();
            p.SystemParameter<IpAddress>("Ip", IpAddress.DEFAULT);
            p.SystemParameter<UserInLog>("User", UserInLog.ANONYMOUS);
            p.SystemParameter<DateTime>("Now", DateTime.Now);
            return hook.PerformQry(script, p);
        }
    }
}
