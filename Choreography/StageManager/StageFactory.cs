using System;
using System.Reflection;

namespace Choreography.StageManager
{
    public static class StageFactory
    {
        public static T Create<T>(PerformerId id, string actorName) where T : Stage
        {
            if (string.IsNullOrWhiteSpace(actorName))
                throw new ArgumentNullException(nameof(actorName));

            if (typeof(T) == typeof(StageV1))
                return (T)(Stage)new StageV1(id, actorName);

            if (typeof(T) == typeof(StageV2))
                return (T)(Stage)new StageV2(id, actorName);

            throw new ArgumentException($"Unknown Stage type: {typeof(T).Name}");
        }

        public static T Create<T>(PerformerId id, string actorName, params Assembly[] libraryAssemblies)
            where T : Stage
        {
            if (string.IsNullOrWhiteSpace(actorName))
                throw new ArgumentNullException(nameof(actorName));
            ArgumentNullException.ThrowIfNull(libraryAssemblies);

            if (typeof(T) == typeof(StageV1))
                return (T)(Stage)new StageV1(id, actorName, libraryAssemblies);

            if (typeof(T) == typeof(StageV2))
                return (T)(Stage)new StageV2(id, actorName, libraryAssemblies);

            throw new ArgumentException($"Unknown Stage type: {typeof(T).Name}");
        }
    }
}
