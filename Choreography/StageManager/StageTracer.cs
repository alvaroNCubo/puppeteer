using System;
using Choreography.Observability;

namespace Choreography.StageManager
{
    internal sealed class StageTracer : Tracer
    {
        private static StageTracer instance;
        private static readonly object gate = new object();

        internal static StageTracer Instance
        {
            get
            {
                if (instance != null) return instance;
                lock (gate)
                {
                    instance ??= new StageTracer();
                    return instance;
                }
            }
        }

        public SpanGroup Span { get; }

        private StageTracer() : base()
        {
            Span = new SpanGroup(this);
        }

        internal void OnDirectorElectionStarted(string stageId)
        {
            using var s = Span.DirectorElection.Start();
            s.SetLabel(Tags.StageId, stageId);
        }

        internal void OnBecameDirector(string stageId)
        {
            using var s = Span.BecameDirector.Start();
            s.SetLabel(Tags.StageId, stageId);
            s.SetOutcome(FlowOutcome.Success);
        }

        internal void OnBecameCast(string stageId, string directorId)
        {
            using var s = Span.BecameCast.Start();
            s.SetLabel(Tags.StageId, stageId);
            if (directorId != null) s.SetLabel("stage.director_id", directorId);
            s.SetOutcome(FlowOutcome.Success);
        }

        internal void OnEntryReplicated(string stageId, long entryId)
        {
            using var s = Span.EntryReplicated.Start();
            s.SetLabel(Tags.StageId, stageId);
            s.SetLabel(Tags.EntryId, entryId);
            s.SetOutcome(FlowOutcome.Success);
        }

        internal void OnConnectivityChanged(string stageId, string status)
        {
            using var s = Span.ConnectivityChanged.Start();
            s.SetLabel(Tags.StageId, stageId);
            s.SetLabel("stage.connectivity", status);
        }

        public sealed class SpanGroup
        {
            private readonly StageTracer t;
            internal SpanGroup(StageTracer t) { this.t = t; }

            public SpanFactory DirectorElection    => t.DefineSpan("Stage.DirectorElection",    "choreography.stage");
            public SpanFactory BecameDirector      => t.DefineSpan("Stage.BecameDirector",      "choreography.stage");
            public SpanFactory BecameCast          => t.DefineSpan("Stage.BecameCast",          "choreography.stage");
            public SpanFactory EntryReplicated     => t.DefineSpan("Stage.EntryReplicated",     "choreography.stage");
            public SpanFactory ConnectivityChanged => t.DefineSpan("Stage.ConnectivityChanged", "choreography.stage");
        }
    }
}
