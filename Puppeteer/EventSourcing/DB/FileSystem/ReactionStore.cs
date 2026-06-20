using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Puppeteer.EventSourcing.DB.FileSystem
{
	internal sealed class ReactionStore
	{
		private static readonly byte[] MAGIC_REACTIONS = new byte[] { (byte)'P', (byte)'P', (byte)'R', (byte)'X' };
		private static readonly byte[] MAGIC_CHECKPOINTS = new byte[] { (byte)'P', (byte)'P', (byte)'C', (byte)'P' };
		private static readonly byte[] MAGIC_FRONTIERS = new byte[] { (byte)'P', (byte)'P', (byte)'F', (byte)'R' };
		private const ushort FORMAT_VERSION = 1;
		private const int HEADER_SIZE = 10;
		private const int CHECKPOINT_RECORD_SIZE = 28;
		// Resume optimization: frontier record = reactionId(8) + highWater(8) + closedFrontier(8).
		private const int FRONTIER_RECORD_SIZE = 24;

		private readonly string reactionsPath;
		private readonly string checkpointsPath;
		private readonly string frontiersPath;
		private readonly IAtomicFileOperation atomicOp;
		private readonly object storeLock = new();

		// Registry: formattedReaction -> reactionId
		private readonly Dictionary<string, long> reactionRegistry = new();
		private long nextReactionId = 1;

		// Checkpoints: (reactionId, seekLevel) -> (detected, confirmed)
		private readonly Dictionary<(long, int), (long detected, long confirmed)> checkpoints = new();

		// Resume optimization (rediseño de checkpoint, paso 2): dos cursores globales por reaction
		// para cobertura. reactionId -> (highWater, closedFrontier). Persiste en un archivo propio
		// (derivado de checkpointsPath) para no tocar el formato del checkpoint per-seek.
		private readonly Dictionary<long, (long highWater, long closedFrontier)> frontiers = new();

		internal ReactionStore(string reactionsPath, string checkpointsPath, IAtomicFileOperation atomicOp)
		{
			if (reactionsPath == null) throw new ArgumentNullException(nameof(reactionsPath));
			if (checkpointsPath == null) throw new ArgumentNullException(nameof(checkpointsPath));
			if (atomicOp == null) throw new ArgumentNullException(nameof(atomicOp));

			this.reactionsPath = reactionsPath;
			this.checkpointsPath = checkpointsPath;
			this.frontiersPath = checkpointsPath + ".frontiers";
			this.atomicOp = atomicOp;

			LoadReactions();
			LoadCheckpoints();
			LoadFrontiers();
		}

		private void LoadReactions()
		{
			atomicOp.RecoverFromIncompleteOperation(reactionsPath);
			if (!File.Exists(reactionsPath)) return;

			byte[] data = File.ReadAllBytes(reactionsPath);
			if (data.Length < HEADER_SIZE) return;
			if (data[0] != MAGIC_REACTIONS[0] || data[1] != MAGIC_REACTIONS[1] ||
				data[2] != MAGIC_REACTIONS[2] || data[3] != MAGIC_REACTIONS[3]) return;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;

			for (int i = 0; i < count && offset + 12 <= data.Length; i++)
			{
				long id = BitConverter.ToInt64(data, offset); offset += 8;
				int nameLen = BitConverter.ToInt32(data, offset); offset += 4;
				if (offset + nameLen > data.Length) break;
				string name = Encoding.UTF8.GetString(data, offset, nameLen); offset += nameLen;

				reactionRegistry[name] = id;
				if (id >= nextReactionId) nextReactionId = id + 1;
			}
		}

		private void SaveReactions()
		{
			int totalBytes = HEADER_SIZE;
			foreach (var kvp in reactionRegistry)
				totalBytes += 8 + 4 + Encoding.UTF8.GetByteCount(kvp.Key);

			byte[] data = new byte[totalBytes];
			int offset = 0;

			Buffer.BlockCopy(MAGIC_REACTIONS, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), reactionRegistry.Count); offset += 4;

			foreach (var kvp in reactionRegistry)
			{
				byte[] nameBytes = Encoding.UTF8.GetBytes(kvp.Key);
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), kvp.Value); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 4), nameBytes.Length); offset += 4;
				Buffer.BlockCopy(nameBytes, 0, data, offset, nameBytes.Length); offset += nameBytes.Length;
			}

			string tempPath = reactionsPath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, reactionsPath);
		}

		private void LoadCheckpoints()
		{
			atomicOp.RecoverFromIncompleteOperation(checkpointsPath);
			if (!File.Exists(checkpointsPath)) return;

			byte[] data = File.ReadAllBytes(checkpointsPath);
			if (data.Length < HEADER_SIZE) return;
			if (data[0] != MAGIC_CHECKPOINTS[0] || data[1] != MAGIC_CHECKPOINTS[1] ||
				data[2] != MAGIC_CHECKPOINTS[2] || data[3] != MAGIC_CHECKPOINTS[3]) return;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;

			for (int i = 0; i < count && offset + CHECKPOINT_RECORD_SIZE <= data.Length; i++)
			{
				long reactionId = BitConverter.ToInt64(data, offset); offset += 8;
				int seekLevel = BitConverter.ToInt32(data, offset); offset += 4;
				long detected = BitConverter.ToInt64(data, offset); offset += 8;
				long confirmed = BitConverter.ToInt64(data, offset); offset += 8;

				checkpoints[(reactionId, seekLevel)] = (detected, confirmed);
			}
		}

		private void SaveCheckpoints()
		{
			int size = HEADER_SIZE + checkpoints.Count * CHECKPOINT_RECORD_SIZE;
			byte[] data = new byte[size];
			int offset = 0;

			Buffer.BlockCopy(MAGIC_CHECKPOINTS, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), checkpoints.Count); offset += 4;

			foreach (var kvp in checkpoints)
			{
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), kvp.Key.Item1); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 4), kvp.Key.Item2); offset += 4;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), kvp.Value.detected); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), kvp.Value.confirmed); offset += 8;
			}

			string tempPath = checkpointsPath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, checkpointsPath);
		}

		private void LoadFrontiers()
		{
			atomicOp.RecoverFromIncompleteOperation(frontiersPath);
			if (!File.Exists(frontiersPath)) return;

			byte[] data = File.ReadAllBytes(frontiersPath);
			if (data.Length < HEADER_SIZE) return;
			if (data[0] != MAGIC_FRONTIERS[0] || data[1] != MAGIC_FRONTIERS[1] ||
				data[2] != MAGIC_FRONTIERS[2] || data[3] != MAGIC_FRONTIERS[3]) return;

			int count = BitConverter.ToInt32(data, 6);
			int offset = HEADER_SIZE;

			for (int i = 0; i < count && offset + FRONTIER_RECORD_SIZE <= data.Length; i++)
			{
				long reactionId = BitConverter.ToInt64(data, offset); offset += 8;
				long highWater = BitConverter.ToInt64(data, offset); offset += 8;
				long closedFrontier = BitConverter.ToInt64(data, offset); offset += 8;

				frontiers[reactionId] = (highWater, closedFrontier);
			}
		}

		private void SaveFrontiers()
		{
			int size = HEADER_SIZE + frontiers.Count * FRONTIER_RECORD_SIZE;
			byte[] data = new byte[size];
			int offset = 0;

			Buffer.BlockCopy(MAGIC_FRONTIERS, 0, data, offset, 4); offset += 4;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 2), FORMAT_VERSION); offset += 2;
			BitConverter.TryWriteBytes(data.AsSpan(offset, 4), frontiers.Count); offset += 4;

			foreach (var kvp in frontiers)
			{
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), kvp.Key); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), kvp.Value.highWater); offset += 8;
				BitConverter.TryWriteBytes(data.AsSpan(offset, 8), kvp.Value.closedFrontier); offset += 8;
			}

			string tempPath = frontiersPath + ".tmp";
			File.WriteAllBytes(tempPath, data);
			atomicOp.AtomicReplace(tempPath, frontiersPath);
		}

		internal (long highWater, long closedFrontier) GetFrontier(long reactionId)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");

			lock (storeLock)
			{
				return frontiers.TryGetValue(reactionId, out var f) ? f : (0L, 0L);
			}
		}

		internal void SaveFrontier(long reactionId, long highWater, long closedFrontier)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			if (highWater < 0) throw new LanguageException("highWater must be zero or greater.");
			if (closedFrontier < 0) throw new LanguageException("closedFrontier must be zero or greater.");

			lock (storeLock)
			{
				frontiers[reactionId] = (highWater, closedFrontier);
				SaveFrontiers();
			}
		}

		internal long GetOrCreate(string formattedReaction)
		{
			if (formattedReaction == null) throw new ArgumentNullException(nameof(formattedReaction));

			lock (storeLock)
			{
				if (reactionRegistry.TryGetValue(formattedReaction, out long existingId))
					return existingId;

				long newId = nextReactionId++;
				reactionRegistry[formattedReaction] = newId;
				SaveReactions();
				return newId;
			}
		}

		internal (long detected, long confirmed) GetCheckpoint(long reactionId, int seekLevel)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException("seekLevel must be zero or greater.");

			lock (storeLock)
			{
				return checkpoints.TryGetValue((reactionId, seekLevel), out var cp) ? cp : (0L, 0L);
			}
		}

		internal void SaveConfirmed(long reactionId, int seekLevel, long entryId)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException("seekLevel must be zero or greater.");
			if (entryId <= 0) throw new LanguageException($"entryId '{entryId}' must be greater than zero.");

			lock (storeLock)
			{
				var (detected, currentConfirmed) = GetCheckpoint(reactionId, seekLevel);
				if (entryId <= currentConfirmed) return;

				checkpoints[(reactionId, seekLevel)] = (detected, entryId);
				SaveCheckpoints();
			}
		}

		internal void SaveDetected(long reactionId, int seekLevel, long entryId)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException("seekLevel must be zero or greater.");

			lock (storeLock)
			{
				var (_, confirmed) = GetCheckpoint(reactionId, seekLevel);
				checkpoints[(reactionId, seekLevel)] = (entryId, confirmed);
				SaveCheckpoints();
			}
		}

		internal void SaveBoth(long reactionId, int seekLevel, long entryId)
		{
			if (reactionId <= 0) throw new LanguageException("reactionId must be greater than zero.");
			if (seekLevel < 0) throw new LanguageException("seekLevel must be zero or greater.");
			if (entryId <= 0) throw new LanguageException($"entryId '{entryId}' must be greater than zero.");

			lock (storeLock)
			{
				var (currentDetected, _) = GetCheckpoint(reactionId, seekLevel);
				if (entryId <= currentDetected) return;

				checkpoints[(reactionId, seekLevel)] = (entryId, entryId);
				SaveCheckpoints();
			}
		}

		// Materialize v2 / Fase 3: snapshot atomic del registry para wire verb (c).
		internal void ListRegistry(List<MaterializationReactionDefinition> result)
		{
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();
			lock (storeLock)
			{
				foreach (var kvp in reactionRegistry)
				{
					result.Add(new MaterializationReactionDefinition(kvp.Value, kvp.Key));
				}
			}
			result.Sort((a, b) => a.ReactionId.CompareTo(b.ReactionId));
		}

		// Materialize v2 / Fase 3: snapshot atomic de checkpoints para wire verb (c).
		internal void ListCheckpoints(List<MaterializationReactionCheckpoint> result)
		{
			ArgumentNullException.ThrowIfNull(result);

			result.Clear();
			lock (storeLock)
			{
				foreach (var kvp in checkpoints)
				{
					result.Add(new MaterializationReactionCheckpoint(
						reactionId: kvp.Key.Item1,
						seekLevel: kvp.Key.Item2,
						detected: kvp.Value.detected,
						confirmed: kvp.Value.confirmed));
				}
			}
			result.Sort((a, b) =>
			{
				int cmp = a.ReactionId.CompareTo(b.ReactionId);
				if (cmp != 0) return cmp;
				return a.SeekLevel.CompareTo(b.SeekLevel);
			});
		}
	}
}
