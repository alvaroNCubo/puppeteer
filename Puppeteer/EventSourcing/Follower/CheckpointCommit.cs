using System;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Follower
{
	internal class CheckpointCommit
	{
		private readonly long[] eventIds;
		private readonly long reactionId;
		private readonly DateTime timestamp;
		private readonly CheckpointVector checkpointVector;

		internal CheckpointCommit(
			long[] eventIds,
			long reactionId,
			DateTime timestamp,
			CheckpointVector checkpointVector)
		{
			ArgumentNullException.ThrowIfNull(eventIds);
			ArgumentNullException.ThrowIfNull(checkpointVector);

			if (eventIds.Length == 0)
				throw new LanguageException("eventIds cannot be empty. At least one event must be marked for elision.");

			if (reactionId <= 0)
				throw new LanguageException($"reactionId must be greater than zero, but was {reactionId}");

			if (checkpointVector.Count == 0)
				throw new LanguageException("checkpointVector cannot be empty. At least one checkpoint level must be specified.");

			ValidateNoDuplicateEventIds(eventIds);
			// For Many there can be more eventIds than checkpoint levels.
			// Only validate that all checkpoint levels are present.
			ValidateCheckpointLevelsPresent(checkpointVector);

			this.eventIds = (long[])eventIds.Clone();
			this.reactionId = reactionId;
			this.timestamp = timestamp;
			this.checkpointVector = checkpointVector;
		}

		internal long[] EventIds => eventIds;
		internal long ReactionId => reactionId;
		internal DateTime Timestamp => timestamp;
		internal CheckpointVector CheckpointVector => checkpointVector;

		internal static CheckpointCommit FromMatchChain(
			MatchNode leafNode,
			long reactionId,
			DateTime timestamp)
		{
			ArgumentNullException.ThrowIfNull(leafNode);

			if (reactionId <= 0)
				throw new LanguageException($"reactionId must be greater than zero, but was {reactionId}");

			List<long> eventIdsList = new List<long>();
			CollectEventIdsFromChain(leafNode, eventIdsList);

			if (eventIdsList.Count == 0)
				throw new LanguageException("Match chain is empty. Cannot create CheckpointCommit from empty chain.");

			// Build the checkpoint vector: one level per node in the chain (not per event).
			// For Many there can be more eventIds than levels.
			List<long> checkpointLevelIds = new List<long>();
			CollectCheckpointLevelIds(leafNode, checkpointLevelIds);

			Dictionary<int, long> checkpointDict = new Dictionary<int, long>();
			for (int i = 0; i < checkpointLevelIds.Count; i++)
			{
				checkpointDict[i] = checkpointLevelIds[i];
			}

			CheckpointVector checkpointVector = CheckpointVector.FromDictionary(checkpointDict);

			return new CheckpointCommit(
				eventIdsList.ToArray(),
				reactionId,
				timestamp,
				checkpointVector);
		}

		private static void CollectCheckpointLevelIds(MatchNode node, List<long> levelIds)
		{
			if (node == null) return;
			CollectCheckpointLevelIds(node.Parent, levelIds);

			// For Many, use the last accumulated ID as the level's checkpoint.
			if (node.AccumulatedEventIds != null && node.AccumulatedEventIds.Count > 0)
			{
				levelIds.Add(node.AccumulatedEventIds[node.AccumulatedEventIds.Count - 1]);
			}
			else if (node.Engine != null && node.Engine.IsExact)
			{
				// K.2: Exact with zero accumulated — the level's checkpoint is the
				// parent anchor (there is no own match to index). Defensive: if
				// there is no parent, fall back to EntryId, which will be 0 for eager-creation.
				if (node.Parent != null && node.Parent.AccumulatedEventIds != null && node.Parent.AccumulatedEventIds.Count > 0)
				{
					levelIds.Add(node.Parent.AccumulatedEventIds[node.Parent.AccumulatedEventIds.Count - 1]);
				}
				else if (node.Parent != null)
				{
					levelIds.Add(node.Parent.EntryId);
				}
				else
				{
					levelIds.Add(node.EntryId);
				}
			}
			else
			{
				levelIds.Add(node.EntryId);
			}
		}

		private static void CollectEventIdsFromChain(MatchNode node, List<long> eventIds)
		{
			if (node == null) return;

			CollectEventIdsFromChain(node.Parent, eventIds);

			// If the node belongs to a Many with accumulated IDs, include them all.
			if (node.AccumulatedEventIds != null && node.AccumulatedEventIds.Count > 0)
			{
				foreach (var accumulatedId in node.AccumulatedEventIds)
				{
					if (!eventIds.Contains(accumulatedId))
					{
						eventIds.Add(accumulatedId);
					}
				}
			}
			else if (node.Engine != null && node.Engine.IsExact)
			{
				// K.2: Exact with zero accumulated (e.g. None that closed vacuously)
				// contributes no EntryId to the chain.
			}
			else
			{
				eventIds.Add(node.EntryId);
			}
		}

		private static void ValidateNoDuplicateEventIds(long[] eventIds)
		{
			var seen = new HashSet<long>();
			foreach (var id in eventIds)
			{
				if (id <= 0)
				{
					throw new LanguageException($"Invalid eventId: {id}. All eventIds must be greater than zero.");
				}

				if (!seen.Add(id))
				{
					throw new LanguageException($"Duplicate eventId found: {id}. All eventIds must be unique.");
				}
			}
		}

		private static void ValidateCheckpointLevelsPresent(CheckpointVector vector)
		{
			for (int i = 0; i < vector.Count; i++)
			{
				if (!vector.HasCheckpoint(i))
				{
					throw new LanguageException(
						$"Checkpoint vector is missing level {i}. All levels from 0 to {vector.Count - 1} must be present.");
				}
			}
		}

		private static void ValidateCheckpointLevelsMatchEventCount(CheckpointVector vector, long[] eventIds)
		{
			if (vector.Count != eventIds.Length)
			{
				throw new LanguageException(
					$"Checkpoint vector levels ({vector.Count}) must match event count ({eventIds.Length}). " +
					$"Each event must have a corresponding checkpoint level.");
			}

			for (int i = 0; i < eventIds.Length; i++)
			{
				if (!vector.HasCheckpoint(i))
				{
					throw new LanguageException(
						$"Checkpoint vector is missing level {i}. All levels from 0 to {eventIds.Length - 1} must be present.");
				}

				if (vector.Get(i) != eventIds[i])
				{
					throw new LanguageException(
						$"Checkpoint vector mismatch at level {i}: checkpoint={vector.Get(i)} but eventId={eventIds[i]}. " +
						$"They must match for correct correlation.");
				}
			}
		}

		public override string ToString()
		{
			return $"CheckpointCommit[ReactionId={reactionId}, Events={eventIds.Length}, " +
				   $"Vector={checkpointVector}]";
		}
	}
}
