using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Puppeteer.EventSourcing.Follower
{
	internal class MatchNode
	{
		internal long EntryId { get; set; }
		internal DateTime OccurredAt { get; set; }
		internal string Ip { get; set; }
		internal string User { get; set; }
		internal Parameters CapturedParams { get; set; }
		internal ReactionEngine Engine { get; set; }
		internal int CurrentDepth { get; set; }
		internal List<MatchNode> Children { get; set; }
		internal MatchNode Parent { get; set; }
		internal long LastExpansionAttemptEntryId { get; set; }

		// RepeatSeek: acumulacion de IDs de eventos matcheados en este nivel
		internal List<long> AccumulatedEventIds { get; set; }
		internal int AccumulatedCount { get; set; }

		internal MatchNode()
		{
			Children = new List<MatchNode>();
		}
		internal void Clear()
		{
			EntryId = 0;
			OccurredAt = default;
			Ip = null;
			User = null;
			CapturedParams = null;
			Engine = null;
			CurrentDepth = 0;
			Children.Clear();
			if (Children.Capacity > 4)
				Children.TrimExcess();
			Parent = null;
			LastExpansionAttemptEntryId = 0;
			AccumulatedEventIds = null;
			AccumulatedCount = 0;
		}

		internal void AccumulateEventId(long entryId)
		{
			if (AccumulatedEventIds == null)
				AccumulatedEventIds = new List<long>();
			AccumulatedEventIds.Add(entryId);
			AccumulatedCount++;
		}
	}
	internal class MatchNodePool
	{
		private readonly Queue<MatchNode> pool = new Queue<MatchNode>();
		private readonly int maxSize;
		private int count;

		internal MatchNodePool(int maxSize = 1024)
		{
			if (maxSize <= 0) throw new ArgumentException("Max size must be greater than zero.");
			this.maxSize = maxSize;
		}

		internal MatchNode Rent()
		{
			if (pool.Count > 0)
			{
				return pool.Dequeue();
			}
			count++;
			return new MatchNode();
		}

		internal void Return(MatchNode node)
		{
			if (node == null) return;

			node.Clear();
			if (count <= maxSize)
				pool.Enqueue(node);
			else
				count--;
		}
	}
	internal class MatchTree
	{
		private readonly List<MatchNode> roots;
		private readonly MatchNodePool nodePool;
		private readonly ActorHandler actorHandler;
		private readonly ReactionAction reactionAction;
		private readonly HydrationMode hydrationMode;
		private readonly int untilSeekIndex;
		private readonly int maxDepth;
		private readonly int staleNodeThreshold;
		private readonly CheckpointVector checkpointVector;
		private readonly DB.DiaryStorage diaryStorage;
		private readonly long reactionId;

		private long totalNodesPruned = 0;
		private long totalNodesCreated = 0;

		// Buffers reutilizables para evitar alocaciones en hot paths
		private readonly Dictionary<int, long> _checkpointBuffer = new Dictionary<int, long>();
		private readonly List<MatchNode> _nodesAtDepthBuffer = new List<MatchNode>();
		private readonly List<MatchNode> _staleNodesBuffer = new List<MatchNode>();

		internal MatchTree(ActorHandler actorHandler, ReactionAction reactionAction, HydrationMode hydrationMode, int untilSeekIndex, CheckpointVector checkpointVector = null, DB.DiaryStorage diaryStorage = null, long reactionId = 0, int poolSize = 1024, int maxDepth = 100, int staleNodeThreshold = 1000)
		{
			ArgumentNullException.ThrowIfNull(actorHandler);
			ArgumentNullException.ThrowIfNull(reactionAction);

			if (untilSeekIndex < -1) throw new ArgumentException("untilSeekIndex must be -1 or greater.", nameof(untilSeekIndex));
			if (poolSize <= 0) throw new ArgumentException("poolSize must be greater than zero.", nameof(poolSize));
			if (maxDepth <= 0) throw new ArgumentException("maxDepth must be greater than zero.", nameof(maxDepth));
			if (staleNodeThreshold <= 0) throw new ArgumentException("staleNodeThreshold must be greater than zero.", nameof(staleNodeThreshold));

			this.actorHandler = actorHandler;
			this.reactionAction = reactionAction;
			this.hydrationMode = hydrationMode;
			this.untilSeekIndex = untilSeekIndex;
			this.maxDepth = maxDepth;
			this.staleNodeThreshold = staleNodeThreshold;
			this.checkpointVector = checkpointVector;
			this.diaryStorage = diaryStorage;
			this.reactionId = reactionId;
			this.roots = new List<MatchNode>();
			this.nodePool = new MatchNodePool(poolSize);
		}

		internal long TotalNodesPruned => totalNodesPruned;
		internal long TotalNodesCreated => totalNodesCreated;
		internal void TryMatchAtLevel(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram = null)
		{
			ArgumentNullException.ThrowIfNull(eventData);
			ArgumentNullException.ThrowIfNull(engines);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(script);
			ArgumentNullException.ThrowIfNull(symbolTable);

			// Check the depth limit.
			if (level >= maxDepth)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchTree.Optimization] Max depth {maxDepth} reached at level {level}, skipping");
#endif
				return;
			}

			// Decide which mode applies at this level.
			HydrationMode effectiveMode = GetEffectiveModeForLevel(level);

			// Select the strategy based on the effective mode for this level.
			if (effectiveMode == HydrationMode.Shared)
			{
				ProcessBreadthFirst(level, eventData, engines, script, symbolTable, cachedProgram);

				// Prune stale nodes in BFS.
				PruneStaleNodes(eventData.EntryId);
			}
			else // HydrationMode.Independent
			{
				ProcessDepthFirst(level, eventData, engines, script, symbolTable, cachedProgram);
			}
		}

		private HydrationMode GetEffectiveModeForLevel(int level)
		{
			HydrationMode effectiveMode;

			// If untilSeekIndex is -1, there is no transition; use the primary mode at every level.
			if (untilSeekIndex < 0)
			{
				effectiveMode = hydrationMode;
			}
			// The specified mode applies up to and including the Seek indicated by untilSeekIndex.
			else if (level <= untilSeekIndex)
			{
				effectiveMode = hydrationMode;
			}
			else
			{
				// After the indicated Seek, switch to the alternate mode.
				effectiveMode = hydrationMode == HydrationMode.Shared ? HydrationMode.Independent : HydrationMode.Shared;
			}

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[MatchTree.GetEffectiveModeForLevel] level={level}, untilSeekIndex={untilSeekIndex}, primaryMode={hydrationMode}, effectiveMode={effectiveMode}");
#endif

			return effectiveMode;
		}

		private void ProcessBreadthFirst(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram)
		{
			if (level == 0)
			{
				TryMatchAtRoot(eventData, engines, script, symbolTable, cachedProgram);
			}
			else
			{
				// Intermediate level: try to expand existing matches.
				ExpandExistingMatches(level, eventData, engines, script, symbolTable, cachedProgram);
			}
		}

		private void ProcessDepthFirst(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram)
		{
			// DFS (Depth-First Search): depth-first strategy.
			// - When a match is found, immediately try to descend to the next levels.
			// - Early pruning: if a level fails, the whole branch is discarded.
			// - Does not keep multiple branches active simultaneously (unlike BFS).
			//
			// NOTE ON REHYDRATION:
			// The current implementation uses the same SymbolTable at every level (stateless).
			// In the future, when stateful Reactions are introduced:
			// - Each DFS branch should have its own actor rehydration.
			// - A pool of rehydration contexts would be required.
			// - Each node would store a reference to its rehydration context.

			if (level == 0)
			{
				// At level 0, create root nodes the same way BFS does.
				TryMatchAtRoot(eventData, engines, script, symbolTable, cachedProgram);
			}
			else
			{
				// At intermediate levels, try to expand nodes from the previous level
				// but process each branch fully before moving on to the next one.
				ExpandExistingMatchesDepthFirst(level, eventData, engines, script, symbolTable, cachedProgram);
			}

			// Prune stale nodes in DFS (same as BFS).
			// Without this, partial matches at intermediate levels persist indefinitely.
			PruneStaleNodes(eventData.EntryId);
		}

		private void ExpandExistingMatchesDepthFirst(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram)
		{
			// Check that the engine for this level exists.
			if (level >= engines.Count) return;

			var engine = engines[level];
			if (engine.Patterns.Count == 0) return;

			int previousLevel = level - 1;
			var nodesToExpand = GetNodesAtDepth(previousLevel);

			// DFS: process each node independently.
			// Instead of expanding all nodes in parallel (BFS),
			// we fully process each branch before moving on to the next.
			foreach (var node in nodesToExpand)
			{
				// Do not expand a node with the same event that created it.
				if (node.EntryId == eventData.EntryId)
				{
					continue;
				}

				var parameters = actorHandler.ParametersPool.Rent();
				try
				{
					// Copy the accumulated parameters from the parent.
					CopyParameters(node.CapturedParams, parameters);

					bool allMatched = true;

					// All engine patterns must match against the SAME script.
					for (int i = 0; i < engine.Patterns.Count; i++)
					{
						var pattern = engine.Patterns[i];
						bool matched = pattern.Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, eventData.ExposeData);
						if (!matched)
						{
							allMatched = false;
							break;
						}
					}

					if (allMatched && engine.HasWhere)
					{
						if (!EvaluateWhere(engine, parameters, symbolTable, eventData, node))
						{
							allMatched = false;
						}
					}

					if (allMatched)
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchTree.DFS] EXPAND MATCH at level={level}, EntryId={eventData.EntryId}, Parent={node.EntryId}");
#endif

						// Create the child node.
						var child = nodePool.Rent();
						child.EntryId = eventData.EntryId;
						child.OccurredAt = eventData.OccurredAt;
						child.Ip = eventData.Ip;
						child.User = eventData.User;
						child.CapturedParams = parameters;
						child.Engine = engine;
						child.CurrentDepth = level;
						child.Parent = node;
						child.LastExpansionAttemptEntryId = eventData.EntryId;

						node.Children.Add(child);
						totalNodesCreated++;

						// Check whether this is the last engine (complete match).
						if (level == engines.Count - 1)
						{
							bool isFinalSeek = engine.IsFinalSeek;
#if DEBUG
							System.Diagnostics.Debug.WriteLine($"[MatchTree.DFS] COMPLETE MATCH at level={level}, EntryId={eventData.EntryId}, IsFinalSeek={isFinalSeek}");
#endif
							ExecuteCompleteMatch(child);
						}
					}
					else
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchTree.DFS] NO EXPAND MATCH at level={level}, EntryId={eventData.EntryId}, Parent={node.EntryId}");
#endif
						// Early pruning: no match, return parameters to the pool.
						parameters.PurgeUserParameters();
						actorHandler.ParametersPool.Return(parameters);
					}
				}
				catch
				{
					parameters.PurgeUserParameters();
					actorHandler.ParametersPool.Return(parameters);
					throw;
				}
			}
		}

		private void TryMatchAtRoot(EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram)
		{
			// Only process the first engine (level 0).
			if (engines.Count == 0) return;

			var engine = engines[0];
			if (engine.Patterns.Count == 0) return;

			// RepeatSeek at root level: try to accumulate into existing nodes.
			if (engine.IsRepeatSeek)
			{
				TryAccumulateOnRepeatSeekRoots(eventData, engines, engine, script, symbolTable, cachedProgram);
				return;
			}

			var parameters = actorHandler.ParametersPool.Rent();
			try
			{
				bool allMatched = true;

				// All engine patterns must match against the SAME script.
				for (int i = 0; i < engine.Patterns.Count; i++)
				{
					var pattern = engine.Patterns[i];
					bool matched = pattern.Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, eventData.ExposeData);
					if (!matched)
					{
						allMatched = false;
						break;
					}
				}

				if (allMatched && engine.HasWhere)
				{
					// Level 0: there is no parent. SeekName.@X should not appear in the first Seek.
					if (!EvaluateWhere(engine, parameters, symbolTable, eventData, null))
					{
						allMatched = false;
					}
				}

				if (allMatched)
				{
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchTree] ROOT MATCH at EntryId={eventData.EntryId}, EngineCount={engines.Count}");
#endif

					// Create the new root node.
					var node = nodePool.Rent();
					node.EntryId = eventData.EntryId;
					node.OccurredAt = eventData.OccurredAt;
					node.Ip = eventData.Ip;
					node.User = eventData.User;
					node.CapturedParams = parameters;
					node.Engine = engine;
					node.CurrentDepth = 0;
					node.Parent = null;
					node.LastExpansionAttemptEntryId = eventData.EntryId;

					roots.Add(node);
					totalNodesCreated++;

					// With a single engine, all patterns matched and this is a complete match.
					// With multiple engines, this is only the first level and must still be expanded.
					if (engines.Count == 1)
					{
						bool isFinalSeek = engine.IsFinalSeek;
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchTree] COMPLETE MATCH (single engine) at EntryId={eventData.EntryId}, IsFinalSeek={isFinalSeek}");
#endif
						ExecuteCompleteMatch(node);
					}
				}
				else
				{
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchTree] NO ROOT MATCH at EntryId={eventData.EntryId}");
#endif
					// No match: return parameters to the pool.
					parameters.PurgeUserParameters();
					actorHandler.ParametersPool.Return(parameters);
				}
			}
			catch
			{
				// On error, make sure parameters are returned to the pool.
				parameters.PurgeUserParameters();
				actorHandler.ParametersPool.Return(parameters);
				throw;
			}
		}
		private void TryAccumulateOnRepeatSeekRoots(EventData eventData, List<ReactionEngine> engines, ReactionEngine engine, string script, SymbolTable symbolTable, Program cachedProgram)
		{
			var parameters = actorHandler.ParametersPool.Rent();
			try
			{
				bool allMatched = true;
				for (int i = 0; i < engine.Patterns.Count; i++)
				{
					if (!engine.Patterns[i].Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, eventData.ExposeData))
					{
						allMatched = false;
						break;
					}
				}

				if (!allMatched)
				{
					parameters.PurgeUserParameters();
					actorHandler.ParametersPool.Return(parameters);

					// Aunque no matcheo el RepeatSeek, intentar el siguiente nivel (ThenSeek)
					// para cada raiz existente que tenga acumulados
					if (engines.Count > 1)
					{
						TryAdvanceRepeatSeekRoots(eventData, engines, script, symbolTable, cachedProgram);
					}
					return;
				}

				// Matcheo: buscar si hay un nodo raiz existente para acumular
				// (para RepeatSeek sin GroupBy, siempre hay un solo grupo)
				bool accumulated = false;
				foreach (var existingRoot in roots)
				{
					if (existingRoot.Engine == engine && existingRoot.Engine.IsRepeatSeek)
					{
						existingRoot.AccumulateEventId(eventData.EntryId);
						existingRoot.LastExpansionAttemptEntryId = eventData.EntryId;
						accumulated = true;

						parameters.PurgeUserParameters();
						actorHandler.ParametersPool.Return(parameters);
						break;
					}
				}

				if (!accumulated)
				{
					// Primer match: crear nodo raiz
					var node = nodePool.Rent();
					node.EntryId = eventData.EntryId;
					node.OccurredAt = eventData.OccurredAt;
					node.Ip = eventData.Ip;
					node.User = eventData.User;
					node.CapturedParams = parameters;
					node.Engine = engine;
					node.CurrentDepth = 0;
					node.Parent = null;
					node.LastExpansionAttemptEntryId = eventData.EntryId;
					node.AccumulateEventId(eventData.EntryId);

					roots.Add(node);
					totalNodesCreated++;

					// Si solo hay un engine, es un match completo
					if (engines.Count == 1)
					{
						ExecuteCompleteMatch(node);
					}
				}

				// Intentar avanzar al siguiente nivel para raices existentes
				if (engines.Count > 1)
				{
					TryAdvanceRepeatSeekRoots(eventData, engines, script, symbolTable, cachedProgram);
				}
			}
			catch
			{
				parameters.PurgeUserParameters();
				actorHandler.ParametersPool.Return(parameters);
				throw;
			}
		}

		private void TryAdvanceRepeatSeekRoots(EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram)
		{
			if (engines.Count < 2) return;

			var nextEngine = engines[1];
			if (nextEngine.Patterns.Count == 0) return;

			for (int ri = roots.Count - 1; ri >= 0; ri--)
			{
				var root = roots[ri];
				if (!root.Engine.IsRepeatSeek || root.AccumulatedEventIds == null || root.AccumulatedEventIds.Count == 0)
					continue;

				// No expandir con el mismo evento que lo creo
				if (root.EntryId == eventData.EntryId)
					continue;

				// Verificar condicion Until si existe
				if (root.Engine.UntilCount.HasValue && root.AccumulatedCount < root.Engine.UntilCount.Value)
					continue;

				var parameters = actorHandler.ParametersPool.Rent();
				try
				{
					CopyParameters(root.CapturedParams, parameters);

					bool allMatched = true;
					for (int i = 0; i < nextEngine.Patterns.Count; i++)
					{
						if (!nextEngine.Patterns[i].Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, eventData.ExposeData))
						{
							allMatched = false;
							break;
						}
					}

					if (allMatched && nextEngine.HasWhere)
					{
						if (!EvaluateWhere(nextEngine, parameters, symbolTable, eventData, root))
						{
							allMatched = false;
						}
					}

					if (allMatched)
					{
						var child = nodePool.Rent();
						child.EntryId = eventData.EntryId;
						child.OccurredAt = eventData.OccurredAt;
						child.Ip = eventData.Ip;
						child.User = eventData.User;
						child.CapturedParams = parameters;
						child.Engine = nextEngine;
						child.CurrentDepth = 1;
						child.Parent = root;
						child.LastExpansionAttemptEntryId = eventData.EntryId;

						root.Children.Add(child);
						totalNodesCreated++;

						if (1 == engines.Count - 1)
						{
							ExecuteCompleteMatch(child);
						}
					}
					else
					{
						parameters.PurgeUserParameters();
						actorHandler.ParametersPool.Return(parameters);
					}
				}
				catch
				{
					parameters.PurgeUserParameters();
					actorHandler.ParametersPool.Return(parameters);
					throw;
				}
			}
		}

		private void ExpandExistingMatches(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram)
		{
			// Check that the engine for this level exists.
			if (level >= engines.Count) return;

			var engine = engines[level];
			if (engine.Patterns.Count == 0) return;

			int previousLevel = level - 1;
			var nodesToExpand = GetNodesAtDepth(previousLevel);

			foreach (var node in nodesToExpand)
			{
				// Update LastExpansionAttemptEntryId for stale-node tracking.
				node.LastExpansionAttemptEntryId = eventData.EntryId;

				// Do not expand a node with the same event that created it.
				if (node.EntryId == eventData.EntryId)
				{
					continue;
				}

				var parameters = actorHandler.ParametersPool.Rent();
				try
				{
					// Copy the accumulated parameters from the parent.
					CopyParameters(node.CapturedParams, parameters);

					bool allMatched = true;

					// All engine patterns must match against the SAME script.
					for (int i = 0; i < engine.Patterns.Count; i++)
					{
						var pattern = engine.Patterns[i];
						bool matched = pattern.Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, eventData.ExposeData);
						if (!matched)
						{
							allMatched = false;
							break;
						}
					}

					if (allMatched && engine.HasWhere)
					{
						if (!EvaluateWhere(engine, parameters, symbolTable, eventData, node))
						{
							allMatched = false;
						}
					}

					if (allMatched)
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchTree] EXPAND MATCH at level={level}, EntryId={eventData.EntryId}, Parent={node.EntryId}");
#endif

						// Create the child node.
						var child = nodePool.Rent();
						child.EntryId = eventData.EntryId;
						child.OccurredAt = eventData.OccurredAt;
						child.Ip = eventData.Ip;
						child.User = eventData.User;
						child.CapturedParams = parameters;
						child.Engine = engine;
						child.CurrentDepth = level;
						child.Parent = node;
						child.LastExpansionAttemptEntryId = eventData.EntryId;

						node.Children.Add(child);
						totalNodesCreated++;

						// Check whether this is the last engine (complete match).
						if (level == engines.Count - 1)
						{
							bool isFinalSeek = engine.IsFinalSeek;
#if DEBUG
							System.Diagnostics.Debug.WriteLine($"[MatchTree] COMPLETE MATCH at level={level}, EntryId={eventData.EntryId}, IsFinalSeek={isFinalSeek}");
#endif
							ExecuteCompleteMatch(child);
						}
					}
					else
					{
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchTree] NO EXPAND MATCH at level={level}, EntryId={eventData.EntryId}, Parent={node.EntryId}");
#endif
						// No match: return parameters to the pool.
						parameters.PurgeUserParameters();
						actorHandler.ParametersPool.Return(parameters);
					}
				}
				catch
				{
					parameters.PurgeUserParameters();
					actorHandler.ParametersPool.Return(parameters);
					throw;
				}
			}
		}
		private List<MatchNode> GetNodesAtDepth(int depth)
		{
			_nodesAtDepthBuffer.Clear();

			if (depth == 0)
			{
				_nodesAtDepthBuffer.AddRange(roots);
			}
			else
			{
				// Buscar recursivamente
				foreach (var root in roots)
				{
					CollectNodesAtDepth(root, depth, 0, _nodesAtDepthBuffer);
				}
			}

			return _nodesAtDepthBuffer;
		}

		private void CollectNodesAtDepth(MatchNode node, int targetDepth, int currentDepth, List<MatchNode> result)
		{
			if (currentDepth == targetDepth)
			{
				result.Add(node);
				return;
			}

			foreach (var child in node.Children)
			{
				CollectNodesAtDepth(child, targetDepth, currentDepth + 1, result);
			}
		}
		private void ExecuteCompleteMatch(MatchNode leafNode)
		{
#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[MatchTree] ExecuteCompleteMatch for leafNode EntryId={leafNode.EntryId}");
#endif
			// Colectar EventIds una sola vez, reutilizar para checkpoint y MarkAsSkip
			reactionAction.EventIdsToSkip.Clear();
			CollectEventIdsFromChain(leafNode, reactionAction.EventIdsToSkip);

			if (reactionAction.EventIdsToSkip.Count == 0)
				return;

			_checkpointBuffer.Clear();
			for (int i = 0; i < reactionAction.EventIdsToSkip.Count; i++)
			{
				_checkpointBuffer[i] = reactionAction.EventIdsToSkip[i];
			}

			bool shouldExecuteAction = true;

			if (reactionAction.ActionType == ReactionActionType.Metadata && reactionAction.MetadataKind == MetadataKind.Elide && diaryStorage != null && reactionId > 0)
			{
				shouldExecuteAction = VerifyAndSaveTransactional(leafNode, reactionAction.EventIdsToSkip, _checkpointBuffer);
			}
			else
			{
				SaveCheckpointNonTransactional(_checkpointBuffer);
			}

			if (!shouldExecuteAction)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchTree] Match already processed by another pod, skipping action execution");
