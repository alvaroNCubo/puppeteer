using System;
using System.Collections.Generic;

namespace Puppeteer
{
	/// <summary>
	/// One projection delivered to the ephemeral push channel. Carries the
	/// immutable document plus the metadata the transport needs to do
	/// delivery-IVM (suppress re-sending what a subscriber already holds — a
	/// reduction of the channel's porosity) and routing.
	///
	/// <para>
	/// Two clocks, answering different questions:
	/// <list type="bullet">
	/// <item><see cref="EntryId"/> — logical clock (monotonic, gap-free,
	/// journal-authoritative). The right key for ordering, dedup ("already
	/// delivered ≤ N → skip") and resync-by-cursor ("send everything > N").</item>
	/// <item><see cref="OccurredAt"/> — wall clock of the triggering event. For
	/// staleness ("hasn't changed in a while"), TTL/coalescing and human-facing
	/// timestamps. NOT for ordering — wall clocks skew and tie.</item>
	/// </list>
	/// </para>
	///
	/// <para>
	/// <see cref="Bindings"/> are the Reaction's match captures (e.g.
	/// <c>name="value"</c>) — the raw material from which a transport composes
	/// its routing/group keys and its per-key dedup identity. The substrate hands
	/// over the bindings; the transport (which knows its own group model —
	/// SignalR groups, subgroups, fan-out) decides how to map them. That keeps
	/// the transport-specific naming where it belongs (the adapter), not in the
	/// DSL.
	/// </para>
	/// </summary>
	public readonly struct PushDocument
	{
		/// <summary>The full projection, serialized to its final immutable form
		/// (the single ToString already produced by the engine).</summary>
		public string Document { get; }

		/// <summary>The name (title) of the Reaction whose <c>Program.Emit</c>
		/// produced this projection. It is the <i>projection-type</i>
		/// discriminator: it does NOT identify the instance (the reaction fires
		/// for many distinct instances, all under the same name). The adapter couples its
		/// routing per reaction — "for <c>ReactionName</c> X, the key is the value
		/// of binding @name" — and uses it to namespace the dedup identity so
		/// two different projections that bind the same dimension do not collide.
		/// </summary>
		public string ReactionName { get; }

		/// <summary>Logical clock of the triggering event (ordering / dedup /
		/// resync).</summary>
		public long EntryId { get; }

		/// <summary>Wall clock of the triggering event (staleness / TTL /
		/// display). Never use for ordering.</summary>
		public DateTime OccurredAt { get; }

		/// <summary>The Reaction's match captures (Now/Ip/User excluded), by name
		/// and value. The instance identity lives here (e.g. <c>name="value"</c>):
		/// the adapter reads the VALUE, since the dimension/name alone is too
		/// coarse for a granular entity. Never null (empty when the match captured
		/// nothing).</summary>
		public IReadOnlyDictionary<string, object> Bindings { get; }

		public PushDocument(string document, string reactionName, long entryId, DateTime occurredAt, IReadOnlyDictionary<string, object> bindings)
		{
			Document = document;
			ReactionName = reactionName;
			EntryId = entryId;
			OccurredAt = occurredAt;
			Bindings = bindings ?? EmptyBindings;
		}

		private static readonly IReadOnlyDictionary<string, object> EmptyBindings =
			new Dictionary<string, object>();
	}

	/// <summary>
	/// Destination for the <b>ephemeral push channel</b> (the implementation arm
	/// of Paper 9 / "distributed observation"). A domain or host adapter
	/// implements this to carry a projection over a transport — a SignalR hub, a
	/// Kora mini-hub, a webhook, etc. Puppeteer stays transport-agnostic: it only
	/// knows this interface and <see cref="PushDocument"/>.
	///
	/// <para>
	/// Pull vs push is a property of the <i>destination</i>, never of
	/// <c>print</c>. The DSL script is identical either way. With no sink
	/// configured the engine is pull-only (the caller reads the result of
	/// <c>PerformCmd</c>/<c>PerformQry</c>). When a sink is configured, a
	/// Reaction's <c>Program.Emit</c> projection — the document
	/// <c>PerformEmit</c> would otherwise discard — is delivered here. The sink is
	/// assembly-agnostic: it is set via <c>OutputTarget(sink)</c> exposed
	/// identically on every Choreography assembly (Performance / Ensemble /
	/// StageManager), because the mechanism lives at the <c>ActorHandler</c>, not
	/// in any one topology.
	/// </para>
	///
	/// <para>
	/// Contract:
	/// <list type="bullet">
	/// <item><see cref="PushDocument.Document"/> is an <b>immutable string</b>:
	/// the engine NEVER hands out its pooled <c>Output</c>/<c>StringBuilder</c>,
	/// so an implementation may safely retain or forward it to an async transport.
	/// (Handing the pooled buffer to an async send would reproduce the documented
	/// double-return corruption — see <c>ExecutionOutput</c>.)</item>
	/// <item><see cref="Push"/> MUST NOT block: it runs on the Reaction's
	/// execution thread, after the actor's read lock has been released.
	/// Fire-and-forget the transport send or enqueue it; the engine does not
	/// await delivery.</item>
	/// <item>This is the <b>ephemeral</b> channel — no journaling, no delivery
	/// guarantee. A dropped push is recoverable by the subscriber re-reading on
	/// reconnect. For durable, exactly-once-recorded delivery use the Outbox
	/// Reaction plane (<c>Reaction.Outbox.Emit(...)</c>) instead.</item>
	/// </list>
	/// </para>
	/// </summary>
	public interface IOutputSink
	{
		/// <summary>
		/// Deliver one projection (with its clocks and match bindings) to the
		/// transport. Called once per Reaction <c>Program.Emit</c> when an output
		/// target is configured and the projection is non-empty.
		/// </summary>
		void Push(in PushDocument document);
	}
}
