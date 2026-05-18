using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Puppeteer.EventSourcing.Follower
{
	internal class CheckpointVector
	{
		private readonly Dictionary<int, long> checkpoints;
		private readonly List<ReactionEngine> reactionEngines;
		private readonly Dictionary<string, int> seekNameToIndex;
		private readonly int maxSeekLevel;

		internal CheckpointVector(List<ReactionEngine> reactionEngines)
		{
			ArgumentNullException.ThrowIfNull(reactionEngines);

			if (reactionEngines.Count == 0)
				throw new LanguageException("reactionEngines cannot be empty. The Reaction must have at least one Seek.");

			this.reactionEngines = reactionEngines;
			this.maxSeekLevel = reactionEngines.Count - 1;
			this.checkpoints = new Dictionary<int, long>();
			this.seekNameToIndex = new Dictionary<string, int>();

			for (int i = 0; i < reactionEngines.Count; i++)
			{
				string seekName = reactionEngines[i].PatternDescription;
				if (string.IsNullOrWhiteSpace(seekName))
					throw new LanguageException($"Seek at index {i} has no name (PatternDescription is null or empty).");

				if (seekNameToIndex.ContainsKey(seekName))
					throw new LanguageException($"Duplicate Seek name: '{seekName}'. Seek/ThenSeek/ThenFinalSeek names must be unique.");

				seekNameToIndex[seekName] = i;
			}
		}

		private CheckpointVector(Dictionary<int, long> checkpointData)
		{
			ArgumentNullException.ThrowIfNull(checkpointData);

			if (checkpointData.Count == 0)
				throw new LanguageException("checkpointData cannot be empty. CheckpointVector must have at least one level.");

			ValidateStructuralIntegrity(checkpointData);

			this.maxSeekLevel = checkpointData.Count - 1;
			this.checkpoints = new Dictionary<int, long>(checkpointData);
			this.reactionEngines = null;
			this.seekNameToIndex = null;
		}

		internal static CheckpointVector FromDictionary(Dictionary<int, long> checkpointData)
		{
			return new CheckpointVector(checkpointData);
		}

		private static void ValidateStructuralIntegrity(Dictionary<int, long> data)
		{
			var sortedLevels = data.Keys.OrderBy(k => k).ToList();

			for (int i = 0; i < sortedLevels.Count; i++)
			{
				int level = sortedLevels[i];
				if (level != i)
				{
					throw new LanguageException(
						$"Checkpoint vector has gaps: expected level {i} but found {level}. " +
						$"Levels must be consecutive starting from 0.");
				}

				long entryId = data[level];
				if (entryId <= 0)
				{
					throw new LanguageException(
						$"Invalid EntryId at level {level}: {entryId}. All entryIds must be greater than zero.");
				}
			}

			for (int i = 0; i < sortedLevels.Count - 1; i++)
			{
				long current = data[sortedLevels[i]];
				long next = data[sortedLevels[i + 1]];

				if (next < current)
				{
					throw new LanguageException(
						$"Checkpoint vector violates monotonicity: " +
						$"level {sortedLevels[i]}={current} > level {sortedLevels[i + 1]}={next}. " +
						$"EntryIds must be monotonically non-decreasing across levels.");
				}
			}
		}

		internal int MaxSeekLevel => maxSeekLevel;

		internal int SeekCount => reactionEngines?.Count ?? (maxSeekLevel + 1);

		internal string GetSeekName(int seekLevel)
		{
			ValidateSeekLevel(seekLevel);

			if (reactionEngines == null)
				throw new LanguageException("This operation is not available on a structural CheckpointVector (one without ReactionEngines).");

			return reactionEngines[seekLevel].PatternDescription;
		}

		internal int GetSeekIndex(string seekName)
		{
			if (string.IsNullOrWhiteSpace(seekName))
				throw new LanguageException("seekName cannot be null or empty.");

			if (seekNameToIndex == null)
				throw new LanguageException("This operation is not available on a structural CheckpointVector (one without ReactionEngines).");

			if (!seekNameToIndex.TryGetValue(seekName, out int index))
				throw new LanguageException($"Seek '{seekName}' does not exist in this Reaction. Valid Seeks: {string.Join(", ", seekNameToIndex.Keys)}.");

			return index;
		}

		internal long this[int seekLevel]
		{
			get
			{
				ValidateSeekLevel(seekLevel);
				return checkpoints.TryGetValue(seekLevel, out long value) ? value : 0;
			}
			set
			{
				ValidateSeekLevel(seekLevel);
				ValidateEntryId(value, seekLevel);
				checkpoints[seekLevel] = value;
			}
		}

		internal void Set(int seekLevel, long entryId)
		{
			ValidateSeekLevel(seekLevel);
			ValidateEntryId(entryId, seekLevel);
			checkpoints[seekLevel] = entryId;
		}

		internal void Set(string seekName, long entryId)
		{
			int seekLevel = GetSeekIndex(seekName);
			Set(seekLevel, entryId);
		}

		internal long Get(int seekLevel)
		{
			ValidateSeekLevel(seekLevel);
			return checkpoints.TryGetValue(seekLevel, out long value) ? value : 0;
		}

		internal long Get(string seekName)
		{
			int seekLevel = GetSeekIndex(seekName);
			return Get(seekLevel);
		}

		internal bool HasCheckpoint(int seekLevel)
		{
			ValidateSeekLevel(seekLevel);
			return checkpoints.ContainsKey(seekLevel);
		}

		internal bool HasCheckpoint(string seekName)
		{
			int seekLevel = GetSeekIndex(seekName);
			return HasCheckpoint(seekLevel);
		}

		internal long GetMinimum()
		{
			if (checkpoints.Count == 0)
				return 0;

			long min = long.MaxValue;
			for (int i = 0; i <= maxSeekLevel; i++)
			{
				if (checkpoints.TryGetValue(i, out long value) && value < min)
				{
					min = value;
				}
			}

			return min == long.MaxValue ? 0 : min;
		}

		internal void ValidateMonotonicity()
		{
			if (checkpoints.Count == 0)
				return;

			long previousEntryId = 0;
			for (int i = 0; i <= maxSeekLevel; i++)
			{
				if (checkpoints.TryGetValue(i, out long currentEntryId))
				{
					if (currentEntryId < previousEntryId)
					{
						throw new LanguageException(
							$"Monotonicity violation in CheckpointVector: " +
							$"checkpoint[{i}]={currentEntryId} < checkpoint[{i - 1}]={previousEntryId}. " +
							$"Checkpoints must be monotonically non-decreasing.");
					}
					previousEntryId = currentEntryId;
				}
			}
		}

		internal Dictionary<int, long> ToDictionary()
		{
			return new Dictionary<int, long>(checkpoints);
		}

		internal void Clear()
		{
			checkpoints.Clear();
		}

		internal int Count => checkpoints.Count;

		public override string ToString()
		{
			if (checkpoints.Count == 0)
				return "[empty]";

			var sb = new StringBuilder();
			sb.Append('[');
			for (int i = 0; i <= maxSeekLevel; i++)
			{
				if (i > 0)
					sb.Append(", ");

				if (reactionEngines != null)
				{
					string seekName = reactionEngines[i].PatternDescription;
					sb.Append($"{seekName}=");
				}
				else
				{
					sb.Append($"level{i}=");
				}

				if (checkpoints.TryGetValue(i, out long value))
					sb.Append(value);
				else
					sb.Append('0');
			}
			sb.Append(']');
			return sb.ToString();
		}

		private void ValidateSeekLevel(int seekLevel)
		{
			if (seekLevel < 0)
				throw new LanguageException($"seekLevel must be 0 or greater, but was {seekLevel}.");

			if (seekLevel > maxSeekLevel)
				throw new LanguageException(
					$"seekLevel {seekLevel} exceeds the maximum allowed {maxSeekLevel}. " +
					$"This Reaction has only {maxSeekLevel + 1} Seek levels.");
		}

		private void ValidateEntryId(long entryId, int seekLevel)
		{
			if (entryId < 0)
				throw new LanguageException(
					$"entryId must be 0 or greater, but was {entryId} for seekLevel {seekLevel}.");
		}
	}
}
