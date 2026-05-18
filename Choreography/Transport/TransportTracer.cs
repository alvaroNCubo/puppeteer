using System;
using Choreography.Observability;

namespace Choreography.Transport
{
    internal sealed class TransportTracer : Tracer
    {
        private static TransportTracer instance;
        private static readonly object gate = new object();

        internal static TransportTracer Instance
        {
            get
            {
                if (instance != null) return instance;
                lock (gate)
                {
                    instance ??= new TransportTracer();
                    return instance;
                }
            }
        }

        public SpanGroup Span { get; }

        private TransportTracer() : base()
        {
            Span = new SpanGroup(this);
        }

        internal void OnMessageSent(string transportKind, string peerId, string channelPurpose)
        {
            using var s = Span.MessageSent.Start();
            s.SetLabel(Tags.TransportKind, transportKind);
            if (peerId != null) s.SetLabel(Tags.PeerId, peerId);
            if (channelPurpose != null) s.SetLabel("transport.channel_purpose", channelPurpose);
        }

        internal void OnMessageReceived(string transportKind, string peerId, string channelPurpose)
        {
            using var s = Span.MessageReceived.Start();
            s.SetLabel(Tags.TransportKind, transportKind);
            if (peerId != null) s.SetLabel(Tags.PeerId, peerId);
            if (channelPurpose != null) s.SetLabel("transport.channel_purpose", channelPurpose);
        }

        internal void OnHandshakeCompleted(string transportKind, string peerId)
        {
            using var s = Span.HandshakeCompleted.Start();
            s.SetLabel(Tags.TransportKind, transportKind);
            if (peerId != null) s.SetLabel(Tags.PeerId, peerId);
            s.SetOutcome(FlowOutcome.Success);
        }

        public sealed class SpanGroup
        {
            private readonly TransportTracer t;
            internal SpanGroup(TransportTracer t) { this.t = t; }

            public SpanFactory MessageSent        => t.DefineSpan("Transport.MessageSent",        "choreography.transport");
            public SpanFactory MessageReceived    => t.DefineSpan("Transport.MessageReceived",    "choreography.transport");
            public SpanFactory HandshakeCompleted => t.DefineSpan("Transport.HandshakeCompleted", "choreography.transport");
        }
    }
}
