namespace Choreography.Input
{
	// The command a routed signal animates: the unit the consume-processing
	// (Dispatch / Saga) applies. It is exactly the pair Dispatch.Receive already
	// takes — an idempotency id plus the typeId-prefixed raw message the registered
	// handler deserializes. Splitting it out names the ROUTING facet's product: a
	// router's whole job is signal -> DispatchCommand.
	//
	//   * MessageId — keys idempotency on the consume side. A redelivered signal
	//                 with the same id runs once.
	//   * RawMessage — the typeId-prefixed body the handler's Deserialize parses.
	//                  rawMessage[0] is the IDispatchMessage.TypeId; the rest is the
	//                  message-specific payload.
	public readonly struct DispatchCommand
	{
		public string MessageId { get; }
		public string RawMessage { get; }

		public DispatchCommand(string messageId, string rawMessage)
		{
			MessageId = messageId;
			RawMessage = rawMessage;
		}
	}

	// The ROUTING facet of an input source: signal (medium-shaped) -> command
	// (handler-shaped). Where IInputSource is the transport facet (medium -> raw
	// signal), this is the second facet (raw signal -> command/verb). Keeping the
	// two separate is the point of the seam: the medium is a property of the source;
	// the mapping to a command is the receiver's, and the same routing can sit over
	// any medium.
	//
	// Returns null to DROP a signal the actor does not consume (e.g. an ack record
	// on a shared topic, a control frame). A dropped signal is not enqueued and not
	// idempotency-recorded.
	public delegate DispatchCommand? InputRouting(InputSignal signal);
}
