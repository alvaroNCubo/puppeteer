using System;

namespace Puppeteer.EventSourcing.Follower
{
	// The unit handed to DiaryStorage.RecordOutboxWithCheckpoint: everything
	// needed to (a) decide whether this match advances the reaction cursor
	// (CheckpointVector, compared monotonically against the persisted detected
	// cursor exactly like CheckpointCommit drives MarkEventsAsElidedWithCheckpoint)
	// and (b) build the outbox row (destination + payload + idempotency key) that
	// is inserted in the SAME store write as the cursor advance.
	//
	// Parallel to CheckpointCommit, but it carries an outgoing message instead of
	// a list of event ids to elide.
	internal sealed class OutboxCommit
	{
		internal long ReactionId { get; }
		internal long AnchorEntryId { get; }
		internal string Destination { get; }
		internal string Payload { get; }
		internal string IdempotencyKey { get; }
		internal DateTime Timestamp { get; }
		internal CheckpointVector CheckpointVector { get; }

		internal OutboxCommit(
			long reactionId,
			long anchorEntryId,
			string destination,
			string payload,
			string idempotencyKey,
			DateTime timestamp,
			CheckpointVector checkpointVector)
		{
			if (reactionId <= 0) throw new LanguageException($"reactionId must be greater than zero, but was {reactionId}.");
			if (anchorEntryId <= 0) throw new LanguageException($"anchorEntryId must be greater than zero, but was {anchorEntryId}.");
			ArgumentException.ThrowIfNullOrWhiteSpace(destination);
			ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
			ArgumentNullException.ThrowIfNull(checkpointVector);
			if (checkpointVector.Count == 0)
				throw new LanguageException("checkpointVector cannot be empty. At least one checkpoint level must be specified.");

			ReactionId = reactionId;
			AnchorEntryId = anchorEntryId;
			Destination = destination;
			Payload = payload ?? string.Empty;
			IdempotencyKey = idempotencyKey;
			Timestamp = timestamp;
			CheckpointVector = checkpointVector;
		}

		// Deterministic idempotency key: (reactionId : anchorEntryId : seekLevel).
		// Same inputs -> same key, so a re-detected match after a crash/handoff,
		// and any redelivery by the at-least-once relay, all carry this key.
		internal static string BuildIdempotencyKey(long reactionId, long anchorEntryId, int seekLevel)
			=> $"{reactionId}:{anchorEntryId}:{seekLevel}";
	}
}
