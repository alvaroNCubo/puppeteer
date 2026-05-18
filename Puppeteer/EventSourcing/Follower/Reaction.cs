using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Puppeteer.EventSourcing.Follower
{
	internal enum HydrationMode
	{
		Shared,
		Independent
	}

	public enum ReactionMode
	{
		Cue,
		Job
	}

	public enum ReactionActivation
	{
		DirectorOnly,
		CastOnly,
		Company
	}

	public class ReactionModeBuilder
	{
		private readonly Reactions reactions;
		private readonly string name;

		internal ReactionModeBuilder(Reactions reactions, string name)
		{
			this.reactions = reactions;
			this.name = name;
		}

		public ReactionActivationBuilder Cue()
		{
			return new ReactionActivationBuilder(reactions, name, ReactionMode.Cue);
		}

		public ReactionActivationBuilder Job()
		{
			return new ReactionActivationBuilder(reactions, name, ReactionMode.Job);
		}
	}

	public class ReactionActivationBuilder
	{
		private readonly Reactions reactions;
		private readonly string name;
		private readonly ReactionMode mode;

		internal ReactionActivationBuilder(Reactions reactions, string name, ReactionMode mode)
		{
			this.reactions = reactions;
			this.name = name;
			this.mode = mode;
		}

		public Reaction DirectorOnly()
		{
			return reactions.CreateReaction(name, mode, ReactionActivation.DirectorOnly);
		}

		public Reaction CastOnly()
		{
			return reactions.CreateReaction(name, mode, ReactionActivation.CastOnly);
		}

		public Reaction Company()
		{
			return reactions.CreateReaction(name, mode, ReactionActivation.Company);
		}
	}

	public class Reaction : DB.IActorEventJournalClient
	{
		private readonly string name;
		private readonly Reactions reactions;
		private readonly ActorHandler actorHandler;
		private readonly ReactionMode mode;
		private readonly ReactionActivation activation;

		private long reactionId = long.MinValue;
		private long lastProcessedEntryId = long.MinValue;
		internal long LastProcessedEntryId => lastProcessedEntryId;

		internal void RequestShutdown()
		{
			actorReactions?.RequestShutdown();
		}

		private List<ReactionEngine> reactionEngines;

		private RehydrateDirection direction;
		private bool directionSet = false;

		private bool isNew;

		private HydrationMode hydrationMode;
		private bool hydrationModenSet = false;

		private string hydrationUntilSeek = null;

		private MatchTree matchTree;
		private ReactionAction reactionAction;
		private SymbolTable symbolTable;

		private ReactionActionType actionType = ReactionActionType.None;

		private string scriptForCmd;
		private string scriptForChk;
		private Action<Parameters> _configureParameters;

		// ===== VALIDITY METADATA (PHASE 6) =====
		// Controls validity and runtime state of the reaction.
		private bool isActive = true;                      // If false, the reaction will not execute.
		private DateTime expirationDate = DateTime.MinValue; // DateTime.MinValue = no limit; any other value = expiration date.

		// ===== PARSED-PROGRAM CACHE (Commits D-E) =====
		// Cache of parsed Programs keyed by ActionId for ActionEventData.
		// - Stores only the Program with resolved references (types, IsParameter=true).
		// - Does NOT store parameter values (they are reloaded per event via LoadArguments).
		// - Implements LRU (Least Recently Used) with a 100-entry cap.
		// - Each entry contains: (parsed Program, lastAccessTick for LRU).
		// Purpose: avoid re-parsing the same script for every ActionEventData.
		private Dictionary<int, (Program program, int lastAccessTick)> cachedProgramas;
		private int cacheAccessTick = 0;
		private const int MAX_CACHE_SIZE = 100;

		// ===== OPTIMIZATION METRICS (Commit E) =====
		// Metrics that evaluate Program-cache effectiveness.
		private int cacheHits = 0;              // How many times a cached Program was reused.
		private int cacheMisses = 0;            // How many times the script had to be parsed (cache miss).
		private int parametersRegistered = 0;   // Total parameters registered into SymbolTable.
		private Stopwatch parameterRegistrationTime = new Stopwatch();  // Time spent registering parameters.

		// ===== DIAGNOSTIC METRICS (Commit G) =====
		// Metrics that monitor errors and runtime robustness.
		private int actionEventsProcessed = 0;          // Total ActionEventData processed.
		private int actionIdNotFoundErrors = 0;         // ActionIds not found in cache (events skipped).
		private int argumentsDeserializationErrors = 0; // Errors deserializing Arguments (events skipped).
		private int parseErrors = 0;                    // Parsing or validation errors (events skipped).

		private ActorReactions actorReactions;
		private readonly EventDataPool pushEventDataPool = new EventDataPool(32);

		internal Reaction(Reactions reactions, string name, ReactionMode mode, ReactionActivation activation)
		{
			ArgumentNullException.ThrowIfNull(reactions);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);

			this.name = name;
			this.direction = RehydrateDirection.Forward;
			this.reactions = reactions;
			this.actorHandler = reactions.ActorHandler;
			this.mode = mode;
			this.activation = activation;
		}

		public string Name => name;

		// ===== OBSERVABILITY EXPOSURE =====
		// Read-only view of internal counters. Choreography PerformanceTracer
		// samples these via ObservableCounter/Gauge and emits a "Reaction.Stopped"
		// Span with all of them when the reaction ends gracefully.
		// Reads are non-atomic on purpose: best-effort metrics are standard,
		// and sampling tolerates inconsistencies.
		public long ActionEventsProcessed          => actionEventsProcessed;
		public long CacheHits                      => cacheHits;
		public long CacheMisses                    => cacheMisses;
		public long ParametersRegistered           => parametersRegistered;
		public long ActionIdNotFoundErrors         => actionIdNotFoundErrors;
		public long ArgumentsDeserializationErrors => argumentsDeserializationErrors;
		public long ParseErrors                    => parseErrors;
		public TimeSpan ParameterRegistrationTime  => parameterRegistrationTime.Elapsed;
		public DateTime LastActionAt               { get; private set; }

		// Fired at the end of Execute(...) when the reaction stops gracefully
		// (cancellation, batch end, etc.). Subscribers (e.g. PerformanceTracer)
		// snapshot the counters above to emit a final Span. NOT fired on hard
		// process crash — that case relies on the periodic sampling.
		public event Action<Reaction> OnExecutionStopped;

		internal RehydrateDirection Direction => direction;

		internal ActorHandler ActorHandler => actorHandler;

		internal bool IsCued => mode == ReactionMode.Cue;

		internal ReactionMode Mode => mode;

		internal ReactionActivation Activation => activation;

		internal PatternsGroup Patterns => new PatternsGroup(this);

		internal ReadOnlyCollection<ReactionEngine> ReactionEngines => reactionEngines.AsReadOnly();

		private DiaryStorage DiaryStorage
		{
			get
			{
				if (reactions.DiaryStorage == null) throw new LanguageException("DiaryStorage is not set. Please set it before using Reactions.");

				return reactions.DiaryStorage;
			}
		}


		public Reaction ReadForward()
		{
			if (reactionEngines != null) throw new LanguageException("ReadForward() must be called before the first Seek().");

			if (this.directionSet) throw new LanguageException("The read direction has already been set; it cannot be set more than once per Reaction.");

			this.direction = RehydrateDirection.Forward;
			this.directionSet = true;

			this.reactionEngines = new List<ReactionEngine>();

			return this;
		}

		public Reaction ReadBackward()
		{
			if (mode == ReactionMode.Cue) throw new LanguageException("Cue() reactions only support ReadForward(). Push feed is forward-only.");
			if (reactionEngines != null) throw new LanguageException("ReadBackward() must be called before the first Seek().");

			if (this.directionSet) throw new LanguageException("The read direction has already been set; it cannot be set more than once per Reaction.");

			this.direction = RehydrateDirection.Backward;
			this.directionSet = true;

			this.reactionEngines = new List<ReactionEngine>();

			return this;
		}

		public Reaction WithSharedHydration(string untilSeek = null)
		{
			if (this.hydrationModenSet) throw new LanguageException("The hydration strategy cannot be set more than once per Reaction.");
			if (reactionEngines != null && reactionEngines.Count > 0) throw new LanguageException("WithSharedHydration() must be called before the first Seek().");

			this.hydrationMode = HydrationMode.Shared;
			this.hydrationModenSet = true;
			this.hydrationUntilSeek = untilSeek;

			return this;
		}

		private void ValidateUntilSeekExists()
		{
			if (hydrationUntilSeek != null)
			{
				bool found = false;
				foreach (var engine in reactionEngines)
				{
					if (string.Equals(engine.PatternDescription, hydrationUntilSeek, StringComparison.OrdinalIgnoreCase))
					{
						found = true;
						break;
					}
				}

				if (!found)
				{
					throw new LanguageException($"Seek '{hydrationUntilSeek}' specified in WithSharedHydration(untilSeek) or WithIndependentHydration(untilSeek) does not exist in this Reaction's definition.");
				}
			}
		}

		public Reaction WithIndependentHydration(string untilSeek = null)
		{
			if (this.hydrationModenSet) throw new LanguageException("The hydration strategy cannot be set more than once per Reaction.");
			if (reactionEngines != null && reactionEngines.Count > 0) throw new LanguageException("WithIndependentHydration() must be called before the first Seek().");

			this.hydrationMode = HydrationMode.Independent;
			this.hydrationModenSet = true;
			this.hydrationUntilSeek = untilSeek;

			return this;
		}

		private static readonly HashSet<string> ReservedSeekNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Now", "User", "Ip", "EntryId", "null"
		};

		private static void ValidateSeekName(string patternDescription)
		{
			if (ReservedSeekNames.Contains(patternDescription))
				throw new LanguageException($"'{patternDescription}' is a reserved word and cannot be used as a Seek name. Reserved names: {string.Join(", ", ReservedSeekNames)}.");
		}

		public ReactionEngine Seek(string patternDescription)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(patternDescription);
			ValidateSeekName(patternDescription);

			if (!directionSet) throw new LanguageException("Call ReadForward() or ReadBackward() before the first Seek().");
			if (!hydrationModenSet) throw new LanguageException("Call WithSharedHydration() or WithIndependentHydration() before the first Seek().");
			if (reactionEngines == null || reactionEngines.Count != 0) throw new LanguageException("Seek() can only be called once per Reaction and must be at the start of the pattern. Use ThenSeek()/ThenFinalSeek() for subsequent steps.");

			var engine = new ReactionEngine(this, patternDescription, isFinalSeek: false);

			this.reactionEngines.Add(engine);

			return engine;
		}

		public ReactionEngine RepeatSeek(string patternDescription)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(patternDescription);
			ValidateSeekName(patternDescription);

			if (!directionSet) throw new LanguageException("Call ReadForward() or ReadBackward() before the first RepeatSeek().");
			if (!hydrationModenSet) throw new LanguageException("Call WithSharedHydration() or WithIndependentHydration() before the first RepeatSeek().");
			if (reactionEngines == null || reactionEngines.Count != 0) throw new LanguageException("RepeatSeek() can only be called once per Reaction and must be at the start of the pattern. Use ThenSeek()/ThenFinalSeek() for subsequent steps.");

			var engine = new ReactionEngine(this, patternDescription, isFinalSeek: false);
			engine.SetRepeatSeek();

			this.reactionEngines.Add(engine);

			return engine;
		}

		public ReactionEngine ThenSeek(string patternDescription)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(patternDescription);
			ValidateSeekName(patternDescription);

			if (reactionEngines == null || reactionEngines.Count == 0) throw new LanguageException("ThenSeek() is used to add a subsequent pattern step. At the start of a Reaction you must use Seek() first.");

			if (HasFinalSeek()) throw new LanguageException("Cannot add ThenSeek() after ThenFinalSeek(). ThenFinalSeek() must be the last Seek of the Reaction.");

			var engine = new ReactionEngine(this, patternDescription, isFinalSeek: false);

			this.reactionEngines.Add(engine);

			return engine;
		}

		public ReactionEngine ThenFinalSeek(string patternDescription)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(patternDescription);
			ValidateSeekName(patternDescription);

			if (reactionEngines == null || reactionEngines.Count == 0) throw new LanguageException("ThenFinalSeek() is used to add a subsequent pattern step. At the start of a Reaction you must use Seek() first.");

			if (HasFinalSeek()) throw new LanguageException("Only one ThenFinalSeek() is allowed per Reaction, and it must be the last Seek.");

			var engine = new ReactionEngine(this, patternDescription, isFinalSeek: true);

			this.reactionEngines.Add(engine);

			return engine;
		}

		private bool HasFinalSeek()
		{
			if (reactionEngines == null) return false;

			foreach (var engine in reactionEngines)
			{
				if (engine.IsFinalSeek) return true;
			}

			return false;
		}

		private void ValidateFinalSeek()
		{
			if (reactionEngines == null || reactionEngines.Count == 0) return;

			int finalSeekCount = 0;
			int finalSeekIndex = -1;

			for (int i = 0; i < reactionEngines.Count; i++)
			{
				if (reactionEngines[i].IsFinalSeek)
				{
					finalSeekCount++;
					finalSeekIndex = i;
				}
			}

			if (finalSeekCount > 1)
			{
				throw new LanguageException($"Only one ThenFinalSeek() is allowed per Reaction, but {finalSeekCount} were found.");
			}

			if (finalSeekCount == 1 && finalSeekIndex != reactionEngines.Count - 1)
			{
				throw new LanguageException($"ThenFinalSeek() must be the last Seek of the Reaction. It was found at position {finalSeekIndex}, but the last position is {reactionEngines.Count - 1}.");
			}

			System.Diagnostics.Debug.WriteLine($"[Reaction.ValidateFinalSeek] Validation passed: FinalSeekCount={finalSeekCount}, LastSeekIndex={reactionEngines.Count - 1}, IsSingleSeek={reactionEngines.Count == 1}");
		}

		internal int MaxDepth
		{
			get
			{
				if (this.reactionEngines == null) throw new LanguageException("MaxDepth is not available until ReadForward() or ReadBackward() and at least one Seek() have been called.");

				// MaxDepth is the number of Seek/ThenSeek calls (i.e. the number of engines).
				// Each engine represents a distinct level that must match a different event.
				return this.reactionEngines.Count;
			}
		}

		private int GetSeekIndexByName(string seekName)
		{
			if (string.IsNullOrWhiteSpace(seekName))
				return -1;

			for (int i = 0; i < reactionEngines.Count; i++)
			{
				if (string.Equals(reactionEngines[i].PatternDescription, seekName, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		public void Execute(ReactionExecutionMode executionMode = ReactionExecutionMode.Batch, System.Threading.CancellationToken cancellationToken = default)
		{
			// NOTE ON REHYDRATION:
			// The current Reactions implementation is "stateless" - pattern matching
			// does not depend on actor state, only on the variable types.
			//
			// BFS (Shared) vs DFS (Independent) differ in:
			// - Search strategy (breadth-first vs depth-first).
			// - Memory usage (many active branches vs one at a time).
			// - Branch pruning (limited vs early).
			//
			// FUTURE TODO: When Reactions that modify actor state are introduced,
			// we will need to:
			// 1. In BFS: a shared rehydration for all nodes.
			// 2. In DFS: an independent rehydration for each branch.
			// 3. Implement a cache of rehydrated states.
			// 4. Add configurable memory limits.

			// Validate that the specified untilSeek exists.
			ValidateUntilSeekExists();

			// Validate ThenFinalSeek (if present, it must be the last Seek).
			ValidateFinalSeek();

			var diaryStorage = DiaryStorage;

			// Step 0: resolve the ReactionId using the Reaction's textual form.
			string formattedReaction = Write();
			this.reactionId = diaryStorage.GetOrCreateReactionId(formattedReaction);

			// Step 1: compute the maximum depth N across all ReactionEngines.
			var maxDepth = 0;
			foreach (var engine in reactionEngines)
			{
				if (engine.Patterns.Count > maxDepth)
					maxDepth = engine.Patterns.Count;
			}

			if (maxDepth == 0)
				return; // No patterns to match against.

			// Step 2: load the checkpoint for every Seek.
			// Create a CheckpointVector tied to the ReactionEngines (Seeks).
			CheckpointVector checkpointVector = new CheckpointVector(reactionEngines);

			// Load checkpoints from storage.
			// COMMIT 3C: checkpoints that point to elided events do NOT need adjustment;
			// rehydration already filters elided events automatically, and the checkpoint
			// correctly indicates "I have processed up to here" regardless of elision.
			// PHASE 5A-2: load two-phase checkpoint (detected, confirmed).
			Dictionary<int, (long detected, long confirmed)> checkpointTuples = new Dictionary<int, (long, long)>();
			int patternId = 0;
			foreach (var engine in reactionEngines)
			{
				var (detected, confirmed) = diaryStorage.GetReactionCheckpoint(reactionId, patternId);
				checkpointTuples[patternId] = (detected, confirmed);

				// Use Detected to determine where to start rehydrating from.
				if (detected > 0)
				{
					checkpointVector.Set(patternId, detected);
				}
				patternId++;
			}

			// PHASE 5A-2: detect and retry pending actions (detected > confirmed).
			RetryPendingActions(reactionId, checkpointTuples);

			// Validate monotonicity of the loaded checkpoint.
			checkpointVector.ValidateMonotonicity();

			// Get the minimum checkpoint to start rehydration from.
			long afterEntryId = checkpointVector.GetMinimum();

			// Step 3: initialize ReactionAction, the match tree, and the symbol table.
			reactionAction = new ReactionAction();
			reactionAction.ActionType = actionType;
			reactionAction.MetadataKind = metadataKind;

			// Resolve the Seek index up to which the rehydration mode applies.
			int untilSeekIndex = GetSeekIndexByName(hydrationUntilSeek);

			matchTree = new MatchTree(ActorHandler, reactionAction, hydrationMode, untilSeekIndex, checkpointVector, diaryStorage, reactionId);
			symbolTable = new SymbolTable();
			cachedProgramas = new Dictionary<int, (Program program, int lastAccessTick)>();

			// PHASE 5: register a Temporal instance as the global 'time' in the table.
			// Lets Where expressions use time.Days(14), time.Hours(3), etc. to obtain a TimeSpan.
			// A single shared instance per Reaction.Execute (Temporal is stateless).
			symbolTable.SetVariable("time", new Temporal(), typeof(Temporal));

			// PHASE 3: statically validate every engine's Where expression at startup.
			// Values for @Now/@User/@Ip/@EntryId are injected per event in MatchTree.EvaluateWhere
			// as SystemParameters. The lexer treats '@' as whitespace and discards it, so '@Now'
			// is parsed as 'Now' and matches the SystemParameters Now/Ip/User pre-populated
			// by the Parameters pool (we add EntryId as an extra one).
			CompileWhereExpressions();

			// Reset optimization metrics.
			cacheAccessTick = 0;
			cacheHits = 0;
			cacheMisses = 0;
			parametersRegistered = 0;
			parameterRegistrationTime.Reset();

			// Reset diagnostic metrics (Commit G).
			actionEventsProcessed = 0;
			actionIdNotFoundErrors = 0;
			argumentsDeserializationErrors = 0;
			parseErrors = 0;

			// Create ActorReactions wrapper to encapsulate execution logic.
			actorReactions = new ActorReactions(actorHandler, this);
			actorReactions.ResetReplayState();
			actorReactions.SetExecutionMode(executionMode);

			// In CONTINUOUS mode, register a shutdown handler tied to the CancellationToken.
			if (executionMode == ReactionExecutionMode.Continuous)
			{
				cancellationToken.Register(() =>
				{
					System.Diagnostics.Debug.WriteLine($"[Reaction] CancellationToken triggered for '{Name}'. Requesting shutdown...");
					actorReactions.RequestShutdown();
				});
			}

			// Step 4A: in Cue mode, register the callback BEFORE the batch to capture concurrent events.
			if (mode == ReactionMode.Cue)
			{
				actorReactions.ActivatePushMode();
				actorHandler.AddRecordWrittenCallback((entryId, record) =>
				{
					actorReactions.EnqueuePushEvent(entryId, record);
				});
			}

			// Steps 4B-6: DiaryStorage produces events one by one (batch catch-up).
			bool includeExposeData = true;
			diaryStorage.RehydrateFromEvent(actorReactions, afterEntryId, direction, includeExposeData);

			// Step 6B: in Cue mode, enter the push loop (filters out events already processed in the batch).
			if (mode == ReactionMode.Cue && !cancellationToken.IsCancellationRequested)
			{
				System.Diagnostics.Debug.WriteLine($"[Reaction] '{Name}' entering push mode after catch-up (lastProcessed={lastProcessedEntryId})");
				actorReactions.RunPushLoop(pushEventDataPool);
			}

			// Step 7: log optimization and diagnostic metrics.
			System.Diagnostics.Debug.WriteLine($"");
			System.Diagnostics.Debug.WriteLine($"=== Cache Performance Metrics ===");
			System.Diagnostics.Debug.WriteLine($"  Cache hits:              {cacheHits}");
			System.Diagnostics.Debug.WriteLine($"  Cache misses:            {cacheMisses}");
			System.Diagnostics.Debug.WriteLine($"  Hit ratio:               {(cacheHits + cacheMisses > 0 ? (cacheHits * 100.0 / (cacheHits + cacheMisses)).ToString("F2") : "N/A")}%");
			System.Diagnostics.Debug.WriteLine($"  Final cache size:        {cachedProgramas.Count}/{MAX_CACHE_SIZE}");
			System.Diagnostics.Debug.WriteLine($"  Parameters registered:   {parametersRegistered}");
			System.Diagnostics.Debug.WriteLine($"  Registration time:       {parameterRegistrationTime.ElapsedMilliseconds}ms");
			System.Diagnostics.Debug.WriteLine($"==================================");
			System.Diagnostics.Debug.WriteLine($"");
			System.Diagnostics.Debug.WriteLine($"=== Diagnostic Metrics (Commit G) ===");
			System.Diagnostics.Debug.WriteLine($"  ActionEvents processed:  {actionEventsProcessed}");
			System.Diagnostics.Debug.WriteLine($"  ActionId not found:      {actionIdNotFoundErrors}");
			System.Diagnostics.Debug.WriteLine($"  Arguments deser errors:  {argumentsDeserializationErrors}");
			System.Diagnostics.Debug.WriteLine($"  Parse errors:            {parseErrors}");
			System.Diagnostics.Debug.WriteLine($"======================================");
			System.Diagnostics.Debug.WriteLine($"");

			// Step 9: clean up resources (incomplete nodes, pools, etc.).
			matchTree.Clear();
			ClearProgramaCache();

			// Notify observability subscribers (e.g. PerformanceTracer) of graceful stop.
			// They snapshot ActionEventsProcessed/CacheHits/etc. to emit a final Span.
			// Wrapped to ensure a misbehaving subscriber does not poison shutdown.
			try { OnExecutionStopped?.Invoke(this); }
			catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Reaction] OnExecutionStopped subscriber threw: {ex}"); }
		}

		public Reaction SetActive(bool active)
		{
			this.isActive = active;
			return this;
		}

		public Reaction SetExpirationDate(DateTime expirationDate)
		{
			this.expirationDate = expirationDate;
			return this;
		}

		internal bool IsActive => isActive;

		internal bool IsExpired
		{
			get
			{
				if (expirationDate == DateTime.MinValue) return false;
				return DateTime.UtcNow > expirationDate;
			}
		}

		// The three planes a Reaction's Action can address. The plane
		// describes what the verb touches (the system surface). Each is
		// exposed as a property — ontology, not a builder configuration
		// step like ReadForward() / WithSharedHydration().
		//
		// Program   — `.Program.Emit(script[, when: check])` — read-only
		//             execution against the actor's libraries.
		// Causation — `.Causation.Continue(script)` — extend the program
		//             flow into another actor (`tell` only legal here).
		// Metadata  — `.Metadata.Elide()` / `.Metadata.Materialize(dest)` —
		//             journal-level bookkeeping. Elide marks observed
		//             entries as elidible (consumed by Distill).
		//             Materialize declares the entry materializes to the
		//             given destination — a delivery worker external to
		//             the actor consumes the resulting (DiaryId, ReactionId,
		//             Destination) rows (Paper 5 claim 4).
		//
		// .Distill() is NOT exposed here on purpose — Distill is
		// O(journal entero) and only runs operationally via
		// performance.Distill(), never declaratively from a Reaction.
		public ProgramPlane Program => new ProgramPlane(this);
		public CausationPlane Causation => new CausationPlane(this);
		public MetadataPlane Metadata => new MetadataPlane(this);

		// Sub-distinction inside the Metadata plane: which journal-level
		// bookkeeping verb the developer chose. Set together with
		// actionType = Metadata via SetMetadataAction.
		private MetadataKind metadataKind = MetadataKind.None;

		// Destination set by .Metadata.Materialize(destination). Read in
		// ExecuteAction / OnRehydrationCompleted to build the
		// (DiaryId, ReactionId, Destination) row consumed by the external
		// delivery worker. Null when metadataKind != Materialize.
		private string materializeDestination;

		// Plane terminator helpers — invoked by the Plane types when the
		// developer calls `.Program.Emit(...)` / `.Causation.Continue(...)`
		// / `.Metadata.Elide()` / `.Metadata.Materialize(dest)`. Build-time
		// guard: each Reaction has at most one Action — calling a second
		// plane verb throws.

		internal void SetProgramAction(string script, string when)
		{
			EnsureNoActionConfigured();
			this.scriptForCmd = script;
			this.scriptForChk = when;  // null when no `when:` was supplied
			this.actionType = ReactionActionType.Program;
		}

		internal void SetCausationAction(string script)
		{
			EnsureNoActionConfigured();
			this.scriptForCmd = script;
			this.actionType = ReactionActionType.Causation;
		}

		internal void SetMetadataAction(MetadataKind kind, string destination)
		{
			EnsureNoActionConfigured();
			this.actionType = ReactionActionType.Metadata;
			this.metadataKind = kind;
			this.materializeDestination = destination;
		}

		private void EnsureNoActionConfigured()
		{
			if (actionType != ReactionActionType.None)
			{
				throw new LanguageException($"Reaction '{name}' already has an Action plane configured ({actionType}). Each Reaction has exactly one Action; remove the redundant plane verb.");
			}
		}

		public Reaction WithParameters(Action<Parameters> configure)
		{
			ArgumentNullException.ThrowIfNull(configure);
			_configureParameters = configure;
			return this;
		}

		internal void ExecuteAction(Parameters matchedParameters, long triggeringEntryId)
		{
			switch (actionType)
			{
				case ReactionActionType.Program:
					ExecuteProgram(matchedParameters, triggeringEntryId);
					break;

				case ReactionActionType.Causation:
					ExecuteCausation(matchedParameters, triggeringEntryId);
					break;

				case ReactionActionType.Metadata:
					ExecuteMetadata();
					break;

				case ReactionActionType.None:
					// No action configured.
					break;
			}
		}

		// Program plane runtime: run script (and optional check) read-only
		// against the actor's libraries. No journal entry, no envelope.
		// The optional `when:` check is folded in here (replaces the old
		// EmitWithCheck branch): if scriptForChk is set, PerformChk runs
		// first and the script is only run when the check returns OK.
		private void ExecuteProgram(Parameters matchedParameters, long triggeringEntryId)
		{
			Parameters parameters = actorHandler.ParametersPool.Rent();
			try
			{
				if (matchedParameters != null)
				{
					foreach (var param in matchedParameters)
					{
						parameters[param.Name, param.ParameterType] = param.GetValue();
					}
				}

				_configureParameters?.Invoke(parameters);

				// Optional `when:` guard. If configured, run the check
				// first; only run the script if the check returned no
				// error.
				if (!string.IsNullOrEmpty(this.scriptForChk))
				{
					var checkResult = actorHandler.PerformChk(this.scriptForChk, parameters);
					if (!string.IsNullOrEmpty(checkResult))
					{
						return; // check failed → skip the emit
					}
				}

				actorHandler.PerformEmit(this.scriptForCmd, parameters);

				var cb = LabInstrumentation.OnReactionEmit;
				if (cb != null) cb(triggeringEntryId, this.scriptForCmd, HashParameters8(matchedParameters));
			}
			finally
			{
				actorHandler.ParametersPool.Return(parameters);
				_configureParameters = null;
			}
		}

		// Causation plane runtime: lift InReactionAction so the script's
		// `tell` statement is permitted, then run the body through
		// PerformCmd. The body journals as a regular script entry (with
		// the rendered `tell ...` sentence); the envelope is dispatched
		// and the ack handler journaled by PerformCmd's normal pipeline.
		// The flag is restored on exit so subsequent reads or commands
		// are once again subject to the tell-only-in-reaction-action
		// invariant.
		//
		// Modo follower: el invariante 1-escritor del journal canonico
		// impide que un follower escriba al journal compartido del actor.
		// Etapa 2: PerformCmd sigue corriendo igual — TellStatement.Execute
		// construye el envelope y lo enqueua en SymbolTable.PendingTells,
		// y ActorHandler.SuppressReactionJournaling gate-a el writeNewEntry
		// adentro de ExecuteCommandWithWriteLock para que la sentence NO
		// toque el journal. El drain post-lock despacha el envelope por
		// el Transport igual que en el primary; el side effect cross-actor
		// se conserva y la huella en el journal del follower desaparece.
		private void ExecuteCausation(Parameters matchedParameters, long triggeringEntryId)
		{
			Parameters parameters = actorHandler.ParametersPool.Rent();
			try
			{
				if (matchedParameters != null)
				{
					foreach (var param in matchedParameters)
					{
						parameters[param.Name, param.ParameterType] = param.GetValue();
					}
				}

				_configureParameters?.Invoke(parameters);

				actorHandler.EnterReactionActionScope();
				try
				{
					actorHandler.PerformCmd(this.scriptForCmd, parameters);
				}
				finally
				{
					actorHandler.ExitReactionActionScope();
				}

				var cb = LabInstrumentation.OnReactionTell;
				if (cb != null) cb(triggeringEntryId, this.scriptForCmd, HashParameters8(matchedParameters));
			}
			finally
			{
				actorHandler.ParametersPool.Return(parameters);
				_configureParameters = null;
			}
		}

		// Metadata plane runtime: journal-level bookkeeping with no state
		// change or causation. The MetadataKind sub-distinction selects
		// between elide (Distill substrate) and materialize (external
		// delivery worker). Both implement IMMEDIATE COMMIT: flush after
		// the match to free memory and exploit data locality.
		private void ExecuteMetadata()
		{
			if (reactionAction.EventIdsToSkip.Count == 0) return;

			var eventIds = reactionAction.EventIdsToSkip.ToArray();

			switch (metadataKind)
			{
				case MetadataKind.Elide:
					DiaryStorage.EventElisionStorage.MarkEventsAsElided(eventIds, (int)reactionId, DateTime.Now);
					LabInstrumentation.OnReactionElide?.Invoke(eventIds, (int)reactionId);
					break;

				case MetadataKind.Materialize:
					DiaryStorage.EventMaterializationStorage.MarkEventsAsMaterialized(eventIds, (int)reactionId, materializeDestination, DateTime.Now);
					break;
			}

			reactionAction.EventIdsToSkip.Clear();
			if (reactionAction.EventIdsToSkip.Capacity > 256)
				reactionAction.EventIdsToSkip.TrimExcess();
		}

		// Paper 5 Lab 3 helper. Deterministic SHA-256 truncated to 8 bytes over
		// the matched parameter set. Sorted by name to be insensitive to
		// dictionary iteration order. Null/empty input yields the 8-byte SHA of
		// the empty string, so the callback can always pass a non-null array.
		private static byte[] HashParameters8(Parameters parameters)
		{
			using var sha = System.Security.Cryptography.SHA256.Create();
			var sb = new StringBuilder();
			if (parameters != null)
			{
				var ordered = new List<(string Name, string Type, string Value)>();
				foreach (var p in parameters)
				{
					// Filter by NAME to exclude the well-known system parameters
					// (Now/Ip/User). Filtering by ParameterKind is not enough —
					// the indexer setter `parameters[name, type] = value` (used
					// in ExecuteEmit to copy matched captures into the rented
					// pool slot) ALWAYS marks the parameter as User, even when
					// the name is a system one. The pool is shared with
					// PerformEmit which sets Now=DateTime.Now, leaving the
					// wall-clock value visible to the hash. Excluding these
					// names keeps the hash a pure function of the pattern's
					// matched captures.
					if (p.Name == "Now" || p.Name == "Ip" || p.Name == "User") continue;
					object v = p.GetValue();
					ordered.Add((p.Name ?? "", p.ParameterType?.FullName ?? "", v == null ? "" : v.ToString()));
				}
				ordered.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
				foreach (var (n, t, v) in ordered)
				{
					sb.Append(n).Append('|').Append(t).Append('|').Append(v).Append(';');
				}
			}
			byte[] full = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
			byte[] truncated = new byte[8];
			Array.Copy(full, truncated, 8);
			return truncated;
		}


		private string Write()
		{
			var sb = new StringBuilder();

			sb.AppendLine($"Name: {name}");
			sb.AppendLine($"Direction: {direction}");
			if (this.hydrationModenSet)
			{
				sb.AppendLine($"HydrationMode: {hydrationMode}");
				if (hydrationUntilSeek != null)
				{
					int resolvedIndex = GetSeekIndexByName(hydrationUntilSeek);
					sb.AppendLine($"HydrationUntilSeek: {hydrationUntilSeek} (index={resolvedIndex})");
				}
			}
			sb.AppendLine();

			// Escribir cada ReactionEngine
			foreach (var engine in reactionEngines)
			{
				sb.AppendLine($"Seek: {engine.PatternDescription}");

				// Escribir los patrones de este engine
				for (int i = 0; i < engine.Patterns.Count; i++)
				{
					var pattern = engine.Patterns[i];
					sb.AppendLine($"  OnMatch[{i}]: {pattern.PatternText}");
				}

				sb.AppendLine();
			}

			return sb.ToString();
		}

		private void ConsumeEvent(int level, EventData eventData)
		{
			string script = ExtractScript(eventData);
			if (script == null) return;

			Program cachedProgram = null;

			// Update the symbol table with this event's variables.
			// Only done at level 0 to avoid duplicates.
			if (eventData is ActionEventData actionData)
			{
				// Check whether the Program is already in our local cache.
				bool isFirstTime = !cachedProgramas.ContainsKey(actionData.ActionId);

				if (isFirstTime)
				{
					SolveActionReferences(actionData);
				}
				else
				{
					SolveActionParameters(actionData);
				}

				// Get the cached Program to pass it into pattern matching.
				if (cachedProgramas.TryGetValue(actionData.ActionId, out var entry))
				{
					cachedProgram = entry.program;
				}
			}
			else if (level == 0)
			{
				UpdateSymbolTable(script);
			}

			matchTree.TryMatchAtLevel(level, eventData, reactionEngines, script, symbolTable, cachedProgram);
		}

		// ===== REFERENCE RESOLUTION FOR ActionEventData (Commits C-D-E) =====
		// Runs ONLY THE FIRST TIME an ActionId is seen (cache miss).
		// Purpose: parse the script and resolve type references and IsParameter.
		//
		// PARAMETER REGISTRATION IN SymbolTable (Commit C):
		// - IN parameters:    registered with type + value (available for pattern matching).
		// - INOUT parameters: registered with type + input value.
		// - EVAL parameters:  registered with type + transactionally computed value.
		// - OUT parameters:   registered with type ONLY (no input value).
		//   REASON: OUT parameters carry no input value, they only hold results,
		//           so they are registered without a value, just with their type for static validation.
		//
		// SEMANTIC DUALITY (Commit C):
		// Parameters play a dual role in pattern matching:
		// 1. Reference by identifier: pattern "@currency:decimal" matches the NAME @currency.
		// 2. Reference by type:        pattern "_.Pagar($x:decimal)" captures any decimal.
		// 3. A literal does NOT match an identifier: pattern "200.75" does NOT match @currency (even if its value is 200.75).
		//
		// PROGRAM CACHE (Commit E):
		// The cached Program contains:
		// - AST with resolved references (known types, IsParameter=true).
		// - Parameters with types but WITHOUT values (reloaded in SolveActionParameters).
		// - LRU policy: maximum 100 entries, evict the least recently used.
		private void SolveActionReferences(ActionEventData actionData)
		{
			// Bump the processed-ActionEvents counter (Commit G).
			actionEventsProcessed++;
			LastActionAt = DateTime.UtcNow;

			if (!actorHandler.TryGetAction(actionData.ActionId, out var entry))
			{
				actionIdNotFoundErrors++;
				System.Diagnostics.Debug.WriteLine($"[SolveActionReferences] ERROR (Commit G): ActionId={actionData.ActionId} not found in cache (total errors: {actionIdNotFoundErrors})");
				return;
			}

			// Check whether it is already cached.
			if (cachedProgramas.ContainsKey(actionData.ActionId))
			{
				cacheHits++;
				System.Diagnostics.Debug.WriteLine($"[SolveActionReferences] ActionId={actionData.ActionId} already cached (hit #{cacheHits}), skipping");
				return;
			}

			// Cache miss - we need to parse and cache.
			cacheMisses++;
			parameterRegistrationTime.Start();

			try
			{
				// The ActorHandler already has the Program parsed and compiled with its parameters defined,
				// BUT we cannot reuse it directly because it is shared.
				// We need to parse again to obtain our own Program instance.
				var parser = new Parser(actorHandler.Libraries, symbolTable);
				parser.SetSource(entry.Script);
				var program = parser.Parse(isQuery: false, isCheck: false);

				// Validate that the cache's parameter structure matches the event's,
				// and build real Parameters for IN/INOUT/EVAL from actionData.Arguments.
				var cacheParameters = entry.Program.Parameters;

				var eventParameters = new Parameters(cacheParameters.ParametersAsString());
				eventParameters.LoadArguments(actionData.Arguments);

				if (!cacheParameters.IsStructuralEquivalentTo(eventParameters)) throw new LanguageException("Parameter structure mismatch between cache and event");

				// Logging: show registered parameters.
				System.Diagnostics.Debug.WriteLine($"[SolveActionReferences] Parameters registered for ActionId={actionData.ActionId}:");
				foreach (var param in eventParameters)
				{
					if (param.Kind == ParameterKind.User)
					{
						string modifierStr = param.ParameterModifier == Parameter.In ? "IN" :
											 param.ParameterModifier == Parameter.Out ? "OUT" :
											 param.ParameterModifier == Parameter.InOut ? "INOUT" :
											 param.ParameterModifier == Parameter.Eval ? "EVAL" : "UNKNOWN";

						string valueStr = param.ParameterModifier == Parameter.Out ? "(no value - OUT)" :
										  (param.IsEmpty ? "(empty)" : param.GetValue()?.ToString() ?? "(null)");

						System.Diagnostics.Debug.WriteLine($"  - {param.Name} : {param.ParameterType.Name} [{modifierStr}] = {valueStr}");
					}
				}

				// Load parameters into the program and resolve references.
				program.CargarArgumentos(eventParameters);
				program.SolveReferences(eventParameters, withStaticValidation: true);

				// Store the Program with its Parameters in the cache.
				// IMPORTANT: Parameters stays bound to the Program and is NOT returned to the pool.
				// If the cache is full, evict the least recently used entry.
				if (cachedProgramas.Count >= MAX_CACHE_SIZE)
				{
					int lruActionId = cachedProgramas.OrderBy(kvp => kvp.Value.lastAccessTick).First().Key;
					cachedProgramas.Remove(lruActionId);
					System.Diagnostics.Debug.WriteLine($"[SolveActionReferences] Cache full, evicted ActionId={lruActionId} (LRU)");
				}

				cacheAccessTick++;
				cachedProgramas[actionData.ActionId] = (program, cacheAccessTick);

				// Count parameters for metrics.
				int userParamCount = 0;
				foreach (var param in eventParameters)
				{
					if (param.Kind == ParameterKind.User) userParamCount++;
				}
				parametersRegistered += userParamCount;
				parameterRegistrationTime.Stop();

				System.Diagnostics.Debug.WriteLine($"[SolveActionReferences] Cached ActionId={actionData.ActionId} with {userParamCount} parameters (cache size: {cachedProgramas.Count}/{MAX_CACHE_SIZE}, miss #{cacheMisses})");

				// Extract global variable declarations and add them to the symbol table.
				var declaraciones = program.Declaraciones;
				if (declaraciones != null)
				{
					foreach (var id in declaraciones)
					{
						if (id.IsGlobalVariable && id.ForcedType != null)
						{
							symbolTable.SetVariable(id.Name, null, id.ForcedType);
						}
					}
				}

				System.Diagnostics.Debug.WriteLine($"[SolveActionReferences] Cached Program for ActionId={actionData.ActionId}");
			}
			catch (Exception ex)
			{
				// Classify the error type and update metrics (Commit G).
				if (ex.Message.Contains("LoadArguments") || ex.Message.Contains("Parameter structure mismatch"))
				{
					argumentsDeserializationErrors++;
					System.Diagnostics.Debug.WriteLine($"[SolveActionReferences] ERROR (Commit G): Arguments deserialization failed for ActionId={actionData.ActionId}: {ex.Message} (total errors: {argumentsDeserializationErrors})");
				}
				else
				{
					parseErrors++;
					System.Diagnostics.Debug.WriteLine($"[SolveActionReferences] ERROR (Commit G): Parse/Validation failed for ActionId={actionData.ActionId}: {ex.Message} (total errors: {parseErrors})");
				}
			}
		}

		// ===== PARAMETER RELOAD FOR ActionEventData (Commits D-E) =====
		// Runs ON EVERY ActionEventData (after the first cache miss).
		// Purpose: reload parameter values into the cached Program.
		//
		// The Program was already parsed in SolveActionReferences (cache hit).
		// We only need to refresh parameter values from Arguments,
		// which is much more efficient than re-parsing the whole script.
		//
		// LRU optimization (Commit E):
		// - Update lastAccessTick to keep the Program in cache.
		// - Increment cacheHits for performance metrics.
		private void SolveActionParameters(ActionEventData actionData)
		{
			// Get the cached Program and update its access timestamp.
			if (!cachedProgramas.TryGetValue(actionData.ActionId, out var cacheEntry))
			{
				System.Diagnostics.Debug.WriteLine($"[SolveActionParameters] WARNING: ActionId={actionData.ActionId} not in cache");
				return;
			}

			// Update lastAccessTick for LRU (Commit E).
			cacheAccessTick++;
			cachedProgramas[actionData.ActionId] = (cacheEntry.program, cacheAccessTick);
			cacheHits++;

			try
			{
				// Reload ONLY parameter values (structure is already resolved).
				cacheEntry.program.Parameters.LoadArguments(actionData.Arguments);

				System.Diagnostics.Debug.WriteLine($"[SolveActionParameters] Reloaded parameter values for ActionId={actionData.ActionId}:");
				foreach (var param in cacheEntry.program.Parameters)
				{
					if (param.Kind == ParameterKind.User)
					{
						string modifierStr = param.ParameterModifier == Parameter.In ? "IN" :
											 param.ParameterModifier == Parameter.Out ? "OUT" :
											 param.ParameterModifier == Parameter.InOut ? "INOUT" :
											 param.ParameterModifier == Parameter.Eval ? "EVAL" : "UNKNOWN";

						string valueStr = param.ParameterModifier == Parameter.Out ? "(no value - OUT)" :
										  (param.IsEmpty ? "(empty)" : param.GetValue()?.ToString() ?? "(null)");

						System.Diagnostics.Debug.WriteLine($"  - {param.Name} : {param.ParameterType.Name} [{modifierStr}] = {valueStr}");
					}
				}
			}
			catch (Exception ex)
			{
				// Classify the error type (Commit G).
				argumentsDeserializationErrors++;
				System.Diagnostics.Debug.WriteLine($"[SolveActionParameters] ERROR (Commit G): LoadArguments failed for ActionId={actionData.ActionId}: {ex.Message} (total errors: {argumentsDeserializationErrors})");
			}
		}

		// PHASES 3+2: statically validate Where expressions at startup and pre-process
		// SeekName.@Symbol references into placeholders that are resolved at runtime.
		// Each evaluation re-parses the Where to avoid issues from compile-once caching.
		private static readonly System.Text.RegularExpressions.Regex SeekScopedRefPattern =
			new System.Text.RegularExpressions.Regex(@"(\w+)\.@(Now|User|Ip|EntryId)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

		private void CompileWhereExpressions()
		{
			if (reactionEngines == null) return;

			// PHASE 2: valid seek names used to resolve SeekName.@X references.
			var seekNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var engine in reactionEngines)
			{
				seekNames.Add(engine.PatternDescription);
			}

			foreach (var engine in reactionEngines)
			{
				if (!engine.HasWhere) continue;

				// Pre-process: rewrite patterns like 'Compra.@Now' -> '_seek_Compra_Now' and record the mapping.
				var refs = new Dictionary<string, (string seekName, string symbolName)>();
				string normalized = SeekScopedRefPattern.Replace(engine.WhereExpression, match =>
				{
					string seek = match.Groups[1].Value;
					string symbol = match.Groups[2].Value;
					if (!seekNames.Contains(seek))
					{
						// Not a registered Seek name; leave it unchanged so the parser
						// interprets it as a member access and reports the natural error (member not found).
						return match.Value;
					}
					string placeholder = "_seek_" + seek + "_" + symbol;
					refs[placeholder] = (seek, symbol);
					return placeholder;
				});

				engine.NormalizedWhereExpression = normalized;
				engine.SeekScopedRefs = refs.Count > 0 ? refs : null;

				string wrappedScript = "Check(" + normalized + ") Error '__where_failed__';";

				try
				{
					var parser = new Parser(actorHandler.Libraries, symbolTable);
					parser.SetSource(wrappedScript);
					var program = parser.Parse(isQuery: true, isCheck: false);
				}
				catch (LanguageException ex)
				{
					throw new LanguageException($"Error parsing the Where expression of Seek '{engine.PatternDescription}': {ex.Message}");
				}
			}
		}

		private void UpdateSymbolTable(string script)
		{
			try
			{
				// Parse the script using the symbol table.
				var parser = new Parser(actorHandler.Libraries, symbolTable);
				parser.SetSource(script);
				var program = parser.Parse(isQuery: false, isCheck: false);

				// We need a temporary Parameters for SolveReferences.
				// The parser adds variables during parsing, but SolveReferences
				// validates and completes type information.
				var tempParams = actorHandler.ParametersPool.Rent();
				try
				{
					program.SolveReferences(tempParams, withStaticValidation: true);

					// After resolving references and validating, extract global variable
					// declarations and add them to the symbol table with their type.
					var declaraciones = program.Declaraciones;
					if (declaraciones != null)
					{
						foreach (var id in declaraciones)
						{
							if (id.IsGlobalVariable && id.ForcedType != null)
							{
								symbolTable.SetVariable(id.Name, null, id.ForcedType);
							}
						}
					}
				}
				finally
				{
					tempParams.PurgeUserParameters();
					actorHandler.ParametersPool.Return(tempParams);
				}

				System.Diagnostics.Debug.WriteLine($"[UpdateSymbolTable] Script: {script}");
				System.Diagnostics.Debug.WriteLine($"[UpdateSymbolTable] Symbols in table: {string.Join(", ", symbolTable.Symbols)}");
			}
			catch (Exception ex)
			{
				// If the script does not parse, ignore it.
				System.Diagnostics.Debug.WriteLine($"[UpdateSymbolTable] ERROR parsing script: {script}, Error: {ex.Message}");
			}
		}

		private void OnRehydrationCompleted()
		{
			// COMMIT 2A: Checkpoint saving removed from here
			// Checkpoints are now saved per-match in MatchTree.SaveMatchCheckpoint()
			// This ensures atomicity guarantee: checkpoint is saved BEFORE executing action

			// COMMIT 3B: mark events as elided / materialized when the
			// Metadata plane is active. Elided events are redundant and
			// can be skipped in future rehydrations; materialized events
			// have been declared deliverable to an external destination.
			// SOUNDNESS: only events that complete a detected pattern
			// are marked. Safety net: if a replay batch skipped the
			// per-match ExecuteAction (because rehydration ended before
			// all matches flushed), the leftover EventIds are flushed
			// here at close.
			if (reactionAction != null && reactionAction.ActionType == ReactionActionType.Metadata && reactionId > 0)
			{
				if (reactionAction.EventIdsToSkip.Count > 0)
				{
					var diaryStorage = DiaryStorage;
					long[] eventIds = reactionAction.EventIdsToSkip.ToArray();

					switch (metadataKind)
					{
						case MetadataKind.Elide:
							diaryStorage.EventElisionStorage.MarkEventsAsElided(eventIds, (int)reactionId, DateTime.Now);
							System.Diagnostics.Debug.WriteLine($"[OnRehydrationCompleted] Marked {eventIds.Length} events as elided for reactionId={reactionId}");
							break;

						case MetadataKind.Materialize:
							if (!string.IsNullOrWhiteSpace(materializeDestination))
							{
								diaryStorage.EventMaterializationStorage.MarkEventsAsMaterialized(eventIds, (int)reactionId, materializeDestination, DateTime.Now);
								System.Diagnostics.Debug.WriteLine($"[OnRehydrationCompleted] Marked {eventIds.Length} events as materialized to '{materializeDestination}' for reactionId={reactionId}");
							}
							break;
					}
				}
			}
		}

		private void ClearProgramaCache()
		{
			if (cachedProgramas != null)
			{
				cachedProgramas.Clear();
				cachedProgramas = null;
			}
		}

		// ===== SCRIPT EXTRACTION FROM EventData (Commits A-B) =====
		// Difference between event types:
		//
		// 1. ScriptEventData (ActorV1 legacy):
		//    - Carries the full script as a string.
		//    - Used for inline commands without pre-compilation.
		//
		// 2. ActionEventData (ActorV2 compiled):
		//    - Carries an ActionId + Arguments (serialized parameters).
		//    - The script lives in ActorHandler's cache (actionCommands).
		//    - Parameters are NOT expanded as literals (IMPORTANT).
		//    - SEMANTIC DUALITY is preserved: @param is the identifier, not its literal value.
		//
		// Example ActionEventData:
		//   Cached script: "cliente.Pagar(@currency);"
		//   Arguments:     "currency=200.75"
		//   Returned script: "cliente.Pagar(@currency);" (NOT expanded to 200.75)
		//
		// Pattern matching registers @currency in SymbolTable with type+value,
		// allowing matches both by identifier (@currency) and by type (decimal).
		private string ExtractScript(EventData eventData)
		{
			switch (eventData)
			{
				case ScriptEventData scriptData:
					return scriptData.Script;

				case ActionEventData actionData:
					// Check whether the ActionId exists in the compiled-actions cache.
					if (actorHandler.TryGetAction(actionData.ActionId, out var entry))
					{
						Debug.WriteLine($"[Reaction.ExtractScript] ActionEventData found: ActionId={actionData.ActionId}, returning script from cache (with @parameters): '{entry.Script}'");
						// Return the script with @parameters (NOT expanded).
						// Parameters are registered in SymbolTable with type+value,
						// but the AST keeps the @param identifiers for pattern matching.
						return entry.Script;
					}
					else
					{
						Debug.WriteLine($"[Reaction.ExtractScript] WARNING: ActionEventData with ActionId={actionData.ActionId} not found in cache");
						return null;
					}

				default:
					return null;
			}
		}

		string DB.IActorEventJournalClient.ActorName => actorHandler.Name;

		bool DB.IActorEventJournalClient.IsNew { set => isNew = value; }

		bool DB.IActorEventJournalClient.IsActionKnown(int actionId)
		{
			// Use reflection to access the private actionCommands field.
			var actionCommandsField = typeof(ActorHandler).GetField(ActorHandler.ActionCommandsFieldName,
				System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			if (actionCommandsField == null) return false;

			var actionCommands = actionCommandsField.GetValue(actorHandler);
			var containsMethod = actionCommands.GetType().GetMethod(ActorHandler.ContainsActionMethodName);
			if (containsMethod == null) return false;

			return (bool)containsMethod.Invoke(actionCommands, new object[] { actionId });
		}

		void DB.IActorEventJournalClient.AddKnownAction(int actionId, string actionScript, string parameters)
		{
			// Delegate to ActorHandler so it adds the action to the cache.
			((DB.IActorEventJournalClient)actorHandler).AddKnownAction(actionId, actionScript, parameters);
		}

		void DB.IActorEventJournalClient.AddKnownActionFromDefine(int actionId, string defineStatementText)
		{
			// Phase 4 of the Action refactor: delegate to ActorHandler.
			((DB.IActorEventJournalClient)actorHandler).AddKnownActionFromDefine(actionId, defineStatementText);
		}
		void DB.IActorEventJournalClient.BeginJournalReplay(long totalEventsToApply)
		{
		}

		bool DB.IActorEventJournalClient.CanContinueReplay(long currentEntryId)
		{
			return true;
		}

		internal void ReplayEvent(DB.EventData eventData)
		{
			ArgumentNullException.ThrowIfNull(eventData);

			for (int level = 0; level < MaxDepth; level++)
			{
				ConsumeEvent(level, eventData);
			}

			lastProcessedEntryId = eventData.EntryId;
		}

		void DB.IActorEventJournalClient.ReplayEvent(DB.EventData eventData)
		{
			ReplayEvent(eventData);
		}

		void DB.IActorEventJournalClient.EndJournalReplay(bool forcedToEnd)
		{
			OnRehydrationCompleted();
		}

		long DB.IActorEventJournalClient.GetLastProcessedEntryId(int followerId)
		{
			return 0;
		}

		// PHASE 5A-2: automatic retry of pending actions (detected > confirmed).
		private void RetryPendingActions(long reactionId, Dictionary<int, (long detected, long confirmed)> checkpointTuples)
		{
			ArgumentNullException.ThrowIfNull(checkpointTuples);

			if (actionType != ReactionActionType.Metadata || metadataKind != MetadataKind.Elide)
			{
				// Only the Metadata-Elide subtype (old MarkAsSkip) uses
				// transactional checkpoints with gap detection.
				return;
			}

			foreach (var kvp in checkpointTuples)
			{
				int seekLevel = kvp.Key;
				long detected = kvp.Value.detected;
				long confirmed = kvp.Value.confirmed;

				if (detected > confirmed)
				{
					// Gap detected: a match was detected and saved, but the action did not execute.
					System.Diagnostics.Debug.WriteLine($"[Reaction] Gap detected at seekLevel={seekLevel}: detected={detected}, confirmed={confirmed}");

					// In this simplified model, the gap indicates that PerformCommand failed.
					// The next rehydration will NOT re-encounter this match (events already elided),
					// so the gap is IRRECOVERABLE under this design.
					//
					// OPTION: log a critical error and keep going (do not block the flow).
					System.Diagnostics.Debug.WriteLine($"[Reaction] WARNING: Unrecoverable gap. Match was detected but action failed. Manual intervention may be required.");
					System.Diagnostics.Debug.WriteLine($"[Reaction] ReactionId={reactionId}, SeekLevel={seekLevel}, DetectedEntryId={detected}, ConfirmedEntryId={confirmed}");

					// FUTURE TODO: if we implement Solution 2 (Confirmation Queue),
					// here we would consult ReactionPendingActions and retry.
				}
				else if (detected == confirmed && detected > 0)
				{
					System.Diagnostics.Debug.WriteLine($"[Reaction] Checkpoint OK at seekLevel={seekLevel}: detected={detected}, confirmed={confirmed}");
				}
			}
		}
	}
}
