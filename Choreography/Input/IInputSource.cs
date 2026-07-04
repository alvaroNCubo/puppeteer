using System;
using System.Collections.Generic;

namespace Choreography.Input
{
	// One raw signal as it arrives off an input medium, BEFORE routing has decided
	// which command it animates. The symmetric mirror of PushDocument on the output
	// side: where PushDocument carries an already-rendered projection OUT to a sink,
	// InputSignal carries an un-interpreted message IN from a source.
	//
	// The MEDIUM is a property of the SOURCE, never of the command: a Kafka record,
	// an InProc loopback record, and a Loopback TCP record all surface here as the
	// same shape. The fields are exactly what a router needs to turn the signal into
	// a command and what the consume-processing needs to de-duplicate it:
	//
	//   * Id        — the source-assigned identity of this signal. The consume side
	//                 keys idempotency on it (so a redelivered record runs once).
	//                 Defaults to the broker key when the medium offers no better id.
	//   * Key       — the partition/ordering key the source carried (Kafka partition
	//                 key, broker record key). Routing may fold it into the command
	//                 or ignore it.
	//   * Headers   — the medium's metadata (correlation, message-name tag, …). Never
	//                 the payload; the payload is Value. Never null (empty when none).
	//   * Value     — the payload exactly as the medium delivered it (UTF-8 string).
	//                 This is the medium-agnostic body the router interprets.
	//
	// A signal carries NO transport route and NO command shape — those belong to the
	// source (medium) and the receiver (routing) respectively, mirroring how the
	// output side keeps pull-vs-push in the destination and the command in `print`.
	public readonly struct InputSignal
	{
		public string Id { get; }
		public string Key { get; }
		public IReadOnlyDictionary<string, string> Headers { get; }
		public string Value { get; }

		public InputSignal(string id, string key, IReadOnlyDictionary<string, string> headers, string value)
		{
			Id = id;
			Key = key;
			Headers = headers ?? EmptyHeaders;
			Value = value ?? string.Empty;
		}

		private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
			new Dictionary<string, string>(0);
	}

	// The INPUT seam — the medium-side sibling of IOutputSink. A source takes
	// messages off a transport (a Kafka topic, an InProcessBroker, a LoopbackBroker,
	// a clock, …) and pushes them, as un-interpreted InputSignals, into a callback.
	//
	// Symmetry with the output side:
	//   * Pull-vs-push is a property of the DESTINATION (IOutputSink); the MEDIUM is
	//     a property of the SOURCE (IInputSource). The command (and `print`) are
	//     medium-agnostic — the same Dispatch/Saga handler runs whether the signal
	//     arrived over Kafka, the in-process broker, or TCP loopback.
	//   * Puppeteer core stays transport-agnostic: it knows only this interface and
	//     InputSignal. A host (or the BrokerInputSource adapter) binds the concrete
	//     medium.
	//
	// An actor is animated by a MERGE of input sources reduced into ONE serial
	// command flow. The serial reducer is the Dispatch work queue: each source is
	// wired with Dispatch.ConsumeFrom(source, routing); several ConsumeFrom calls
	// are the merge. Start is the framework's to call (Dispatch calls it when the
	// source is bound); a host does not call Start itself.
	//
	// Contract:
	//   * Start binds the callback and begins delivery. It returns an IDisposable
	//     whose Dispose stops delivery (unsubscribes the medium). Start may be called
	//     once per source instance; a second call throws.
	//   * onSignal MUST be invoked for every message the medium delivers from the
	//     subscription point onward. A late-bound source over a retained log replays
	//     the backlog (mirrors the brokers' retained-log behaviour).
	//   * onSignal runs on the medium's delivery thread. The reducer (Dispatch)
	//     enqueues and returns; the source must not assume the command has run when
	//     onSignal returns.
	public interface IInputSource
	{
		// A stable label for telemetry/tracing (e.g. the topic name). Never the
		// routing key and never journaled — purely diagnostic, mirroring the
		// transport's WitnessName on the Tell side.
		string SourceName { get; }

		// Begin delivering signals to onSignal. The returned token stops delivery
		// when disposed. Idempotent Dispose.
		IDisposable Start(Action<InputSignal> onSignal);
	}
}
