using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Choreography.Observability;
using Puppeteer.EventSourcing.Follower;

namespace Choreography.Theater
{
    internal sealed class PerformanceTracer : Tracer
    {
        private static PerformanceTracer instance;
        private static readonly object gate = new object();

        internal static PerformanceTracer Instance
        {
            get
            {
                if (instance != null) return instance;
                lock (gate)
                {
                    instance ??= new PerformanceTracer();
                    return instance;
                }
            }
        }

        private readonly ConcurrentDictionary<TrackedReactionKey, Reaction> trackedReactions = new();

        private readonly SpanGroup spanGroup;
        public SpanGroup Span => spanGroup;

        private PerformanceTracer() : base()
        {
            spanGroup = new SpanGroup(this);
            RegisterObservableMetrics();
        }

        internal void AttachToReaction(string actorName, Reaction reaction)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorName);
            ArgumentNullException.ThrowIfNull(reaction);

            var key = new TrackedReactionKey(actorName, reaction.Name);
            if (!trackedReactions.TryAdd(key, reaction)) return;

            reaction.OnExecutionStopped += OnReactionStopped;
        }

        internal void DetachReaction(string actorName, Reaction reaction)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorName);
            ArgumentNullException.ThrowIfNull(reaction);

            var key = new TrackedReactionKey(actorName, reaction.Name);
            if (trackedReactions.TryRemove(key, out _))
            {
                reaction.OnExecutionStopped -= OnReactionStopped;
            }
        }

        internal void RaiseHydrated(string actorName, bool isFirst)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorName);
            using var s = (isFirst ? spanGroup.FirstHydration : spanGroup.Hydrated).Start();
            s.SetLabel(Tags.ActorName, actorName);
            s.SetOutcome(FlowOutcome.Success);
        }

        internal void RaiseHandoverStarted(string actorName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorName);
            using var s = spanGroup.HandoverStarted.Start();
            s.SetLabel(Tags.ActorName, actorName);
            Puppeteer.LabInstrumentation.OnHandoverStarted?.Invoke(actorName);
        }

        internal void RaiseHandoverCompleted(string actorName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorName);
            using var s = spanGroup.HandoverCompleted.Start();
            s.SetLabel(Tags.ActorName, actorName);
            s.SetOutcome(FlowOutcome.Success);
            Puppeteer.LabInstrumentation.OnHandoverCompleted?.Invoke(actorName);
        }

        internal void RaiseCatchUp(string actorName, long targetEntryId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorName);
            using var s = spanGroup.CatchUp.Start();
            s.SetLabel(Tags.ActorName, actorName);
            s.SetLabel(Tags.EntryId, targetEntryId);
        }

        internal void RaiseUnhandledError(string actorName, Exception ex)
        {
            CaptureError($"Performance.{actorName}.Unhandled", ex);
        }

        private void OnReactionStopped(Reaction r)
        {
            string actorName = "unknown";
            TrackedReactionKey foundKey = default;
            bool found = false;
            foreach (var kv in trackedReactions)
            {
                if (ReferenceEquals(kv.Value, r))
                {
                    actorName = kv.Key.ActorName;
                    foundKey = kv.Key;
                    found = true;
                    break;
                }
            }

            using var s = spanGroup.ReactionStopped.Start();
            s.SetLabel(Tags.ActorName, actorName);
            s.SetLabel(Tags.ReactionName, r.Name);
            s.SetLabel("reaction.action_events_processed", r.ActionEventsProcessed);
            s.SetLabel("reaction.cache_hits", r.CacheHits);
            s.SetLabel("reaction.cache_misses", r.CacheMisses);
            s.SetLabel("reaction.parameters_registered", r.ParametersRegistered);
            s.SetLabel("reaction.action_id_not_found", r.ActionIdNotFoundErrors);
            s.SetLabel("reaction.arguments_deser_errors", r.ArgumentsDeserializationErrors);
            s.SetLabel("reaction.parse_errors", r.ParseErrors);
            s.SetLabel("reaction.parameter_registration_ms", r.ParameterRegistrationTime.TotalMilliseconds);
            s.SetLabel("reaction.last_action_at",
                r.LastActionAt == default ? "never" : r.LastActionAt.ToString("o"));

            bool hadErrors = r.ParseErrors > 0
                          || r.ActionIdNotFoundErrors > 0
                          || r.ArgumentsDeserializationErrors > 0;
            s.SetOutcome(hadErrors ? FlowOutcome.Failure : FlowOutcome.Success);
            s.SetResult($"Reaction '{r.Name}' stopped after {r.ActionEventsProcessed} events");

            if (found)
            {
                trackedReactions.TryRemove(foundKey, out _);
                r.OnExecutionStopped -= OnReactionStopped;
            }
        }

        private void RegisterObservableMetrics()
        {
            var meter = ChoreographyDiagnostics.FrameworkMeter;

            meter.CreateObservableCounter(
                "choreography.reaction.action_events_processed_total",
                ObserveActionEvents,
                unit: "{events}");

            meter.CreateObservableCounter(
                "choreography.reaction.cache_hits_total",
                ObserveCacheHits);

            meter.CreateObservableCounter(
                "choreography.reaction.cache_misses_total",
                ObserveCacheMisses);

            meter.CreateObservableCounter(
                "choreography.reaction.parse_errors_total",
                ObserveParseErrors);

            meter.CreateObservableCounter(
                "choreography.reaction.action_id_not_found_total",
                ObserveActionIdNotFound);

            meter.CreateObservableCounter(
                "choreography.reaction.arguments_deser_errors_total",
                ObserveArgumentsDeserErrors);

            meter.CreateObservableGauge(
                "choreography.reaction.cache_hit_ratio",
                ObserveCacheHitRatio);

            meter.CreateObservableGauge(
                "choreography.reaction.last_action_age_seconds",
                ObserveLastActionAgeSeconds,
                unit: "s");
        }

        private IEnumerable<Measurement<long>> ObserveActionEvents()
            => SnapshotLong(r => r.ActionEventsProcessed);

        private IEnumerable<Measurement<long>> ObserveCacheHits()
            => SnapshotLong(r => r.CacheHits);

        private IEnumerable<Measurement<long>> ObserveCacheMisses()
            => SnapshotLong(r => r.CacheMisses);

        private IEnumerable<Measurement<long>> ObserveParseErrors()
            => SnapshotLong(r => r.ParseErrors);

        private IEnumerable<Measurement<long>> ObserveActionIdNotFound()
            => SnapshotLong(r => r.ActionIdNotFoundErrors);

        private IEnumerable<Measurement<long>> ObserveArgumentsDeserErrors()
            => SnapshotLong(r => r.ArgumentsDeserializationErrors);

        private IEnumerable<Measurement<double>> ObserveCacheHitRatio()
        {
            var measurements = new List<Measurement<double>>();
            foreach (var kv in trackedReactions)
            {
                long hits = kv.Value.CacheHits;
                long misses = kv.Value.CacheMisses;
                long total = hits + misses;
                double ratio = total > 0 ? (double)hits / total : 0.0;
                measurements.Add(new Measurement<double>(ratio, BuildTags(kv.Key)));
            }
            return measurements;
        }

        private IEnumerable<Measurement<double>> ObserveLastActionAgeSeconds()
        {
            var measurements = new List<Measurement<double>>();
            DateTime now = DateTime.UtcNow;
            foreach (var kv in trackedReactions)
            {
                var last = kv.Value.LastActionAt;
                double age = last == default ? -1.0 : (now - last).TotalSeconds;
                measurements.Add(new Measurement<double>(age, BuildTags(kv.Key)));
            }
            return measurements;
        }

        private IEnumerable<Measurement<long>> SnapshotLong(Func<Reaction, long> selector)
        {
            var measurements = new List<Measurement<long>>();
            foreach (var kv in trackedReactions)
            {
                measurements.Add(new Measurement<long>(selector(kv.Value), BuildTags(kv.Key)));
            }
            return measurements;
        }

        private static KeyValuePair<string, object>[] BuildTags(TrackedReactionKey key)
        {
            return new[]
            {
                new KeyValuePair<string, object>(Tags.ActorName, key.ActorName),
                new KeyValuePair<string, object>(Tags.ReactionName, key.ReactionName)
            };
        }

        public sealed class SpanGroup
        {
            private readonly PerformanceTracer t;
            internal SpanGroup(PerformanceTracer t) { this.t = t; }

            public SpanFactory FirstHydration    => t.DefineSpan("Performance.FirstHydration",    "choreography.performance");
            public SpanFactory Hydrated          => t.DefineSpan("Performance.Hydrated",          "choreography.performance");
            public SpanFactory HandoverStarted   => t.DefineSpan("Performance.HandoverStarted",   "choreography.performance");
            public SpanFactory HandoverCompleted => t.DefineSpan("Performance.HandoverCompleted", "choreography.performance");
            public SpanFactory CatchUp           => t.DefineSpan("Performance.CatchUp",           "choreography.performance");
            public SpanFactory ReactionStopped   => t.DefineSpan("Reaction.Stopped",              "choreography.reaction");
        }

        private readonly struct TrackedReactionKey : IEquatable<TrackedReactionKey>
        {
            internal string ActorName { get; }
            internal string ReactionName { get; }

            internal TrackedReactionKey(string actorName, string reactionName)
            {
                ActorName = actorName;
                ReactionName = reactionName;
            }

            public bool Equals(TrackedReactionKey other)
                => ActorName == other.ActorName && ReactionName == other.ReactionName;

            public override bool Equals(object obj) => obj is TrackedReactionKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(ActorName, ReactionName);
        }
    }
}
