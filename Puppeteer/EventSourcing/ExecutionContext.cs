using System;
using System.Collections.Generic;
using System.Linq;

namespace Puppeteer.EventSourcing
{
    public class ExecutionContext
    {
        private readonly Dictionary<Actor, (bool itIsThePresent, DateTime now)> itIsThePresentActors = new();
        public static readonly ExecutionContext Current = new ExecutionContext();

        private ExecutionContext() { }

        public DateTime Now
        {
            get
            {
                var mainActor = itIsThePresentActors.FirstOrDefault();
                if (mainActor.Key == null) return DateTime.MinValue;

                var actorItIsThePresentNow = mainActor.Value;
                return actorItIsThePresentNow.now;
            }
        }

        public bool ItIsThePresent
        {
            get
            {
                var mainActor = itIsThePresentActors.FirstOrDefault();
                if (mainActor.Key == null) return false;

                var actorItIsThePresentNow = mainActor.Value;
                return actorItIsThePresentNow.itIsThePresent;
            }
        }

        internal bool ItIsThePresentFor(string actorName)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(actorName);

            var actor = itIsThePresentActors.Keys.FirstOrDefault(a => a.Name.Equals(actorName, StringComparison.OrdinalIgnoreCase));

            if (actor == null) throw new LanguageException($"Execution context has not been set for the actor '{actorName}'.");
            var actorItIsThePresentNow = itIsThePresentActors[actor];
            return actorItIsThePresentNow.itIsThePresent;
        }

        internal DateTime NowFor(Actor actor)
        {
            ArgumentNullException.ThrowIfNull(actor);
            if (!itIsThePresentActors.TryGetValue(actor, out var actorItIsThePresentNow))
            {
                throw new LanguageException("Execution context has not been set for the actor.");
            }
            return actorItIsThePresentNow.now;
        }

        public void SetContext(DateTime now, bool itIsThePresent, Actor actor)
        {
            ArgumentNullException.ThrowIfNull(actor);
            if (now == DateTime.MinValue) throw new ArgumentNullException(nameof(now));
            if (actor is ActorV2)
                throw new LanguageException("ExecutionContext is not available for ActorV2. Use Reactions / OnRecordWritten for external side-effects.");

            if (!itIsThePresentActors.TryGetValue(actor, out var actorItIsThePresentNow))
            {
                itIsThePresentActors.TryAdd(actor, (itIsThePresent, now));
            }
            else
            {
                if (now < actorItIsThePresentNow.now) throw new LanguageException("Cannot set a past date in the execution context.");
                if (actorItIsThePresentNow.itIsThePresent && !itIsThePresent) throw new LanguageException("Cannot change 'ItIsThePresent' to false after it has been set to true.");
            }

            itIsThePresentActors[actor] = (itIsThePresent, now);
        }

        internal void Clear(Actor actor)
        {
            ArgumentNullException.ThrowIfNull(actor);
            if (actor is ActorV2)
                throw new LanguageException("ExecutionContext is not available for ActorV2.");

            itIsThePresentActors.Remove(actor);
        }
    }
}
