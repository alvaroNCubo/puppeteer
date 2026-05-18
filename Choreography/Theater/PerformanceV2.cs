using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Choreography.Dispatch;
using Choreography.Saga;
using Puppeteer;

namespace Choreography.Theater
{
    public class PerformanceV2 : Performance
    {
        private readonly ActorV2 actorV2;

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

        protected override Actor CreateActor(string actorName)
        {
            return LibraryAssemblies != null && LibraryAssemblies.Length > 0
                ? new ActorV2(actorName, LibraryAssemblies)
                : new ActorV2(actorName);
        }

        public ActorV2 Actor => actorV2;

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
