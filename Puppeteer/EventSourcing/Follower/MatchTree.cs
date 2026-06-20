using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Puppeteer.EventSourcing.Follower
{
	// Paso 4 del rediseño de checkpoint (notes/reactions-checkpoint-policy.md):
	// fotografia de un match de cobertura ABIERTO (compra cuya tupla-cobertura aun no
	// cierra completa, o cerrada pero parqueada por .Aged). Es lo que se serializa para
	// el cold-start de un consumidor-puro de replicacion (Svix no rebobina): en restart
	// se reconstruyen estos nodos y se resume en el frente, sin re-leer el journal.
	internal class CoverageMatchSnapshot
	{
		internal long AnchorEntryId { get; set; }
		internal DateTime AnchorOccurredAt { get; set; }
		internal List<string> RemainingObligations { get; set; }
		internal bool PendingSettle { get; set; }
		internal DateTime LastConfirmOccurredAt { get; set; }
		internal List<long> AccumulatedConfirmIds { get; set; }
	}

	// Codec del snapshot de cobertura. Formato interno (NO user-facing) linea-por-match,
	// culture-invariante: las obligaciones (strings de dominio arbitrarios) van en Base64
	// para evitar colision con los separadores. Una linea:
	//   anchorId|occurredAtTicks|pendingSettle(0/1)|lastConfirmTicks|cid,cid,...|oblB64,oblB64,...
	internal static class CoverageSnapshotCodec
	{
		internal static string Encode(List<CoverageMatchSnapshot> snapshots)
		{
			ArgumentNullException.ThrowIfNull(snapshots);
			var sb = new System.Text.StringBuilder();
			foreach (var s in snapshots)
			{
				sb.Append(s.AnchorEntryId).Append('|');
				sb.Append(s.AnchorOccurredAt.Ticks).Append('|');
				sb.Append(s.PendingSettle ? '1' : '0').Append('|');
				sb.Append(s.LastConfirmOccurredAt.Ticks).Append('|');
				if (s.AccumulatedConfirmIds != null)
				{
					for (int i = 0; i < s.AccumulatedConfirmIds.Count; i++)
					{
						if (i > 0) sb.Append(',');
						sb.Append(s.AccumulatedConfirmIds[i]);
					}
				}
				sb.Append('|');
				if (s.RemainingObligations != null)
				{
					for (int i = 0; i < s.RemainingObligations.Count; i++)
					{
						if (i > 0) sb.Append(',');
						sb.Append(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s.RemainingObligations[i] ?? string.Empty)));
					}
				}
				sb.Append('\n');
			}
			return sb.ToString();
		}

		internal static List<CoverageMatchSnapshot> Decode(string blob)
		{
			var result = new List<CoverageMatchSnapshot>();
			if (string.IsNullOrEmpty(blob)) return result;

			string[] lines = blob.Split('\n');
			foreach (string line in lines)
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				string[] parts = line.Split('|');
				if (parts.Length < 6) continue;

				var snap = new CoverageMatchSnapshot
				{
					AnchorEntryId = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
					AnchorOccurredAt = new DateTime(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)),
					PendingSettle = parts[2] == "1",
					LastConfirmOccurredAt = new DateTime(long.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)),
					AccumulatedConfirmIds = new List<long>(),
					RemainingObligations = new List<string>(),
				};

				if (parts[4].Length > 0)
				{
					foreach (string cid in parts[4].Split(','))
						snap.AccumulatedConfirmIds.Add(long.Parse(cid, System.Globalization.CultureInfo.InvariantCulture));
				}
				if (parts[5].Length > 0)
				{
					foreach (string ob in parts[5].Split(','))
						snap.RemainingObligations.Add(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ob)));
				}
				result.Add(snap);
			}
			return result;
		}
	}

	internal class MatchNode
	{
		internal long EntryId { get; set; }
		internal DateTime OccurredAt { get; set; }
		internal Parameters CapturedParams { get; set; }
		internal ReactionEngine Engine { get; set; }
		internal int CurrentDepth { get; set; }
		internal List<MatchNode> Children { get; set; }
		internal MatchNode Parent { get; set; }
		internal long LastExpansionAttemptEntryId { get; set; }

		// Many (K.1): acumulacion de IDs de eventos matcheados en este nivel
		internal List<long> AccumulatedEventIds { get; set; }
		internal int AccumulatedCount { get; set; }

		// I.time: anchor temporal del ultimo evento acumulado. Para ventanas por
		// TimeSpan, la ventana se desliza con la actividad reciente del Many parent
		// igual que el anchor de EntryId se desliza con AccumulatedEventIds.Last().
		// Default = OccurredAt al crear el nodo; se actualiza en cada accumulate.
		internal DateTime LastAccumulatedOccurredAt { get; set; }

		// K.2: gate de avance para Exact-family nodes. Default true (backward
		// compat — regular y Many nodes son advanceable apenas creados). Para
		// Exact nodes se setea false al eager-creation y true cuando la ventana
		// cierra con count == expected (lazy success). ExpandExistingMatches
		// salta parents con IsAdvanceable=false (Exact aun no cerrada/satisfecha).
		internal bool IsAdvanceable { get; set; } = true;

		// ForEach (F1b/F2): conjunto de obligaciones pendientes de este nodo captor.
		// Materializado al matchear el Seek captor = producto cartesiano de las
		// colecciones fuente (claves-tupla). Un Seek de cobertura descarga claves a
		// medida que matchea; cuando queda vacio, dispara el match completo. null si el
		// Reaction no tiene ForEach o este nodo no es el captor.
		internal HashSet<string> RemainingObligations { get; set; }

		// F3 .Aged: el nodo de cobertura completo (obligaciones vacias) pero cuyo evento
		// de cierre aun no asienta 'span' respecto al frente. Queda pendiente; el tick
		// por-evento lo dispara cuando el frente avanza lo suficiente. LastAccumulatedOccurredAt
		// del nodo Many de cobertura = OccurredAt del confirm de cierre.
		internal bool CoveragePendingSettle { get; set; }

		internal MatchNode()
		{
			Children = new List<MatchNode>();
		}
		internal void Clear()
		{
			EntryId = 0;
			OccurredAt = default;
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
			LastAccumulatedOccurredAt = default;
			IsAdvanceable = true;
			RemainingObligations = null;
			CoveragePendingSettle = false;
		}

		internal void AccumulateEventId(long entryId, DateTime occurredAt)
		{
			if (AccumulatedEventIds == null)
				AccumulatedEventIds = new List<long>();
			AccumulatedEventIds.Add(entryId);
			AccumulatedCount++;
			LastAccumulatedOccurredAt = occurredAt;
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
		private readonly ForEachSpec forEachSpec;

		private long totalNodesPruned = 0;
		private long totalNodesCreated = 0;

		// Buffers reutilizables para evitar alocaciones en hot paths
		private readonly Dictionary<int, long> _checkpointBuffer = new Dictionary<int, long>();
		private readonly List<MatchNode> _nodesAtDepthBuffer = new List<MatchNode>();
		private readonly List<MatchNode> _staleNodesBuffer = new List<MatchNode>();

		internal MatchTree(ActorHandler actorHandler, ReactionAction reactionAction, HydrationMode hydrationMode, int untilSeekIndex, CheckpointVector checkpointVector = null, DB.DiaryStorage diaryStorage = null, long reactionId = 0, int poolSize = 1024, int maxDepth = 100, int staleNodeThreshold = 1000, ForEachSpec forEachSpec = null)
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
			this.forEachSpec = forEachSpec;
			this.roots = new List<MatchNode>();
			this.nodePool = new MatchNodePool(poolSize);
		}

		internal long TotalNodesPruned => totalNodesPruned;
		internal long TotalNodesCreated => totalNodesCreated;
		internal void TryMatchAtLevel(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram = null, bool cachedProgramIsCanonical = false)
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

			// K.2 + I.time: tick antes de procesar el evento. Cierra ventanas
			// vencidas (Exact-nodes pendientes cuya ventana ya quedo atras vs
			// currentEntryId o currentOccurredAt). Solo en level=0 porque
			// ReplayEvent itera levels secuencialmente para un mismo evento —
			// un solo tick por evento basta. Tick puede disparar fires (Exact
			// at FinalSeek) o prunes (count != expected).
			if (level == 0)
			{
				TickPendingExactNodes(eventData.EntryId, eventData.OccurredAt);
				// F3 .Aged: el evento entrante avanza el frente; dispara los matches de
				// cobertura completos cuyo confirm de cierre ya asento 'span'.
				TickPendingCoverageSettle(eventData.OccurredAt);
			}

			// Decide which mode applies at this level.
			HydrationMode effectiveMode = GetEffectiveModeForLevel(level);

			// Select the strategy based on the effective mode for this level.
			if (effectiveMode == HydrationMode.Shared)
			{
				ProcessBreadthFirst(level, eventData, engines, script, symbolTable, cachedProgram, cachedProgramIsCanonical);

				// Prune stale nodes in BFS.
				PruneStaleNodes(eventData.EntryId);
			}
			else // HydrationMode.Independent
			{
				ProcessDepthFirst(level, eventData, engines, script, symbolTable, cachedProgram, cachedProgramIsCanonical);
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

		private void ProcessBreadthFirst(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram, bool cachedProgramIsCanonical)
		{
			if (level == 0)
			{
				TryMatchAtRoot(eventData, engines, script, symbolTable, cachedProgram, cachedProgramIsCanonical);
			}
			else
			{
				// Intermediate level: try to expand existing matches.
				ExpandExistingMatches(level, eventData, engines, script, symbolTable, cachedProgram, cachedProgramIsCanonical);
			}
		}

		private void ProcessDepthFirst(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram, bool cachedProgramIsCanonical)
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
				TryMatchAtRoot(eventData, engines, script, symbolTable, cachedProgram, cachedProgramIsCanonical);
			}
			else
			{
				// At intermediate levels, try to expand nodes from the previous level
				// but process each branch fully before moving on to the next one.
				ExpandExistingMatchesDepthFirst(level, eventData, engines, script, symbolTable, cachedProgram, cachedProgramIsCanonical);
			}

			// Prune stale nodes in DFS (same as BFS).
			// Without this, partial matches at intermediate levels persist indefinitely.
			PruneStaleNodes(eventData.EntryId);
		}

		private void ExpandExistingMatchesDepthFirst(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram, bool cachedProgramIsCanonical)
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

				// K.1: gate advancement out of a Many parent until its AtLeast
				// threshold is met. A Many parent without AtLeast has threshold=1
				// and is satisfied as soon as its first match accumulates, so the
				// gate is a no-op for the default case.
				if (node.Engine != null && node.Engine.IsMany)
				{
					int parentThreshold = node.Engine.AtLeastCount ?? 1;
					if (node.AccumulatedCount < parentThreshold)
					{
						continue;
					}
				}

				// K.2: gate por advanceability. Exact parents que aun no han
				// cerrado/satisfecho su ventana no permiten avance al siguiente
				// nivel. La satisfaccion se establece en TickPendingExactNodes
				// al cerrar la ventana con count == expected.
				if (node.Engine != null && node.Engine.IsExact && !node.IsAdvanceable)
				{
					continue;
				}

				// I.entry / I.time: gate by window. Anchor is the parent's last
				// active EntryId/OccurredAt (last accumulated for Many, creation
				// for regular). Window closes naturally when an event arrives past
				// anchor + entries (I.entry) or anchor + span (I.time).
				if (engine.WithinEntries.HasValue)
				{
					long anchor = GetParentAnchorEntryId(node);
					if (eventData.EntryId - anchor > engine.WithinEntries.Value)
					{
						continue;
					}
				}
				else if (engine.WithinTime.HasValue)
				{
					DateTime anchor = GetParentAnchorOccurredAt(node);
					if (eventData.OccurredAt - anchor > engine.WithinTime.Value)
					{
						continue;
					}
				}

				// K.2: Exact at this level — accumular o eager-fail contra el
				// Exact-child eager-creado del parent.
				if (engine.IsExact)
				{
					TryMatchOrAccumulateExact(parent: node, level: level, engine: engine, eventData: eventData, engines: engines, script: script, symbolTable: symbolTable, cachedProgram: cachedProgram, cachedProgramIsCanonical: cachedProgramIsCanonical);
					continue;
				}

				// K.1: Many at this level — accumulate-or-create; do not branch.
				if (engine.IsMany)
				{
					TryMatchOrAccumulateMany(parent: node, level: level, engine: engine, eventData: eventData, engines: engines, script: script, symbolTable: symbolTable, cachedProgram: cachedProgram, cachedProgramIsCanonical: cachedProgramIsCanonical);
					continue;
				}

				// Phase A: same shape as BFS — count one SeekEntered per
				// parent node attempted at this level.
				engine.IncrementSeekEntered();

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
						bool matched = pattern.Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, cachedProgramIsCanonical, eventData.ExposeData);
						if (!matched)
						{
							allMatched = false;
							break;
						}
					}

					if (allMatched && !EvaluateWhereIfPresent(engine, parameters, symbolTable, eventData, node))
					{
						allMatched = false;
					}

					if (allMatched)
					{
						engine.IncrementSeekMatched();
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchTree.DFS] EXPAND MATCH at level={level}, EntryId={eventData.EntryId}, Parent={node.EntryId}");
#endif

						// Create the child node.
						var child = nodePool.Rent();
						child.EntryId = eventData.EntryId;
						child.OccurredAt = eventData.OccurredAt;
						child.CapturedParams = parameters;
						child.Engine = engine;
						child.CurrentDepth = level;
						child.Parent = node;
						child.LastExpansionAttemptEntryId = eventData.EntryId;

						node.Children.Add(child);
						totalNodesCreated++;

						// K.2: si el siguiente engine es Exact, pre-crear su placeholder.
						MaybeCreateEagerExactChild(child, level, engines);

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

		private void TryMatchAtRoot(EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram, bool cachedProgramIsCanonical)
		{
			// Only process the first engine (level 0).
			if (engines.Count == 0) return;

			var engine = engines[0];
			if (engine.Patterns.Count == 0) return;

			// K.1: Many at root → accumulate-or-create. Single path, no dual-route
			// advance; ConsumeEvent's level=1+ iterations handle expansion to ThenSeek
			// via ExpandExistingMatches over depth-0 nodes.
			if (engine.IsMany)
			{
				TryMatchOrAccumulateMany(parent: null, level: 0, engine: engine, eventData: eventData, engines: engines, script: script, symbolTable: symbolTable, cachedProgram: cachedProgram, cachedProgramIsCanonical: cachedProgramIsCanonical);
				return;
			}

			// Phase A: every event evaluated against the root engine's patterns
			// counts as a SeekEntered, whether or not all patterns match.
			engine.IncrementSeekEntered();

			var parameters = actorHandler.ParametersPool.Rent();
			try
			{
				bool allMatched = true;

				// All engine patterns must match against the SAME script.
				for (int i = 0; i < engine.Patterns.Count; i++)
				{
					var pattern = engine.Patterns[i];
					bool matched = pattern.Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, cachedProgramIsCanonical, eventData.ExposeData);
					if (!matched)
					{
						allMatched = false;
						break;
					}
				}

				// Level 0: there is no parent. SeekName.@X should not appear in the first Seek.
				if (allMatched && !EvaluateWhereIfPresent(engine, parameters, symbolTable, eventData, null))
				{
					allMatched = false;
				}

				if (allMatched)
				{
					engine.IncrementSeekMatched();
#if DEBUG
					System.Diagnostics.Debug.WriteLine($"[MatchTree] ROOT MATCH at EntryId={eventData.EntryId}, EngineCount={engines.Count}");
#endif

					// Create the new root node.
					var node = nodePool.Rent();
					node.EntryId = eventData.EntryId;
					node.OccurredAt = eventData.OccurredAt;
					node.CapturedParams = parameters;
					node.Engine = engine;
					node.CurrentDepth = 0;
					node.Parent = null;
					node.LastExpansionAttemptEntryId = eventData.EntryId;

					roots.Add(node);
					totalNodesCreated++;

					// ForEach (F1b): el Seek raiz es el captor; materializa el conjunto de
					// obligaciones (producto cartesiano de las colecciones fuente capturadas).
					MaterializeObligations(node);

					// K.2: si engines[1] es Exact, pre-crear su Exact-child placeholder.
					MaybeCreateEagerExactChild(node, 0, engines);

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
		// K.1: existential Seek quantifier. Either accumulates this event into the
		// existing Many node anchored to `parent` (or to the roots list when parent
		// is null), or creates that node on first match. Subsequent matches at the
		// same level fold into the same node so the trajectory advances only once
		// per Many level. AtLeast(n) is honored: ExecuteCompleteMatch is gated on
		// the AccumulatedCount crossing the threshold exactly once.
		private void TryMatchOrAccumulateMany(MatchNode parent, int level, ReactionEngine engine, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram, bool cachedProgramIsCanonical)
		{
			engine.IncrementSeekEntered();

			var parameters = actorHandler.ParametersPool.Rent();
			try
			{
				if (parent != null)
				{
					CopyParameters(parent.CapturedParams, parameters);
				}

				bool allMatched = true;
				for (int i = 0; i < engine.Patterns.Count; i++)
				{
					if (!engine.Patterns[i].Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, cachedProgramIsCanonical, eventData.ExposeData))
					{
						allMatched = false;
						break;
					}
				}

				if (allMatched && !EvaluateWhereIfPresent(engine, parameters, symbolTable, eventData, parent))
				{
					allMatched = false;
				}

				if (!allMatched)
				{
					parameters.PurgeUserParameters();
					actorHandler.ParametersPool.Return(parameters);
					return;
				}

				engine.IncrementSeekMatched();

				int threshold = engine.AtLeastCount ?? 1;
				bool isLastLevel = level == engines.Count - 1;

				// ForEach (F2): modo cobertura. Si el parent es un nodo captor con
				// obligaciones, este Many descarga una obligacion por match en vez de
				// contar. El evento solo cuenta si su tupla (formada con las loop-vars
				// capturadas) es una obligacion pendiente del parent; los matches cuya
				// tupla no pertenece a este parent (o ya descargada) se ignoran sin
				// acumular. El disparo es cuando el conjunto de obligaciones queda vacio.
				bool coverage = forEachSpec != null && parent != null && parent.RemainingObligations != null;
				string coverageKey = null;
				if (coverage)
				{
					coverageKey = CoverageKey(parameters);
					if (!parent.RemainingObligations.Contains(coverageKey))
					{
						parameters.PurgeUserParameters();
						actorHandler.ParametersPool.Return(parameters);
						return;
					}
				}

				// Find existing Many node for this (parent, engine) pair. For level=0
				// (parent==null), scope is the roots list.
				MatchNode existing = FindManyNode(parent, engine);

				if (existing != null)
				{
					int prevCount = existing.AccumulatedCount;
					existing.AccumulateEventId(eventData.EntryId, eventData.OccurredAt);
					existing.LastExpansionAttemptEntryId = eventData.EntryId;

					parameters.PurgeUserParameters();
					actorHandler.ParametersPool.Return(parameters);

					if (coverage)
					{
						parent.RemainingObligations.Remove(coverageKey);
						if (isLastLevel && parent.RemainingObligations.Count == 0)
						{
							// F3 .Aged: si el seek de cierre tiene asentamiento, no dispares aun
							// (el frente = el confirm recien acumulado, edad 0); marca pendiente y
							// deja que el tick por-evento dispare al envejecer 'span'.
							if (engine.AgedSpan.HasValue)
								existing.CoveragePendingSettle = true;
							else
								ExecuteCompleteMatch(existing);
						}
					}
					// Fire once when the threshold is crossed at the final level.
					// AtLeast(1) (default) only crosses on creation, so this path
					// handles AtLeast(n>=2) reaching threshold via accumulation.
					else if (isLastLevel && prevCount < threshold && existing.AccumulatedCount >= threshold)
					{
						ExecuteCompleteMatch(existing);
					}
					return;
				}

				// First match for this (parent, engine): create the Many node.
				var node = nodePool.Rent();
				node.EntryId = eventData.EntryId;
				node.OccurredAt = eventData.OccurredAt;
				node.CapturedParams = parameters;
				node.Engine = engine;
				node.CurrentDepth = level;
				node.Parent = parent;
				node.LastExpansionAttemptEntryId = eventData.EntryId;
				node.AccumulateEventId(eventData.EntryId, eventData.OccurredAt);

				if (parent == null)
				{
					roots.Add(node);
				}
				else
				{
					parent.Children.Add(node);
				}
				totalNodesCreated++;

				// K.2: si el siguiente engine es Exact, pre-crear su placeholder.
				MaybeCreateEagerExactChild(node, level, engines);

				if (coverage)
				{
					parent.RemainingObligations.Remove(coverageKey);
					if (isLastLevel && parent.RemainingObligations.Count == 0)
					{
						// F3 .Aged: ver nota en la rama 'existing'.
						if (engine.AgedSpan.HasValue)
							node.CoveragePendingSettle = true;
						else
							ExecuteCompleteMatch(node);
					}
				}
				else if (isLastLevel && node.AccumulatedCount >= threshold)
				{
					ExecuteCompleteMatch(node);
				}
			}
			catch
			{
				parameters.PurgeUserParameters();
				actorHandler.ParametersPool.Return(parameters);
				throw;
			}
		}

		private MatchNode FindManyNode(MatchNode parent, ReactionEngine engine)
		{
			if (parent == null)
			{
				foreach (var root in roots)
				{
					if (root.Engine == engine && root.Engine.IsMany)
						return root;
				}
				return null;
			}

			foreach (var child in parent.Children)
			{
				if (child.Engine == engine && child.Engine.IsMany)
					return child;
			}
			return null;
		}

		// I.entry: anchor de una ventana es el ultimo EntryId activo del parent.
		// Para un parent Many el anchor es el ultimo evento acumulado — la ventana
		// se desliza con la actividad reciente del Seek previo. Para un parent
		// regular el anchor es su EntryId de creacion (un solo punto de match).
		private long GetParentAnchorEntryId(MatchNode parent)
		{
			ArgumentNullException.ThrowIfNull(parent);

			if (parent.AccumulatedEventIds != null && parent.AccumulatedEventIds.Count > 0)
			{
				return parent.AccumulatedEventIds[parent.AccumulatedEventIds.Count - 1];
			}
			return parent.EntryId;
		}

		// I.time: anchor temporal del parent. Mismo principio de slide que para
		// EntryId — para Many el anchor es LastAccumulatedOccurredAt (se actualiza
		// en cada AccumulateEventId), para regular es OccurredAt de creacion.
		private DateTime GetParentAnchorOccurredAt(MatchNode parent)
		{
			ArgumentNullException.ThrowIfNull(parent);

			if (parent.LastAccumulatedOccurredAt != default)
			{
				return parent.LastAccumulatedOccurredAt;
			}
			return parent.OccurredAt;
		}

		// K.2: cuantificador exact-family. Accumula contra el Exact-child eager-creado
		// del parent. Eager-fail si AccumulatedCount excede expected (Exactly/One con
		// match adicional, None con cualquier match). El fire por satisfaccion ocurre
		// en TickPendingExactNodes cuando la ventana cierra con count == expected.
		private void TryMatchOrAccumulateExact(MatchNode parent, int level, ReactionEngine engine, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram, bool cachedProgramIsCanonical)
		{
			ArgumentNullException.ThrowIfNull(parent);

			MatchNode exactNode = FindExactNode(parent, engine);
			if (exactNode == null)
			{
				// Pruned ya (eager-fail anterior) o no eager-creado. Nada que hacer.
				return;
			}

			engine.IncrementSeekEntered();

			var parameters = actorHandler.ParametersPool.Rent();
			try
			{
				CopyParameters(parent.CapturedParams, parameters);

				bool allMatched = true;
				for (int i = 0; i < engine.Patterns.Count; i++)
				{
					if (!engine.Patterns[i].Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, cachedProgramIsCanonical, eventData.ExposeData))
					{
						allMatched = false;
						break;
					}
				}

				if (allMatched && !EvaluateWhereIfPresent(engine, parameters, symbolTable, eventData, parent))
				{
					allMatched = false;
				}

				if (!allMatched)
				{
					return;
				}

				engine.IncrementSeekMatched();

				exactNode.AccumulateEventId(eventData.EntryId, eventData.OccurredAt);
				exactNode.LastExpansionAttemptEntryId = eventData.EntryId;
				if (exactNode.EntryId == 0)
				{
					// Primer match: arrastrar el EntryId del primer evento (mismo
					// patron que Many) para que la cadena tenga un anchor identificable.
					exactNode.EntryId = eventData.EntryId;
					exactNode.OccurredAt = eventData.OccurredAt;
				}

				int expected = engine.ExactCount.Value;
				if (exactNode.AccumulatedCount > expected)
				{
					// Eager-fail. La trayectoria por este Exact-child esta muerta.
					PruneNode(exactNode);
				}
			}
			finally
			{
				parameters.PurgeUserParameters();
				actorHandler.ParametersPool.Return(parameters);
			}
		}

		// K.2: localizar el Exact-child de un parent para un engine dado (eager-creado).
		private MatchNode FindExactNode(MatchNode parent, ReactionEngine engine)
		{
			if (parent == null) return null;
			foreach (var child in parent.Children)
			{
				if (child.Engine == engine && child.Engine.IsExact)
					return child;
			}
			return null;
		}

		// K.2 + I.time: cierre por evento out-of-window. Recorre el arbol buscando
		// Exact-nodes pendientes (IsAdvanceable=false) cuya ventana haya cerrado.
		// El cierre se evalua segun el tipo de ventana del engine: EntryId (I.entry)
		// o TimeSpan (I.time). Para cada uno: count == expected -> satisfaccion
		// (marca advanceable; si es FinalSeek, fire). count != expected -> falla,
		// prune. Sin este tick los Exact que nunca matchearon (None vacuo, etc)
		// quedarian eternamente pending.
		private readonly List<MatchNode> _tickBuffer = new List<MatchNode>();
		private readonly List<MatchNode> _coverageSettleBuffer = new List<MatchNode>();

		// F3 .Aged: dispara los nodos de cobertura completos (CoveragePendingSettle) cuyo
		// evento de cierre (LastAccumulatedOccurredAt) ya asento 'span' respecto al frente
		// (currentOccurredAt = el evento entrante, reloj logico del journal). Los nodos de
		// cobertura son Many -> ExecuteCompleteMatch NO los prunea, asi que limpiar la
		// bandera evita re-disparo.
		private void TickPendingCoverageSettle(DateTime currentOccurredAt)
		{
			_coverageSettleBuffer.Clear();
			foreach (var root in roots)
			{
				CollectPendingCoverageNodes(root, _coverageSettleBuffer);
			}

			foreach (var node in _coverageSettleBuffer)
			{
				if (!node.CoveragePendingSettle) continue;
				if (node.Engine == null || !node.Engine.AgedSpan.HasValue) continue;

				if (currentOccurredAt - node.LastAccumulatedOccurredAt >= node.Engine.AgedSpan.Value)
				{
					node.CoveragePendingSettle = false;
					ExecuteCompleteMatch(node);
				}
			}
		}

		private void CollectPendingCoverageNodes(MatchNode node, List<MatchNode> buffer)
		{
			if (node == null) return;
			if (node.CoveragePendingSettle) buffer.Add(node);
			foreach (var child in node.Children)
			{
				CollectPendingCoverageNodes(child, buffer);
			}
		}

		private void TickPendingExactNodes(long currentEntryId, DateTime currentOccurredAt)
		{
			_tickBuffer.Clear();
			foreach (var root in roots)
			{
				CollectPendingExactNodes(root, _tickBuffer);
			}

			foreach (var exactNode in _tickBuffer)
			{
				if (exactNode.Parent == null) continue;
				if (exactNode.IsAdvanceable) continue;

				bool windowClosed = false;
				if (exactNode.Engine.WithinEntries.HasValue)
				{
					long anchor = GetParentAnchorEntryId(exactNode.Parent);
					long windowEnd = anchor + exactNode.Engine.WithinEntries.Value;
					windowClosed = currentEntryId > windowEnd;
				}
				else if (exactNode.Engine.WithinTime.HasValue)
				{
					DateTime anchor = GetParentAnchorOccurredAt(exactNode.Parent);
					windowClosed = (currentOccurredAt - anchor) > exactNode.Engine.WithinTime.Value;
				}

				if (!windowClosed) continue;

				int expected = exactNode.Engine.ExactCount.Value;
				if (exactNode.AccumulatedCount == expected)
				{
					exactNode.IsAdvanceable = true;
					if (exactNode.Engine.IsFinalSeek)
					{
						ExecuteCompleteMatch(exactNode);
					}
				}
				else
				{
					PruneNode(exactNode);
				}
			}
		}

		private void CollectPendingExactNodes(MatchNode node, List<MatchNode> buffer)
		{
			if (node == null) return;
			if (node.Engine != null && node.Engine.IsExact && !node.IsAdvanceable)
			{
				buffer.Add(node);
			}
			foreach (var child in node.Children)
			{
				CollectPendingExactNodes(child, buffer);
			}
		}

		// K.2: eager-creation del Exact-child al crearse un parent. Si engines[parentLevel+1]
		// es Exact, crea inmediatamente el placeholder node con AccumulatedCount=0,
		// IsAdvanceable=false. La ventana se evaluara al tick cuando llegue un evento
		// con EntryId > parent.anchor + within. Si llega match del Exact pattern antes,
		// AccumulatedCount sube; eager-fail si excede expected.
		private void MaybeCreateEagerExactChild(MatchNode parent, int parentLevel, List<ReactionEngine> engines)
		{
			ArgumentNullException.ThrowIfNull(parent);
			ArgumentNullException.ThrowIfNull(engines);

			int childLevel = parentLevel + 1;
			if (childLevel >= engines.Count) return;

			var childEngine = engines[childLevel];
			if (!childEngine.IsExact) return;

			// Si ya existe (defensivo), no recrear.
			if (FindExactNode(parent, childEngine) != null) return;

			var exactNode = nodePool.Rent();
			exactNode.EntryId = 0;
			exactNode.OccurredAt = parent.OccurredAt;
			exactNode.CapturedParams = actorHandler.ParametersPool.Rent();
			CopyParameters(parent.CapturedParams, exactNode.CapturedParams);
			exactNode.Engine = childEngine;
			exactNode.CurrentDepth = childLevel;
			exactNode.Parent = parent;
			exactNode.LastExpansionAttemptEntryId = parent.LastExpansionAttemptEntryId;
			exactNode.IsAdvanceable = false;

			parent.Children.Add(exactNode);
			totalNodesCreated++;
		}

		private void ExpandExistingMatches(int level, EventData eventData, List<ReactionEngine> engines, string script, SymbolTable symbolTable, Program cachedProgram, bool cachedProgramIsCanonical)
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

				// K.1: gate advancement out of a Many parent until its AtLeast
				// threshold is met. A Many parent without AtLeast has threshold=1
				// and is satisfied as soon as its first match accumulates, so the
				// gate is a no-op for the default case.
				if (node.Engine != null && node.Engine.IsMany)
				{
					int parentThreshold = node.Engine.AtLeastCount ?? 1;
					if (node.AccumulatedCount < parentThreshold)
					{
						continue;
					}
				}

				// K.2: gate por advanceability. Exact parents que aun no han
				// cerrado/satisfecho su ventana no permiten avance al siguiente
				// nivel. La satisfaccion se establece en TickPendingExactNodes
				// al cerrar la ventana con count == expected.
				if (node.Engine != null && node.Engine.IsExact && !node.IsAdvanceable)
				{
					continue;
				}

				// I.entry / I.time: gate by window. Anchor is the parent's last
				// active EntryId/OccurredAt (last accumulated for Many, creation
				// for regular). Window closes naturally when an event arrives past
				// anchor + entries (I.entry) or anchor + span (I.time).
				if (engine.WithinEntries.HasValue)
				{
					long anchor = GetParentAnchorEntryId(node);
					if (eventData.EntryId - anchor > engine.WithinEntries.Value)
					{
						continue;
					}
				}
				else if (engine.WithinTime.HasValue)
				{
					DateTime anchor = GetParentAnchorOccurredAt(node);
					if (eventData.OccurredAt - anchor > engine.WithinTime.Value)
					{
						continue;
					}
				}

				// K.2: Exact at this level — accumular o eager-fail contra el
				// Exact-child eager-creado del parent.
				if (engine.IsExact)
				{
					TryMatchOrAccumulateExact(parent: node, level: level, engine: engine, eventData: eventData, engines: engines, script: script, symbolTable: symbolTable, cachedProgram: cachedProgram, cachedProgramIsCanonical: cachedProgramIsCanonical);
					continue;
				}

				// K.1: Many at this level — accumulate-or-create; do not branch.
				if (engine.IsMany)
				{
					TryMatchOrAccumulateMany(parent: node, level: level, engine: engine, eventData: eventData, engines: engines, script: script, symbolTable: symbolTable, cachedProgram: cachedProgram, cachedProgramIsCanonical: cachedProgramIsCanonical);
					continue;
				}

				// Phase A: each parent node being evaluated counts as a
				// SeekEntered at this level — drop-off is parent-count minus
				// successful matches.
				engine.IncrementSeekEntered();

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
						bool matched = pattern.Match(script, eventData.OccurredAt, parameters, symbolTable, cachedProgram, cachedProgramIsCanonical, eventData.ExposeData);
						if (!matched)
						{
							allMatched = false;
							break;
						}
					}

					if (allMatched && !EvaluateWhereIfPresent(engine, parameters, symbolTable, eventData, node))
					{
						allMatched = false;
					}

					if (allMatched)
					{
						engine.IncrementSeekMatched();
#if DEBUG
						System.Diagnostics.Debug.WriteLine($"[MatchTree] EXPAND MATCH at level={level}, EntryId={eventData.EntryId}, Parent={node.EntryId}");
#endif

						// Create the child node.
						var child = nodePool.Rent();
						child.EntryId = eventData.EntryId;
						child.OccurredAt = eventData.OccurredAt;
						child.CapturedParams = parameters;
						child.Engine = engine;
						child.CurrentDepth = level;
						child.Parent = node;
						child.LastExpansionAttemptEntryId = eventData.EntryId;

						node.Children.Add(child);
						totalNodesCreated++;

						// K.2: si el siguiente engine es Exact, pre-crear su placeholder.
						MaybeCreateEagerExactChild(child, level, engines);

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

			// K.1: build the checkpoint vector via a separate level-walk. With Many,
			// EventIdsToSkip carries every accumulated event (one chain may contribute
			// N events at one level), but CheckpointVector expects exactly one entry
			// per Seek level — the most recent accumulated ID. Indexing it by
			// EventIdsToSkip position would push seekLevel beyond the Reaction's
			// declared level count and trip ValidateSeekLevel.
			_checkpointBuffer.Clear();
			CollectCheckpointLevelIds(leafNode, _checkpointBuffer);

			// Shadow Replay — S3. Skip-preview (dry-run): en un shadow con preview
			// encendido, una reaction Elide captura el batch que elidiria en
			// Reaction.WouldSkip + registra el match en Phase A, pero OMITE el commit
			// (no MarkEventsAsElidedWithCheckpoint) y no ejecuta side-effects. Es "ver
			// que se elidiria" sin tocar ningun journal. WouldSkip SOLO se puebla aqui,
			// y esta rama RETORNA antes del commit — por eso WouldSkip poblado prueba
			// que corrio el dry-run y NO la elision real.
			if (reactionAction.ActionType == ReactionActionType.Metadata
				&& reactionAction.MetadataKind == MetadataKind.Elide
				&& actorHandler.SkipPreviewEnabled)
			{
				long[] previewBatch = reactionAction.EventIdsToSkip.ToArray();
				leafNode.Engine.Reaction.RecordWouldSkip(previewBatch);

				Parameters previewParameters = actorHandler.ParametersPool.Rent();
				try
				{
					CollectAllParametersFromChain(leafNode, previewParameters);
					leafNode.Engine.Reaction.RecordCompleteMatch(leafNode.EntryId, leafNode.OccurredAt, previewBatch, previewParameters);
				}
				finally
				{
					previewParameters.PurgeUserParameters();
					actorHandler.ParametersPool.Return(previewParameters);
				}

				reactionAction.EventIdsToSkip.Clear();
				if (leafNode.Engine == null || !leafNode.Engine.IsMany)
					PruneNode(leafNode);
				return;
			}

			bool shouldExecuteAction = true;

			if (reactionAction.ActionType == ReactionActionType.Metadata && reactionAction.MetadataKind == MetadataKind.Elide && diaryStorage != null && reactionId > 0)
			{
				if (forEachSpec != null)
				{
					// Cobertura (ForEach): los matches concurrentes multi-ancla cierran FUERA
					// de orden de ancla, asi que el guard lexicografico monotono
					// (VerifyAndSaveTransactional + el de MarkEventsAsElidedWithCheckpoint) los
					// deduplicaria (p.ej. ancla 1 tras ancla 2). Se elide IDEMPOTENTE directo:
					// re-elidir es no-op (flag Skip) y MarkEventsAsElided es transaccional por
					// fila -> seguridad cross-pod por membership, no por orden. NO se guarda el
					// checkpoint escalar (no aplica a cobertura; el resume es por frontera-cerrada
					// / snapshot — rediseño en notes/reactions-checkpoint-policy.md).
					diaryStorage.EventElisionStorage.MarkEventsAsElided(reactionAction.EventIdsToSkip.ToArray(), (int)reactionId, leafNode.OccurredAt);
					shouldExecuteAction = true;
				}
				else
				{
					shouldExecuteAction = VerifyAndSaveTransactional(leafNode, reactionAction.EventIdsToSkip, _checkpointBuffer);
				}
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

				// Phase A: snapshot the match before ExecuteAction runs so the
				// retained Bindings reflect what the matcher actually
				// captured, not what the action may have mutated via
				// _configureParameters. Recorded only when the action is
				// about to execute (we are past the shouldExecuteAction
				// gate); the dedup-skip branch upstream returned earlier.
				long[] chainSnapshot = reactionAction.EventIdsToSkip.ToArray();
				leafNode.Engine.Reaction.RecordCompleteMatch(leafNode.EntryId, leafNode.OccurredAt, chainSnapshot, allParameters);

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

			// K.1: a Many leaf node stays alive after firing so subsequent
			// matches at the same level accumulate into it instead of creating
			// a new Many node and refiring. PruneStaleNodes reclaims it once
			// the journal moves past its activity window. Non-Many leaves are
			// pruned eagerly — each Final-Seek match is its own trajectory.
			if (leafNode.Engine == null || !leafNode.Engine.IsMany)
			{
				PruneNode(leafNode);
			}
		}

		// POLITICA DE CHECKPOINT/RESUME/DEDUP — leer antes de tocar esto.
		// Detalle completo y matriz: notes/reactions-checkpoint-policy.md
		//
		// HOY: dedup por comparacion lexicografica monotona del vector per-seek
		// (IsCheckpointGreater). Funciona SOLO cuando los matches cierran en orden de
		// ancla (patrones secuenciales). LIMITACION CONOCIDA: con cobertura (ForEach) los
		// matches cierran FUERA de orden (fan-out multi-ancla) y el gate lexicografico
		// descarta commits legitimos (p.ej. (1,4) tras (2,3)). Test que lo fija:
		// ReactionForEachCoverageTests.ForEach_FanOut [Ignore].
		//
		// OBJETIVO (disenado, no implementado):
		//   - dedup = membership idempotente (flag Skip + MarkEventsAsElidedWithCheckpoint
		//     transaccional por fila), NO orden lexicografico. Los matches concurrentes no
		//     tienen orden total.
		//   - resume = dos cursores globales: frente-leido (high-water) + frontera-cerrada
		//     (min ancla abierta), acotada por settle (.Aged es su gate).
		//   - cold-start: Job/Cue con journal local -> re-lee [closed, high-water]; replicacion
		//     consumidor-puro (Svix no rebobina) -> snapshot de matches abiertos; Shadow ->
		//     nada (no commitea, no resume).
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

		// K.1: build the per-level checkpoint dictionary (one entry per Seek level,
		// keyed by depth). For Many nodes the last accumulated EntryId wins — it is
		// the high-water mark for that level. Mirrors CheckpointCommit's static
		// CollectCheckpointLevelIds so the persisted CheckpointCommit and the
		// in-memory _checkpointBuffer stay in lockstep.
		private void CollectCheckpointLevelIds(MatchNode leafNode, Dictionary<int, long> destination)
		{
			int depth = 0;
			var current = leafNode;
			while (current != null)
			{
				current = current.Parent;
				depth++;
			}

			current = leafNode;
			int level = depth - 1;
			while (current != null)
			{
				long checkpointId;
				if (current.AccumulatedEventIds != null && current.AccumulatedEventIds.Count > 0)
				{
					checkpointId = current.AccumulatedEventIds[current.AccumulatedEventIds.Count - 1];
				}
				else
				{
					checkpointId = current.EntryId;
				}
				destination[level] = checkpointId;
				level--;
				current = current.Parent;
			}
		}

		// ForEach (F1b): materializa el conjunto de obligaciones del nodo captor =
		// producto cartesiano de las colecciones fuente capturadas, como claves-tupla.
		private void MaterializeObligations(MatchNode node)
		{
			if (forEachSpec == null) return;

			var sources = new List<System.Collections.IEnumerable>(forEachSpec.SourceVars.Count);
			foreach (string src in forEachSpec.SourceVars)
			{
				if (node.CapturedParams == null || !node.CapturedParams.ContainsParameter(src))
					throw new LanguageException($"ForEach: la coleccion fuente '${src}' no fue capturada por el Seek captor.");

				object val = node.CapturedParams[src]?.GetValue();
				if (!(val is System.Collections.IEnumerable enumerable) || val is string)
					throw new LanguageException($"ForEach: la fuente '${src}' no es una coleccion (es {val?.GetType().Name ?? "null"}).");

				sources.Add(enumerable);
			}

			List<object[]> tuples = forEachSpec.CrossProduct(sources);
			var set = new HashSet<string>(tuples.Count);
			foreach (object[] tuple in tuples) set.Add(ObligationKey(tuple));
			node.RemainingObligations = set;
		}

		// Clave canonica de una obligacion / tupla de cobertura. La materializacion y la
		// descarga deben coincidir byte-a-byte, asi que ambas pasan por aqui (valores
		// formateados culture-invariante).
		private string CoverageKey(Parameters parameters)
		{
			var vars = forEachSpec.TupleVars;
			object[] vals = new object[vars.Count];
			for (int i = 0; i < vars.Count; i++)
			{
				vals[i] = parameters.ContainsParameter(vars[i]) ? parameters[vars[i]]?.GetValue() : null;
			}
			return ObligationKey(vals);
		}

		private static string ObligationKey(object[] values)
		{
			var sb = new System.Text.StringBuilder();
			for (int i = 0; i < values.Length; i++)
			{
				if (i > 0) sb.Append("|#|");
				object v = values[i];
				if (v == null) sb.Append("<null>");
				else if (v is IFormattable f) sb.Append(f.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
				else sb.Append(v.ToString());
			}
			return sb.ToString();
		}

		private void CollectEventIdsFromChain(MatchNode node, List<long> eventIds)
		{
			if (node == null) return;

			// K.1: include all accumulated event IDs for Many nodes. Without this,
			// the EventIdsToSkip path would mark only the first match per Many
			// level as elided, leaving the rest of the collapsed multiplicity in
			// the journal. This mirrors what CheckpointCommit.CollectEventIdsFromChain
			// already does for persistence.
			// K.2: an Exact node with zero accumulated matches (e.g. None that
			// succeeded vacuously) contributes no event IDs — there are no events
			// to mark as elided. The EntryId fallback (which would be 0 for an
			// eager-created Exact node that never matched) is skipped explicitly.
			int startIndex = eventIds.Count;
			var current = node;
			while (current != null)
			{
				// F4 Elide(seek:/seeks:): si hay Seeks objetivo, solo recolectar los ids de
				// los nodos cuyo Seek esta en el conjunto (p.ej. elidir la compra ancla pero
				// no sus confirms). Sin objetivos -> la cadena completa.
				if (IsSeekElideTarget(current))
				{
					if (current.AccumulatedEventIds != null && current.AccumulatedEventIds.Count > 0)
					{
						for (int i = current.AccumulatedEventIds.Count - 1; i >= 0; i--)
						{
							long accId = current.AccumulatedEventIds[i];
							eventIds.Add(accId);
						}
					}
					else if (current.Engine != null && current.Engine.IsExact)
					{
						// Exact with zero accumulated — contributes nothing.
					}
					else
					{
						eventIds.Add(current.EntryId);
					}
				}
				current = current.Parent;
			}
			eventIds.Reverse(startIndex, eventIds.Count - startIndex);
		}

		// F4: ¿el Seek de este nodo esta entre los objetivos de Elide(seek:/seeks:)?
		// null = sin filtro (cadena completa). Comparacion case-insensitive (consistente
		// con la validacion de nombres de Seek).
		private bool IsSeekElideTarget(MatchNode current)
		{
			string[] targets = reactionAction.ElideTargetSeeks;
			if (targets == null) return true;
			if (current.Engine == null) return false;
			foreach (string t in targets)
			{
				if (string.Equals(t, current.Engine.PatternDescription, System.StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
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

			// Resume/cobertura: NUNCA podar por stale un match de cobertura ABIERTO. Su ancla
			// debe sobrevivir para que la frontera-cerrada no avance por encima de ella (si se
			// podara, el resume desde la frontera la perderia). Se salta el subarbol completo.
			// Solo aplica a roots captores de cobertura (RemainingObligations != null); para
			// reactions sin ForEach es no-op y el comportamiento de pruning queda intacto.
			if (node.Parent == null && IsOpenCoverageRoot(node)) return;

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

		// FASE 2 / Fase 4.5 refactor Playbill: inyecta el value del symbol de un ancestor
		// con el nombre placeholder. Ip/User dejaron de ser simbolos scoped validos (ya no
		// viajan en el journal y por lo tanto nunca existen en un ancestor matcheado).
		private static void InjectScopedSymbol(Parameters target, string placeholder, string symbolName, MatchNode ancestor)
		{
			ArgumentNullException.ThrowIfNull(target);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(placeholder);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(symbolName);
			ArgumentNullException.ThrowIfNull(ancestor);

			switch (symbolName)
			{
				case "Now":
					target[placeholder, typeof(DateTime)] = ancestor.OccurredAt;
					break;
				case "EntryId":
					target[placeholder, typeof(int)] = checked((int)ancestor.EntryId);
					break;
				default:
					throw new LanguageException($"Simbolo scoped desconocido: '{symbolName}'. Validos: Now, EntryId.");
			}
		}

		// Evalua la clausula Where del engine contra el evento actual.
		// Retorna true si el filtro pasa, false si el match debe descartarse.
		// Fase 4.5 refactor Playbill: los simbolos validos en Where son @Now y @EntryId
		// (Ip/User dejaron de inyectarse — no viajan en el journal). El lexer trata '@'
		// como whitespace y lo descarta, asi que '@Now' parsea a 'Now' y coincide con
		// el parametro Now pre-populado por el pool.
		// FASE 2: 'SeekName.@Simbolo' se pre-procesa en Reaction a placeholders '_seek_SeekName_Simbolo'
		// que aqui se resuelven navegando parentNode.Parent hasta encontrar el MatchNode cuyo engine
		// tenga el nombre correspondiente.
		// Where compilation: el path preferido cachea el Program compilado en
		// engine.CachedWhereProgram (poblado por Reaction.CompileWhereExpressions
		// al inicio de Execute). Per-event solo se actualizan los Values del
		// Parameters dedicado al engine y se invoca el delegate compilado. El
		// path de re-parse + interpretado se conserva como fallback para
		// callsites que bypasean CompileWhereExpressions.
		private bool EvaluateWhereIfPresent(ReactionEngine engine, Parameters matchedParameters, SymbolTable symbolTable, EventData eventData, MatchNode parentNode)
		{
			ArgumentNullException.ThrowIfNull(engine);
			ArgumentNullException.ThrowIfNull(matchedParameters);
			ArgumentNullException.ThrowIfNull(symbolTable);
			ArgumentNullException.ThrowIfNull(eventData);

			if (!engine.HasWhere) return true;

			Program cached = engine.CachedWhereProgram;
			if (cached != null)
			{
				// Where compilation fast path. The Program is parsed once at startup
				// (Reaction.CompileWhereExpressions) and stored on the engine. Its
				// compiled lambda captures Parameter.instance VariableSymbol objects
				// as Expression.Constant (Parameter.ParameterInitializationExpression),
				// so the Program is bound to one specific Parameters instance for its
				// lifetime. We keep that instance on the engine and only mutate
				// values per event via the indexer, which reuses existing Parameter
				// objects (Parameters.SetParameter looks up by name and updates
				// parameter.Value, which writes instance.value — the exact field
				// the lambda reads). The lock serializes population + invocation
				// because the dedicated Parameters is engine-shared mutable state
				// and the first call also performs SolveReferences + Compile.
				lock (engine.WhereCompileLock)
				{
					Parameters dedicated = engine.GetOrCreateCachedWhereParameters();
					foreach (var param in matchedParameters)
					{
						dedicated[param.Name, param.ParameterType] = param.GetValue();
					}
					dedicated["Now", typeof(DateTime)] = eventData.OccurredAt;
					// Los operadores del interprete solo soportan int (no long). Para EntryId usamos int,
					// asumiendo que un diario real no supera 2.1B eventos en el horizonte esperado.
					dedicated["EntryId", typeof(int)] = checked((int)eventData.EntryId);
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

							InjectScopedSymbol(dedicated, placeholder, symbolName, ancestor);
						}
					}

					string output = cached.ExecuteExpression(dedicated);
					return string.IsNullOrEmpty(output);
				}
			}

			// Fallback: cache was not populated (e.g. CompileWhereExpressions did not
			// run, or the Reaction execution path bypassed the startup pass).
			// Preserves the legacy per-event re-parse + interpret behavior.
			Parameters whereParameters = actorHandler.ParametersPool.Rent();
			try
			{
				foreach (var param in matchedParameters)
				{
					whereParameters[param.Name, param.ParameterType] = param.GetValue();
				}

				whereParameters["Now", typeof(DateTime)] = eventData.OccurredAt;
				// Los operadores del interprete solo soportan int (no long). Para EntryId usamos int,
				// asumiendo que un diario real no supera 2.1B eventos en el horizonte esperado.
				whereParameters[ "EntryId", typeof(int) ] = checked((int)eventData.EntryId);

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
				program.LoadArguments(whereParameters);

				string output = program.Execute();
				return string.IsNullOrEmpty(output);
			}
			finally
			{
				whereParameters.PurgeUserParameters();
				actorHandler.ParametersPool.Return(whereParameters);
			}
		}
		// ===== RESUME OPTIMIZATION (rediseño de checkpoint, pasos 2-4) =====
		// Detalle + matriz: notes/reactions-checkpoint-policy.md.
		//
		// El modelo escalar lexicografico per-seek se descarta para cobertura: los matches
		// concurrentes multi-ancla cierran FUERA de orden y no tienen orden total. El resume
		// se gobierna por DOS cursores globales: el frente-leido (high-water = max entryId
		// escaneado, lo provee el caller) y la frontera-cerrada (closed-frontier), que es el
		// mayor entryId por debajo del cual TODA ancla de cobertura ya cerro (cobertura
		// completa + asentada + elidida). En el proximo Execute se re-lee desde la
		// frontera-cerrada en vez de génesis.

		// Frontera-cerrada = (ancla abierta mas vieja) - 1; si no hay anclas abiertas, todo lo
		// escaneado cerro -> la frontera es el frente-leido. Paso 3: .Aged parquea esta
		// frontera porque un nodo de cobertura completo-pero-pending-settle cuenta como ABIERTO
		// (ver IsOpenCoverageRoot), asi la frontera no avanza por encima de un cierre no asentado.
		internal long ComputeClosedFrontier(long highWater)
		{
			long minOpenAnchor = long.MaxValue;
			foreach (var root in roots)
			{
				if (IsOpenCoverageRoot(root) && root.EntryId < minOpenAnchor)
					minOpenAnchor = root.EntryId;
			}

			if (minOpenAnchor == long.MaxValue) return highWater;
			long cf = minOpenAnchor - 1;
			return cf < 0 ? 0 : cf;
		}

		// Un root captor de cobertura esta ABIERTO si aun le faltan obligaciones por cubrir, o
		// si su cobertura ya esta completa pero parqueada por .Aged (CoveragePendingSettle en su
		// hijo de cobertura). Cerrado = obligaciones vacias y ningun hijo pendiente de asentar
		// (ya disparo ExecuteCompleteMatch y elidio). Para reactions sin ForEach el root no tiene
		// RemainingObligations -> retorna false (no participa de la frontera).
		private static bool IsOpenCoverageRoot(MatchNode root)
		{
			if (root == null || root.RemainingObligations == null) return false;
			if (root.RemainingObligations.Count > 0) return true;
			foreach (var child in root.Children)
			{
				if (child.CoveragePendingSettle) return true;
			}
			return false;
		}

		// Paso 4: fotografia de los matches de cobertura abiertos para el cold-start de un
		// consumidor-puro de replicacion (no re-lee journal; restaura y resume en el frente).
		internal List<CoverageMatchSnapshot> SnapshotOpenCoverageMatches()
		{
			var result = new List<CoverageMatchSnapshot>();
			foreach (var root in roots)
			{
				if (!IsOpenCoverageRoot(root)) continue;

				var snap = new CoverageMatchSnapshot
				{
					AnchorEntryId = root.EntryId,
					AnchorOccurredAt = root.OccurredAt,
					RemainingObligations = root.RemainingObligations != null ? new List<string>(root.RemainingObligations) : new List<string>(),
					PendingSettle = false,
					AccumulatedConfirmIds = new List<long>(),
				};

				foreach (var child in root.Children)
				{
					if (child.CoveragePendingSettle)
					{
						snap.PendingSettle = true;
						snap.LastConfirmOccurredAt = child.LastAccumulatedOccurredAt;
						if (child.AccumulatedEventIds != null)
							snap.AccumulatedConfirmIds.AddRange(child.AccumulatedEventIds);
					}
				}

				result.Add(snap);
			}
			return result;
		}

		// Paso 4: reconstruye los roots captores abiertos desde un snapshot, ANTES de la
		// rehidratacion. El resume posterior arranca en el frente: los confirms nuevos descargan
		// las obligaciones restauradas y al vaciarse disparan la elision del ancla (cuyo entryId
		// se conserva aunque su evento no se re-lea). Un match parqueado por .Aged se restaura
		// como hijo de cobertura pending-settle para que el tick lo dispare al asentar.
		internal void RestoreOpenCoverageMatches(List<CoverageMatchSnapshot> snapshots, List<ReactionEngine> engines)
		{
			ArgumentNullException.ThrowIfNull(snapshots);
			ArgumentNullException.ThrowIfNull(engines);
			if (engines.Count == 0) return;

			var captorEngine = engines[0];
			var coverageEngine = engines[engines.Count - 1];

			foreach (var snap in snapshots)
			{
				var root = nodePool.Rent();
				root.EntryId = snap.AnchorEntryId;
				root.OccurredAt = snap.AnchorOccurredAt;
				root.CapturedParams = actorHandler.ParametersPool.Rent();
				root.Engine = captorEngine;
				root.CurrentDepth = 0;
				root.Parent = null;
				root.LastExpansionAttemptEntryId = snap.AnchorEntryId;
				root.RemainingObligations = new HashSet<string>(snap.RemainingObligations ?? new List<string>());
				roots.Add(root);
				totalNodesCreated++;

				if (snap.PendingSettle)
				{
					var child = nodePool.Rent();
					child.EntryId = snap.AnchorEntryId;
					child.OccurredAt = snap.AnchorOccurredAt;
					child.CapturedParams = actorHandler.ParametersPool.Rent();
					child.Engine = coverageEngine;
					child.CurrentDepth = engines.Count - 1;
					child.Parent = root;
					child.LastExpansionAttemptEntryId = snap.AnchorEntryId;
					child.CoveragePendingSettle = true;
					if (snap.AccumulatedConfirmIds != null)
					{
						foreach (long id in snap.AccumulatedConfirmIds)
							child.AccumulateEventId(id, snap.LastConfirmOccurredAt);
					}
					child.LastAccumulatedOccurredAt = snap.LastConfirmOccurredAt;
					root.Children.Add(child);
					totalNodesCreated++;
				}
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