#endif
				// Limpiar EventIdsToSkip ya que no se ejecutara la accion
				reactionAction.EventIdsToSkip.Clear();
				PruneNode(leafNode);
				return;
			}

			Parameters allParameters = actorHandler.ParametersPool.Rent();
			try
			{
				CollectAllParametersFromChain(leafNode, allParameters);

#if DEBUG
				if (reactionAction.ActionType == ReactionActionType.Metadata && reactionAction.MetadataKind == MetadataKind.Elide)
				{
					System.Diagnostics.Debug.WriteLine($"[MatchTree] Metadata.Elide: EventIdsToSkip count = {reactionAction.EventIdsToSkip.Count}");
				}
#endif

				leafNode.Engine.ExecuteAction(allParameters, leafNode.EntryId);

				// PHASE 5A-2: only save the Confirmed checkpoint if ExecuteAction() succeeded.
				// On failure, leave a gap (detected > confirmed) so the retry kicks in.
				if (reactionAction.ActionType == ReactionActionType.Metadata && reactionAction.MetadataKind == MetadataKind.Elide && diaryStorage != null && reactionId > 0)
				{
					SaveConfirmedCheckpoint(_checkpointBuffer);
				}
			}
			catch (Exception ex)
			{
				// PHASE 5A-2: if ExecuteAction() fails, do NOT save Confirmed.
				// The gap (detected > confirmed) will trigger a retry on the next Execute().
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchTree] ExecuteAction failed: {ex.Message}. Confirmed checkpoint NOT saved (gap will trigger retry)");
#endif
				throw;
			}
			finally
			{
				allParameters.PurgeUserParameters();
				actorHandler.ParametersPool.Return(allParameters);
			}

			PruneNode(leafNode);
		}

		private bool VerifyAndSaveTransactional(MatchNode leafNode, List<long> matchEntryIds, Dictionary<int, long> newCheckpoint)
		{
			// Lexicographic comparison of the checkpoint.
			bool isGreater = IsCheckpointGreater(newCheckpoint, checkpointVector);

			if (!isGreater)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchTree] Checkpoint not greater than cached, refreshing from DB");
