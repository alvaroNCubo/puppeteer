using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Puppeteer.EventSourcing.DB
{
	// Paper 5 / Materialize v2 — Phase 2. Type of a materialized journal record.
	// Public because actor.Materialization.ReadRecordsAfter exposes it to the
	// destination side (HTTP proxy in production). Three kinds by journal
	// construction: Script (free DSL code), Invocation (action call by id),
	// Define (action declaration).
	public enum MaterializationRecordKind
	{
		Script,
		Invocation,
		Define
	}

	// Immutable snapshot of a journal record. Wire-format-agnostic — the
	// transport (HTTP/binary/JSON) serializes this. Layer 1 of the wire (records
	// alone, without elision markers or checkpoints — those come via (c) and (d) in
	// Phase 3). For a destination that wants Layer 1 only, this record is the
	// unit transferred via wire verb (a) EnviameDesde.
	//
	// Flattened polymorphism: each kind has its relevant fields and the others
	// stay at default. RecordKind selects the valid branch.
	public readonly struct MaterializationRecord
	{
		public long EntryId { get; }
		public MaterializationRecordKind Kind { get; }
		public DateTime OccurredAt { get; }
		public string Script { get; }
		public int ActionId { get; }
		public string Arguments { get; }
		public string DefineStatementText { get; }
		public string ExposeData { get; }

		internal MaterializationRecord(
			long entryId,
			MaterializationRecordKind kind,
			DateTime occurredAt,
			string script,
			int actionId,
			string arguments,
			string defineStatementText,
			string exposeData)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			this.EntryId = entryId;
			this.Kind = kind;
			this.OccurredAt = occurredAt;
			this.Script = script;
			this.ActionId = actionId;
			this.Arguments = arguments;
			this.DefineStatementText = defineStatementText;
			this.ExposeData = exposeData;
		}
	}

	// Paper 5 / Materialize v2 — Phase 3. An entry of the primary actor's reaction
	// registry, shipped via wire verb (c) DameCheckpointsHasta. The destination
	// receives the formatted reaction (canonical DSL string) and its reactionId
	// assigned by the primary; with this it can rebuild its own local registry
	// with the same mapping.
	public readonly struct MaterializationReactionDefinition
	{
		public long ReactionId { get; }
		public string FormattedReaction { get; }

		internal MaterializationReactionDefinition(long reactionId, string formattedReaction)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			ArgumentNullException.ThrowIfNullOrWhiteSpace(formattedReaction);
			this.ReactionId = reactionId;
			this.FormattedReaction = formattedReaction;
		}
	}

	// State of a reaction's seek level (decision D1 #13: ship AS-IS,
	// atomic snapshot read). detected = match detected and persisted; confirmed
	// = PerformCommand executed successfully. The asymmetry justifies shipping
	// AS-IS without clipping: values ahead of the record watermark cause no harm
	// because GetMinimum + IsCheckpointGreater control the behavior on
	// failover; the matcher does not fabricate non-existent matches.
	public readonly struct MaterializationReactionCheckpoint
	{
		public long ReactionId { get; }
		public int SeekLevel { get; }
		public long Detected { get; }
		public long Confirmed { get; }

		internal MaterializationReactionCheckpoint(long reactionId, int seekLevel, long detected, long confirmed)
		{
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException($"SeekLevel {seekLevel} must be zero or greater.");
			if (detected < 0) throw new LanguageException($"Detected {detected} must be zero or greater.");
			if (confirmed < 0) throw new LanguageException($"Confirmed {confirmed} must be zero or greater.");
			this.ReactionId = reactionId;
			this.SeekLevel = seekLevel;
			this.Detected = detected;
			this.Confirmed = confirmed;
		}
	}

	// Combined atomic snapshot of the reactions state: registry + checkpoints.
	// Result of (c) DameCheckpointsHasta. The destination uses this to
	// rebuild its own local EventElision storage — together with (d) ElidedRange
	// for the concrete markers.
	public readonly struct MaterializationReactionsSnapshot
	{
		public IReadOnlyList<MaterializationReactionDefinition> Reactions { get; }
		public IReadOnlyList<MaterializationReactionCheckpoint> Checkpoints { get; }

		internal MaterializationReactionsSnapshot(
			IReadOnlyList<MaterializationReactionDefinition> reactions,
			IReadOnlyList<MaterializationReactionCheckpoint> checkpoints)
		{
			ArgumentNullException.ThrowIfNull(reactions);
			ArgumentNullException.ThrowIfNull(checkpoints);
			this.Reactions = reactions;
			this.Checkpoints = checkpoints;
		}
	}

	// An elision marker — a journal EntryId marked as elided by a
	// specific reaction. Shipped via wire verb (d) DameElidedRange ordered by
	// (Timestamp, EntryId) — the temporal marking order comes from
	// EventElision.Timestamp + DiaryId tie-break (without an additional
	// MarkingOrder autoincrement, signed decision 2026-05-13 PM: "do not create new concepts").
	public readonly struct MaterializationElisionMarker
	{
		public long EntryId { get; }
		public int ReactionId { get; }
		public DateTime Timestamp { get; }

		internal MaterializationElisionMarker(long entryId, int reactionId, DateTime timestamp)
		{
			if (entryId <= 0) throw new LanguageException($"EntryId {entryId} must be greater than zero.");
			if (reactionId <= 0) throw new LanguageException($"ReactionId {reactionId} must be greater than zero.");
			this.EntryId = entryId;
			this.ReactionId = reactionId;
			this.Timestamp = timestamp;
		}
	}

	// Paper 5 / Materialize v2 — Phase 0 (signed D1 2026-05-13). One row per
	// registered destination; models the presence contract between the primary
	// actor and a symbolic destination (see decision #17). Enables the
	// Materialize-then-Distill invariant of Phase 1: Distill(Until N) fails if
	// some registered destination did not explicitly confirm having received
	// up to N. Phase 0 only covers presence (register/deregister/list); the
	// monotonic watermark (LastConfirmedEntryId via ConfirmoUntil) is exercised
	// in Phase 1.
	//
	// Per-actor-by-construction: cross-ref project_actor_per_db_principle.md
	// — each actor lives in its own DB, no partition column needed.
	//
	// Ontological difference vs EventMaterializationStorage (v1, marker queue):
	// that one accumulates rows (DiaryId, ReactionId, Destination) — N markers per
	// destination. This is a registry: a single row per destination with its
	// delivery state. v1 remains alive as the push notification layer; v2 adds
	// on top of it the transfer contract layer.
	internal abstract class MaterializationCheckpointStorage
	{
		protected readonly string ConnectionString;
		protected readonly IActorEventJournalClient EventJournalClient;

		protected MaterializationCheckpointStorage(IActorEventJournalClient eventJournalClient, string connectionString)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connectionString);
			ArgumentNullException.ThrowIfNull(eventJournalClient);

			this.EventJournalClient = eventJournalClient;
			this.ConnectionString = connectionString;
		}

		// Idempotent: if the destination already exists, no-op (preserves the existing
		// watermark and registeredAtEntryId). Signed decision — the caller must
		// do an explicit Deregister + Register to reset. Returns true if
		// a new row was inserted, false if the destination was already registered.
		protected internal abstract bool Register(string destination, long registeredAtEntryId, DateTime now);
		protected internal abstract Task<bool> RegisterAsync(string destination, long registeredAtEntryId, DateTime now);

		// Unilateral (decision D1 #11): does not fail if the destination does not exist.
		// Returns true if a row was removed, false if there was nothing to remove.
		protected internal abstract bool Deregister(string destination);
		protected internal abstract Task<bool> DeregisterAsync(string destination);

		// For Phase 1: watermark read. If the destination is not
		// registered, lastConfirmed comes out 0 and returns false. Signed decision
		// D1 #14: Max-monotonic — the caller (ConfirmoUntil) will only raise it when
		// the new value is strictly greater.
		protected internal abstract bool TryGetWatermark(string destination, out long lastConfirmedEntryId);
		protected internal abstract Task<(bool found, long lastConfirmedEntryId)> TryGetWatermarkAsync(string destination);

		// Phase 1 — wire verb (b) ConfirmoUntil (decision D1 #14). Max-monotonic
		// idempotent: actor.watermark[destination] = Max(existing, entryId). If the
		// destination is not registered, throws LanguageException (confirm is not
		// allowed for unknown destinations — they would have to be registered first,
		// decisions D1 #11/#12 about forward-fidelity from registration time).
		//
		// Returns true if the watermark advanced (entryId > existing), false if it was
		// a no-op (entryId <= existing). Natural recovery: a retry of (a)(c)(d)(b)
		// recovers correctly because the second ConfirmoUntil with the same
		// entryId is a no-op (decision D1 #14).
		//
		// Also updates ConfirmedAt to the timestamp of the advance (only if it advances).
		protected internal abstract bool ConfirmUntil(string destination, long entryId, DateTime now);
		protected internal abstract Task<bool> ConfirmUntilAsync(string destination, long entryId, DateTime now);

		// Registry snapshot: one row per registered destination. Result
		// is filled in stable order (alphabetical by destination) for
		// determinism in tests.
		protected internal abstract void List(List<MaterializationCheckpointRow> result);
		protected internal abstract Task ListAsync(List<MaterializationCheckpointRow> result);
	}

	// Immutable row to avoid accidental mutation after List(). Snapshot of the
	// state of a registered destination at an instant. Public because the
	// actor.Materialization.List() API exposes it as an element of the result.
	public readonly struct MaterializationCheckpointRow : IEquatable<MaterializationCheckpointRow>
	{
		public string Destination { get; }
		public long RegisteredAtEntryId { get; }
		public long LastConfirmedEntryId { get; }
		public DateTime RegisteredAt { get; }
		public DateTime ConfirmedAt { get; }

		internal MaterializationCheckpointRow(
			string destination,
			long registeredAtEntryId,
			long lastConfirmedEntryId,
			DateTime registeredAt,
			DateTime confirmedAt)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(destination);
			if (registeredAtEntryId < 0) throw new LanguageException($"RegisteredAtEntryId {registeredAtEntryId} must be zero or greater.");
			if (lastConfirmedEntryId < registeredAtEntryId) throw new LanguageException($"LastConfirmedEntryId {lastConfirmedEntryId} cannot be less than RegisteredAtEntryId {registeredAtEntryId}.");

			this.Destination = destination;
			this.RegisteredAtEntryId = registeredAtEntryId;
			this.LastConfirmedEntryId = lastConfirmedEntryId;
			this.RegisteredAt = registeredAt;
			this.ConfirmedAt = confirmedAt;
		}

		public bool Equals(MaterializationCheckpointRow other)
		{
			return string.Equals(Destination, other.Destination, StringComparison.Ordinal)
				&& RegisteredAtEntryId == other.RegisteredAtEntryId
				&& LastConfirmedEntryId == other.LastConfirmedEntryId
				&& RegisteredAt == other.RegisteredAt
				&& ConfirmedAt == other.ConfirmedAt;
		}

		public override bool Equals(object obj) => obj is MaterializationCheckpointRow row && Equals(row);
		public override int GetHashCode() => HashCode.Combine(Destination, RegisteredAtEntryId, LastConfirmedEntryId, RegisteredAt, ConfirmedAt);
	}
}
