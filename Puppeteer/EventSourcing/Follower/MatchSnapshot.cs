using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Puppeteer.EventSourcing.Follower
{
	// Phase A: immutable snapshot of one complete match. Captured at the
	// instant ExecuteCompleteMatch detects the chain, before any Action runs.
	// The Reaction keeps a ring buffer (LastMatchesCapacity = 32) of these so
	// callers can assert behavioral properties retrospectively against the
	// journal that was just replayed:
	//   Assert.Equal(123, reaction.LastMatches[^1].Bindings["cliente"]);
	// Bindings are filtered to exclude Now/User/Ip — consistent with the
	// HashParameters8 convention used by the Reactions LabInstrumentation
	// callbacks — so the snapshot reflects domain captures, not wall-clock
	// or identity context.
	public sealed class MatchSnapshot
	{
		public long TriggeringEntryId { get; }
		public DateTime OccurredAt { get; }
		public IReadOnlyList<long> Chain { get; }
		public IReadOnlyDictionary<string, object> Bindings { get; }

		internal MatchSnapshot(long triggeringEntryId, DateTime occurredAt, long[] chain, IDictionary<string, object> bindings)
		{
			ArgumentNullException.ThrowIfNull(chain);
			ArgumentNullException.ThrowIfNull(bindings);

			TriggeringEntryId = triggeringEntryId;
			OccurredAt = occurredAt;

			// Defensive copy of the chain so MatchTree's reusable buffer can
			// be cleared after the snapshot is recorded without affecting it.
			long[] chainCopy = new long[chain.Length];
			Array.Copy(chain, chainCopy, chain.Length);
			Chain = Array.AsReadOnly(chainCopy);

			Bindings = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(bindings));
		}
	}
}
