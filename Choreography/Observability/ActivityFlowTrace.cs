using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Choreography.Observability
{
    internal sealed class ActivityFlowTrace : IFlowTrace
    {
        private readonly ActivitySource source;
        private readonly Meter meter;

        private readonly ConcurrentDictionary<string, Counter<long>> counters = new();
        private readonly ConcurrentDictionary<string, Histogram<double>> histograms = new();
        private readonly ConcurrentDictionary<string, long> lastMatchAtUnixMs = new();

        internal ActivityFlowTrace(string serviceName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
            this.source = new ActivitySource(serviceName);
            this.meter = new Meter(serviceName);
        }

        public IFlowSpan StartSpan(string name, string type, string parentContext)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);

            Activity activity;
            if (parentContext != null)
            {
                if (ActivityContext.TryParse(parentContext, null, out var ctx))
                {
                    activity = source.StartActivity(name, ActivityKind.Internal, ctx);
                }
                else
                {
                    Debug.WriteLine($"[ActivityFlowTrace] Unparseable traceContext (likely Elastic-legacy or invalid). Starting new trace. ctx={parentContext}");
                    activity = source.StartActivity(name, ActivityKind.Internal);
                }
            }
            else
            {
                activity = source.StartActivity(name, ActivityKind.Internal);
            }

            if (activity == null)
                return NoOpFlowSpan.Instance;

            activity.SetTag(Tags.FlowType, type);
            return new ActivityFlowSpan(activity);
        }

        public IFlowSpan StartChildSpan(string name, string type)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);

            var activity = source.StartActivity(name, ActivityKind.Internal);
            if (activity == null)
                return NoOpFlowSpan.Instance;

            activity.SetTag(Tags.FlowType, type);
            return new ActivityFlowSpan(activity);
        }

        public IDisposable BeginLoopIteration(string name, string type)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);

            var histogram = histograms.GetOrAdd(name + ".iteration_duration_ms",
                key => meter.CreateHistogram<double>(key, unit: "ms"));

            var sw = Stopwatch.StartNew();
            return new LoopIterationScope(sw, histogram, type);
        }

        public void LoopTick(string name, string type, bool matched, Exception error)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);

            var iterations = counters.GetOrAdd(name + ".iterations_total",
                key => meter.CreateCounter<long>(key));
            iterations.Add(1, new KeyValuePair<string, object>(Tags.FlowType, type));

            if (matched)
            {
                var matches = counters.GetOrAdd(name + ".matches_total",
                    key => meter.CreateCounter<long>(key));
                matches.Add(1, new KeyValuePair<string, object>(Tags.FlowType, type));
                lastMatchAtUnixMs[name] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            if (error != null)
            {
                var errors = counters.GetOrAdd(name + ".errors_total",
                    key => meter.CreateCounter<long>(key));
                errors.Add(1,
                    new KeyValuePair<string, object>(Tags.FlowType, type),
                    new KeyValuePair<string, object>(Tags.ErrorType, error.GetType().Name));
            }
        }

        public void IdleMark(string name, string type)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);

            var marks = counters.GetOrAdd(name + ".idle_marks_total",
                key => meter.CreateCounter<long>(key));
            marks.Add(1, new KeyValuePair<string, object>(Tags.FlowType, type));
        }

        public IDisposable BeginIdle(string name, string type, string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(type);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            var histogram = histograms.GetOrAdd(name + ".idle_duration_ms",
                key => meter.CreateHistogram<double>(key, unit: "ms"));

            var activity = source.StartActivity(name, ActivityKind.Internal);
            if (activity != null)
            {
                activity.SetTag(Tags.FlowType, type);
                activity.SetTag(Tags.Idle, true);
                activity.SetTag(Tags.IdleReason, reason);
            }

            var sw = Stopwatch.StartNew();
            return new IdleScope(sw, histogram, activity, type);
        }

        public void CaptureError(string step, Exception error)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(step);
            ArgumentNullException.ThrowIfNull(error);

            var current = Activity.Current;
            var tags = new ActivityTagsCollection
            {
                { Tags.Step, step },
                { Tags.ErrorType, error.GetType().FullName },
                { Tags.ErrorMessage, error.Message },
                { Tags.ErrorStack, error.ToString() }
            };

            if (current != null)
            {
                current.AddEvent(new ActivityEvent("error", default, tags));
                current.SetStatus(ActivityStatusCode.Error, error.Message);
            }
            else
            {
                using var stub = source.StartActivity(step + ".error", ActivityKind.Internal);
                stub?.AddEvent(new ActivityEvent("error", default, tags));
                stub?.SetStatus(ActivityStatusCode.Error, error.Message);
            }

            var errors = counters.GetOrAdd("errors_total",
                key => meter.CreateCounter<long>(key));
            errors.Add(1,
                new KeyValuePair<string, object>(Tags.Step, step),
                new KeyValuePair<string, object>(Tags.ErrorType, error.GetType().Name));
        }

        private sealed class LoopIterationScope : IDisposable
        {
            private readonly Stopwatch sw;
            private readonly Histogram<double> histogram;
            private readonly string type;
            private int disposed;

            internal LoopIterationScope(Stopwatch sw, Histogram<double> histogram, string type)
            {
                this.sw = sw;
                this.histogram = histogram;
                this.type = type;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                sw.Stop();
                histogram.Record(sw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object>(Tags.FlowType, type));
            }
        }

        private sealed class IdleScope : IDisposable
        {
            private readonly Stopwatch sw;
            private readonly Histogram<double> histogram;
            private readonly Activity activity;
            private readonly string type;
            private int disposed;

            internal IdleScope(Stopwatch sw, Histogram<double> histogram, Activity activity, string type)
            {
                this.sw = sw;
                this.histogram = histogram;
                this.activity = activity;
                this.type = type;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                sw.Stop();
                histogram.Record(sw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object>(Tags.FlowType, type));
                activity?.Dispose();
            }
        }
    }

    internal sealed class ActivityFlowSpan : IFlowSpan
    {
        private readonly Activity activity;
        private int disposed;

        internal ActivityFlowSpan(Activity activity)
        {
            ArgumentNullException.ThrowIfNull(activity);
            this.activity = activity;
        }

        public void SetLabel(string key, string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            activity.SetTag(key, value);
        }

        public void SetLabel(string key, long value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            activity.SetTag(key, value);
        }

        public void SetLabel(string key, double value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            activity.SetTag(key, value);
        }

        public void SetLabel(string key, bool value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            activity.SetTag(key, value);
        }

        public void SetResult(string description)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
            activity.SetTag(Tags.Result, description);
        }

        public void SetOutcome(FlowOutcome outcome)
        {
            switch (outcome)
            {
                case FlowOutcome.Success:
                    activity.SetStatus(ActivityStatusCode.Ok);
                    activity.SetTag(Tags.Outcome, "success");
                    break;
                case FlowOutcome.Failure:
                    activity.SetStatus(ActivityStatusCode.Error);
                    activity.SetTag(Tags.Outcome, "failure");
                    break;
                default:
                    activity.SetStatus(ActivityStatusCode.Unset);
                    activity.SetTag(Tags.Outcome, "unknown");
                    break;
            }
        }

        public string SerializeContext()
        {
            return activity.Id ?? string.Empty;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            activity.Dispose();
        }
    }
}