#endif

				foreach (var kvp in newCheckpoint.Keys)
				{
					long freshCheckpoint = diaryStorage.GetReactionLastProcessedEntryId(reactionId, kvp);
					if (checkpointVector != null)
						checkpointVector[kvp] = freshCheckpoint;
				}

				// Re-check against the refreshed checkpoint.
				isGreater = IsCheckpointGreater(newCheckpoint, checkpointVector);

				if (!isGreater)
				{
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchTree] Checkpoint not greater even after refresh, skipping");
#endif
					return false;
				}
			}

			// Use the timestamp of the last event in the match (from the diary), NOT DateTime.Now from the pod.
			CheckpointCommit commit = CheckpointCommit.FromMatchChain(leafNode, reactionId, leafNode.OccurredAt);
			bool success = diaryStorage.MarkEventsAsElidedWithCheckpoint(commit);

			if (!success)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchTree] Transactional commit failed, another pod won. Refreshing cache");
#endif
				foreach (var kvp in newCheckpoint.Keys)
				{
					long freshCheckpoint = diaryStorage.GetReactionLastProcessedEntryId(reactionId, kvp);
					if (checkpointVector != null)
						checkpointVector[kvp] = freshCheckpoint;
				}
				return false;
			}

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[MatchTree] Transactional commit succeeded");
#endif
			if (checkpointVector != null)
			{
				foreach (var kvp in newCheckpoint)
				{
					checkpointVector[kvp.Key] = kvp.Value;
				}
			}

			return true;
		}

		private bool IsCheckpointGreater(Dictionary<int, long> newCheckpoint, CheckpointVector currentCheckpoint)
		{
			// Lexicographic comparison: (1,2,6) < (4,5,6) because at the first differing level 1 < 4.
			// This lets matches that share events execute correctly.
			var sortedLevels = newCheckpoint.Keys.OrderBy(k => k).ToList();

			for (int i = 0; i < sortedLevels.Count; i++)
			{
				int level = sortedLevels[i];
				long newValue = newCheckpoint[level];
				long currentValue = (currentCheckpoint != null) ? currentCheckpoint.Get(level) : 0;

				if (newValue > currentValue)
				{
					return true; // First level where new > current.
				}
				else if (newValue < currentValue)
				{
					return false; // First level where new < current.
				}
				// Equal at this level: continue to the next.
			}

			// All levels equal → not greater.
			return false;
		}

		// PHASE 5A-2: save the Confirmed checkpoint after a successful ExecuteAction.
		private void SaveConfirmedCheckpoint(Dictionary<int, long> newCheckpoint)
		{
			ArgumentNullException.ThrowIfNull(newCheckpoint);

			if (diaryStorage == null || reactionId <= 0)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchTree] Cannot save Confirmed checkpoint: diaryStorage={diaryStorage != null}, reactionId={reactionId}");
