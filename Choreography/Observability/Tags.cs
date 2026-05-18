namespace Choreography.Observability
{
    internal static class Tags
    {
        internal const string Step          = "step";
        internal const string Outcome       = "outcome";
        internal const string Result        = "result";
        internal const string Idle          = "flow.idle";
        internal const string IdleReason    = "flow.idle.reason";
        internal const string FlowType      = "flow.type";

        internal const string SagaName      = "choreography.saga.name";
        internal const string SagaStep      = "choreography.saga.step";
        internal const string SagaInstance  = "choreography.saga.instance_key";
        internal const string ActorName     = "choreography.actor.name";
        internal const string MessageId     = "choreography.message.id";
        internal const string MessageType   = "choreography.message.type";
        internal const string ReactionName  = "choreography.reaction.name";
        internal const string StageId       = "choreography.stage.id";
        internal const string EntryId       = "choreography.entry.id";
        internal const string PeerId        = "choreography.peer.id";
        internal const string TransportKind = "choreography.transport.kind";

        internal const string ErrorType    = "exception.type";
        internal const string ErrorMessage = "exception.message";
        internal const string ErrorStack   = "exception.stacktrace";
    }
}