#endif
				return;
			}

#if DEBUG
			System.Diagnostics.Debug.WriteLine($"[MatchTree] Saving Confirmed checkpoint for {newCheckpoint.Count} seek levels");
#endif

			foreach (var kvp in newCheckpoint)
			{
				int seekLevel = kvp.Key;
				long entryId = kvp.Value;

				try
				{
					diaryStorage.SaveReactionConfirmedCheckpoint(reactionId, seekLevel, entryId);
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchTree] Confirmed checkpoint saved: seekLevel={seekLevel}, entryId={entryId}");
#endif
				}
				catch (Exception ex)
				{
					// Log the error but do NOT throw — we don't want this to fail the whole flow.
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchTree] Error saving Confirmed checkpoint for seekLevel={seekLevel}: {ex.Message}");
#endif
				}
			}
		}

		private void SaveCheckpointNonTransactional(Dictionary<int, long> newCheckpoint)
		{
			if (diaryStorage == null || reactionId == 0)
				return;

			foreach (var kvp in newCheckpoint)
			{
				diaryStorage.SaveReactionLastProcessedEntryId(reactionId, kvp.Key, kvp.Value);
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[SaveCheckpointNonTransactional] Saved checkpoint: reactionId={reactionId}, seekLevel={kvp.Key}, entryId={kvp.Value}");
#endif
			}
		}

		private void CollectAllParametersFromChain(MatchNode node, Parameters destination)
		{
			if (node == null) return;

			// Walk recursively from the root.
			CollectAllParametersFromChain(node.Parent, destination);

			// Copy this node's parameters.
			if (node.CapturedParams != null)
			{
				CopyParameters(node.CapturedParams, destination);
			}
		}

		private void CollectEventIdsFromChain(MatchNode node, List<long> eventIds)
		{
			if (node == null) return;

			// Iterative: walk towards the root, then reverse the appended segment.
			int startIndex = eventIds.Count;
			var current = node;
			while (current != null)
			{
				eventIds.Add(current.EntryId);
				current = current.Parent;
			}
			eventIds.Reverse(startIndex, eventIds.Count - startIndex);
		}

		internal void PruneNode(MatchNode node)
		{
			if (node == null) return;

			// Recursively prune all children (iterate in reverse to avoid .ToArray()).
			for (int i = node.Children.Count - 1; i >= 0; i--)
			{
				PruneNode(node.Children[i]);
			}

			// Release parameters back to the pool.
			if (node.CapturedParams != null)
			{
				node.CapturedParams.PurgeUserParameters();
				actorHandler.ParametersPool.Return(node.CapturedParams);
				node.CapturedParams = null;
			}

			// Remove from the parent's list.
			if (node.Parent != null)
			{
				node.Parent.Children.Remove(node);
			}
			else
			{
				// Root node: remove from the roots list.
				roots.Remove(node);
			}

			// Return the node to the pool.
			nodePool.Return(node);
			totalNodesPruned++;
		}

		private void PruneStaleNodes(long currentEntryId)
		{
			_staleNodesBuffer.Clear();

			// Buscar nodos obsoletos en todos los niveles
			foreach (var root in roots)
			{
				CollectStaleNodes(root, currentEntryId, _staleNodesBuffer);
			}

			if (_staleNodesBuffer.Count > 0)
			{
#if DEBUG
				System.Diagnostics.Debug.WriteLine($"[MatchTree.Optimization] Pruning {_staleNodesBuffer.Count} stale nodes at EntryId={currentEntryId}");
#endif
				foreach (var node in _staleNodesBuffer)
				{
					PruneNode(node);
				}
			}
		}

		private void CollectStaleNodes(MatchNode node, long currentEntryId, List<MatchNode> result)
		{
			if (node == null) return;

			// Bottom-up: primero recursar hijos para que se poden las hojas
			for (int i = node.Children.Count - 1; i >= 0; i--)
			{
				CollectStaleNodes(node.Children[i], currentEntryId, result);
			}

			// Despues de podar hijos, verificar si este nodo quedo sin hijos y es stale
			long eventsSinceLastExpansion = currentEntryId - node.LastExpansionAttemptEntryId;
			if (eventsSinceLastExpansion > staleNodeThreshold && node.Children.Count == 0)
			{
				result.Add(node);
			}
		}
		private void CopyParameters(Parameters source, Parameters destination)
		{
			if (source == null || destination == null) return;

			foreach (var param in source)
			{
				destination[param.Name, param.ParameterType] = param.GetValue();
			}
		}

		// FASE 2: navega la str Parent hasta encontrar un MatchNode cuyo engine tenga el nombre dado.
		private static MatchNode FindAncestorBySeekName(MatchNode start, string seekName)
		{
			if (start == null || string.IsNullOrEmpty(seekName)) return null;

			var current = start;
			while (current != null)
			{
				if (current.Engine != null && string.Equals(current.Engine.PatternDescription, seekName, StringComparison.OrdinalIgnoreCase))
				{
					return current;
				}
				current = current.Parent;
			}
			return null;
		}

		// FASE 2: inyecta el value del symbol de un ancestor como SystemParameter con el nombre placeholder.
		private static void InjectScopedSymbol(Parameters target, string placeholder, string symbolName, MatchNode ancestor)
		{
			ArgumentNullException.ThrowIfNull(target);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(placeholder);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(symbolName);
			ArgumentNullException.ThrowIfNull(ancestor);

			switch (symbolName)
			{
				case "Now":
					target.SystemParameter<DateTime>(placeholder, ancestor.OccurredAt);
					break;
				case "EntryId":
					target.SystemParameter<int>(placeholder, checked((int)ancestor.EntryId));
					break;
				case "Ip":
					bool ipIsDefault = string.IsNullOrEmpty(ancestor.Ip) || ancestor.Ip == IpAddress.DEFAULT.Ip;
					IpAddress ip = ipIsDefault ? null : new IpAddress(ancestor.Ip);
					target.SystemParameter<IpAddress>(placeholder, ip);
					break;
				case "User":
					bool userIsAnonymous = string.IsNullOrEmpty(ancestor.User) || ancestor.User == UserInLog.ANONYMOUS.Id;
					UserInLog user = userIsAnonymous ? null : UserInLog.GenerateUserBasedOn(ancestor.User);
					target.SystemParameter<UserInLog>(placeholder, user);
					break;
				default:
					throw new LanguageException($"Simbolo scoped desconocido: '{symbolName}'. Validos: Now, User, Ip, EntryId.");
			}
		}

		// Evalua la clausula Where del engine contra el evento actual.
		// Retorna true si el filtro pasa, false si el match debe descartarse.
		// Los simbolos @Now/@User/@Ip/@EntryId se inyectan como SystemParameters en los Parameters
		// (el lexer trata '@' como whitespace, asi que '@Now' parsea a 'Now' y coincide con el
		// SystemParameter "Now" pre-populado por el pool).
		// FASE 2: 'SeekName.@Simbolo' se pre-procesa en Reaction a placeholders '_seek_SeekName_Simbolo'
		// que aqui se resuelven navegando parentNode.Parent hasta encontrar el MatchNode cuyo engine
		// tenga el nombre correspondiente.
		// Se reparsea y ejecuta en modo interpretado para evitar problemas con captura de Parameters.
		private bool EvaluateWhere(ReactionEngine engine, Parameters matchedParameters, SymbolTable symbolTable, EventData eventData, MatchNode parentNode)
		{
			ArgumentNullException.ThrowIfNull(engine);
			ArgumentNullException.ThrowIfNull(matchedParameters);
			ArgumentNullException.ThrowIfNull(symbolTable);
			ArgumentNullException.ThrowIfNull(eventData);

			if (!engine.HasWhere) throw new LanguageException($"EvaluateWhere llamado sin Where definido en Seek '{engine.PatternDescription}'.");

			// FASE 4: sentinelas IpAddress.DEFAULT y UserInLog.ANONYMOUS se exponen como null en el DSL.
			// Pasamos null a SystemParameter cuando el evento corresponde al sentinela; asi las expresiones
			// Where pueden usar '@Ip == null' y '@User == null' sin conocer las constantes sentinela.
			// El codigo C# interno (fuera del DSL) sigue usando IpAddress.DEFAULT/UserInLog.ANONYMOUS.
			bool ipIsDefault = string.IsNullOrEmpty(eventData.Ip) || eventData.Ip == IpAddress.DEFAULT.Ip;
			bool userIsAnonymous = string.IsNullOrEmpty(eventData.User) || eventData.User == UserInLog.ANONYMOUS.Id;
			IpAddress ip = ipIsDefault ? null : new IpAddress(eventData.Ip);
			UserInLog user = userIsAnonymous ? null : UserInLog.GenerateUserBasedOn(eventData.User);

			Parameters whereParameters = actorHandler.ParametersPool.Rent();
			try
			{
				// El lexer trata '@' como whitespace y lo descarta, asi que '@Now' parsea a 'Now'.
				// Inyectamos @Now/@Ip/@User/@EntryId como SystemParameters en los Parameters
				// (el pool ya pre-popula Now/Ip/User; actualizamos values + agregamos EntryId).
				foreach (var param in matchedParameters)
				{
					whereParameters[param.Name, param.ParameterType] = param.GetValue();
				}

				whereParameters.SystemParameter<DateTime>("Now", eventData.OccurredAt);
				whereParameters.SystemParameter<IpAddress>("Ip", ip);
				whereParameters.SystemParameter<UserInLog>("User", user);
				// Los operadores del interprete solo soportan int (no long). Para EntryId usamos int,
				// asumiendo que un diario real no supera 2.1B eventos en el horizonte esperado.
				whereParameters.SystemParameter<int>("EntryId", checked((int)eventData.EntryId));

				// FASE 2: resolver referencias scoped SeekName.@Simbolo navegando parentNode.Parent.
				if (engine.SeekScopedRefs != null)
				{
					foreach (var kvp in engine.SeekScopedRefs)
					{
						string placeholder = kvp.Key;
						string seekName = kvp.Value.seekName;
						string symbolName = kvp.Value.symbolName;
						MatchNode ancestor = FindAncestorBySeekName(parentNode, seekName);
						if (ancestor == null)
							throw new LanguageException($"Where del Seek '{engine.PatternDescription}' referencia '{seekName}.@{symbolName}' pero no se encontro el Seek '{seekName}' en la str de matches actual.");

						InjectScopedSymbol(whereParameters, placeholder, symbolName, ancestor);
					}
				}

				string wrappedScript = "Check(" + (engine.NormalizedWhereExpression ?? engine.WhereExpression) + ") Error '__where_failed__';";
				var parser = new Parser(actorHandler.Libraries, symbolTable);
				parser.SetSource(wrappedScript);
				var program = parser.Parse(isQuery: true, isCheck: false);
				program.AdjustCompilationMode(useInterpretedMode: true, CompilationModePolicy.AlwaysInterpreted);
				program.SolveReferences(whereParameters, withStaticValidation: true);
				program.CargarArgumentos(whereParameters);

				string output = program.Execute();
				return string.IsNullOrEmpty(output);
			}
			finally
			{
				whereParameters.PurgeUserParameters();
				actorHandler.ParametersPool.Return(whereParameters);
			}
		}
		internal void Clear()
		{
			for (int i = roots.Count - 1; i >= 0; i--)
			{
				PruneNode(roots[i]);
			}
			roots.Clear();
		}
		internal int ActiveNodeCount
		{
			get
			{
				int count = roots.Count;
				foreach (var root in roots)
				{
					count += CountDescendants(root);
				}
				return count;
			}
		}

		private int CountDescendants(MatchNode node)
		{
			int count = 0;
			foreach (var child in node.Children)
			{
				count++;
				count += CountDescendants(child);
			}
			return count;
		}
	}
}
