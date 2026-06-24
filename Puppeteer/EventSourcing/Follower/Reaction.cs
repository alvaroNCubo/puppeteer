using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Interpreter;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

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

		// IActorIntrospection (ShowReactions / ShowReaction) reads the
		// reactionId assigned during Execute() to query the DiaryStorage
		// checkpoint tuple per Seek. Returns long.MinValue when the Reaction
		// has never been executed; the introspection surface treats that as
		// (detected=0, confirmed=0) since no checkpoint row exists yet.
		internal long ReactionId => reactionId;

		internal void RequestShutdown()
		{
			actorReactions?.RequestShutdown();
		}

		private List<ReactionEngine> reactionEngines;

		private bool isNew;

		private HydrationMode hydrationMode;
		private bool hydrationModenSet = false;

		private string hydrationUntilSeek = null;

		// Universal quantifier of the Reaction: (a,b,c) in $x × $y × $z. Declared
		// after the Seek that captures the source collections; the set of
		// obligations (cartesian product) is materialized when that Seek matches (F1b).
		private ForEachSpec forEachSpec = null;

		// Resume optimization (checkpoint redesign, step 4). Opt-in for the
		// "Cue / Replication / pure-consumer" row of the matrix: the push transport does NOT
		// rewind, so the cold-start cannot re-read. Instead the open coverage matches are
		// restored from a snapshot and resumed at the frontier. For Job/Cue with a
		// local journal (default) it is NOT enabled: there the resume is re-reading [closed, high-water].
		private bool useSnapshotResume = false;

		private MatchTree matchTree;
		private ReactionAction reactionAction;
		// SymbolTable scope invariant: Reaction owns a SymbolTable INSTANCE
		// SEPARATE from actorHandler.symbolTable. It is created in Execute()
		// and populated with Reaction-only globals ('time' = Temporal helper
		// for time.Days/Hours/..., plus per-engine globals registered during
		// Action parsing via UpdateSymbolTableFromProgram). Any Parser used
		// to parse Reaction-scope scripts (event scripts, Where expressions,
		// cached Action programs) MUST be constructed against THIS instance
		// — a parser bound to actorHandler.symbolTable cannot resolve 'time'
		// (or any Reaction-only global) and silently types it as Object,
		// breaking static validation. This is why the Parser pool lives on
		// Reaction itself, not on ActorHandler.
		private SymbolTable symbolTable;
		// Per-Reaction Parser pool. Bound to this Reaction's local symbolTable
		// (which holds 'time' and per-engine globals — distinct from
		// actorHandler.symbolTable). Allocated alongside symbolTable in
		// Execute() and reused for every script parse during that Execute
		// (event scripts in Pattern.Match, Action cache misses in
		// SolveActionReferences, Where pre-compile in CompileWhereExpressions,
		// and UpdateSymbolTable). Eliminates per-event Parser allocations
		// in the hot path.
		internal ActorHandler.ConcurrentParsersPool ParsersPool;

		private ReactionActionType actionType = ReactionActionType.None;

		private string scriptForCmd;
		private string scriptForChk;
		// Check of a Causation.Continue(check:, script). Unlike
		// scriptForChk (the `when:` of Program.Emit, which is evaluated locally
		// before the emit), this check is NOT evaluated here: it travels in the
		// TellEnvelope.Check so the RECEIVER runs it as CheckThenCommand.
		private string causationCheck;
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

		// B.2: Reaction-level match cache. Keyed by (Pattern, parsed Program
		// AST, initial Parameters signature, expose data JSON). Negative
		// caching included: failed matches are stored with Matched=false so
		// repeated events with identical inputs skip the matcher entirely.
		// Single-threaded access assumed (consistent with cachedProgramas
		// above); the cache lives on the Reaction so multiple Patterns of
		// the same Reaction share the dictionary while remaining
		// distinguishable via the Pattern reference in the key.
		private readonly ReactionMatchCache matchCache = new ReactionMatchCache();
		internal ReactionMatchCache MatchCache => matchCache;

		// Phase A: aggregate count of complete matches (OnMatch chain reached
		// the final Seek and the action was about to execute). Plus a fixed-
		// size ring buffer of the last N MatchSnapshots, capturing the
		// bindings and chain at the moment of detection. The ring is
		// in-memory only; lock is taken on every append and on every read
		// (snapshot copy is returned). Capacity intentionally fixed: 32
		// covers typical retrospective-assertion windows without growing
		// per-Reaction memory unbounded across long-running pods.
		private const int LastMatchesCapacity = 32;
		private long matchCount;
		private readonly MatchSnapshot[] lastMatchesBuffer = new MatchSnapshot[LastMatchesCapacity];
		private int lastMatchesCursor;
		private int lastMatchesCount;
		private readonly object lastMatchesLock = new object();

		// Shadow Replay — S3 (skip-preview). EntryIds that Elide reactions would mark
		// in dry-run mode over a shadow, WITHOUT committing the elision. Accumulated by
		// RecordWouldSkip; reset by ResetCounters.
		private readonly List<long> wouldSkip = new List<long>();
		private readonly object wouldSkipLock = new object();

		internal Reaction(Reactions reactions, string name, ReactionMode mode, ReactionActivation activation)
		{
			ArgumentNullException.ThrowIfNull(reactions);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);

			this.name = name;
			// Reactions replay the journal forward only — the append-only substrate
			// has a single natural reading order. reactionEngines is allocated here
			// (formerly inside ReadForward/ReadBackward) so the list is ready before
			// the first Seek(); an empty list is the "no Seek declared yet" state.
			this.reactionEngines = new List<ReactionEngine>();
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
		// B.2: match-cache observability. CacheHits/CacheMisses above count the
		// parsed-Program LRU (Commit E); these count the new per-Pattern match
		// cache (skip the matcher entirely on hit).
		public long MatchCacheHits                 => matchCache.Hits;
		public long MatchCacheMisses               => matchCache.Misses;
		public int MatchCacheSize                  => matchCache.Count;
		public long ParametersRegistered           => parametersRegistered;
		public long ActionIdNotFoundErrors         => actionIdNotFoundErrors;
		public long ArgumentsDeserializationErrors => argumentsDeserializationErrors;
		public long ParseErrors                    => parseErrors;
		public TimeSpan ParameterRegistrationTime  => parameterRegistrationTime.Elapsed;
		public DateTime LastActionAt               { get; private set; }

		// Phase A observability. MatchCount counts complete-match detections
		// (advance through the final Seek); LastMatches returns the most
		// recent N snapshots in chronological order (oldest first, newest
		// last). Reads are snapshot copies, so the caller can iterate without
		// holding any lock. ResetCounters() is the in-memory reset for tests
		// or operational tooling that wants a fresh zero baseline without
		// re-invoking Execute(); cero I/O.
		public long MatchCount => Interlocked.Read(ref matchCount);

		public IReadOnlyList<MatchSnapshot> LastMatches
		{
			get
			{
				lock (lastMatchesLock)
				{
					int count = lastMatchesCount;
					if (count == 0) return Array.Empty<MatchSnapshot>();

					var result = new MatchSnapshot[count];
					int start = (count < LastMatchesCapacity) ? 0 : lastMatchesCursor;
					for (int i = 0; i < count; i++)
					{
						result[i] = lastMatchesBuffer[(start + i) % LastMatchesCapacity];
					}
					return result;
				}
			}
		}

		// Shadow Replay — S3. WouldSkip exposes, in skip-preview mode (dry-run over a
		// shadow), the EntryIds that Elide reactions would mark WITHOUT committing the
		// elision. It is to Elide what LastMatches is to matches: an observable buffer
		// of "what it would elide", not a persisted effect. Read = snapshot copy.
		public IReadOnlyList<long> WouldSkip
		{
			get
			{
				lock (wouldSkipLock)
				{
					return wouldSkip.ToArray();
				}
			}
		}

		public void ResetCounters()
		{
			Interlocked.Exchange(ref matchCount, 0);

			if (reactionEngines != null)
			{
				foreach (var engine in reactionEngines)
				{
					engine.ResetSeekCounters();
				}
			}

			lock (lastMatchesLock)
			{
				for (int i = 0; i < LastMatchesCapacity; i++)
				{
					lastMatchesBuffer[i] = null;
				}
				lastMatchesCursor = 0;
				lastMatchesCount = 0;
			}

			lock (wouldSkipLock)
			{
				wouldSkip.Clear();
			}
		}

		// Called from MatchTree.ExecuteCompleteMatch the moment the chain is
		// fully resolved (after CollectAllParametersFromChain, before
		// ExecuteAction). Bindings are filtered to exclude Now/User/Ip so
		// the snapshot reflects domain captures only — mirrors the filter
		// applied by HashParameters8 for the lab callbacks.
		internal void RecordCompleteMatch(long triggeringEntryId, DateTime occurredAt, long[] chain, Parameters parameters)
		{
			ArgumentNullException.ThrowIfNull(chain);

			Interlocked.Increment(ref matchCount);

			var bindings = new Dictionary<string, object>();
			if (parameters != null)
			{
				foreach (var p in parameters)
				{
					if (p.Name == "Now" || p.Name == "User" || p.Name == "Ip") continue;
					bindings[p.Name ?? string.Empty] = p.GetValue();
				}
			}

			var snapshot = new MatchSnapshot(triggeringEntryId, occurredAt, chain, bindings);

			lock (lastMatchesLock)
			{
				lastMatchesBuffer[lastMatchesCursor] = snapshot;
				lastMatchesCursor = (lastMatchesCursor + 1) % LastMatchesCapacity;
				if (lastMatchesCount < LastMatchesCapacity) lastMatchesCount++;
			}
		}

		// Shadow Replay — S3. Called from MatchTree.ExecuteCompleteMatch when the
		// shadow runs in skip-preview: captures the batch of EntryIds that Elide
		// would mark, WITHOUT committing (no MarkEventsAsElidedWithCheckpoint).
		internal void RecordWouldSkip(long[] batch)
		{
			ArgumentNullException.ThrowIfNull(batch);
			lock (wouldSkipLock)
			{
				wouldSkip.AddRange(batch);
			}
		}

		// Fired at the end of Execute(...) when the reaction stops gracefully
		// (cancellation, batch end, etc.). Subscribers (e.g. PerformanceTracer)
		// snapshot the counters above to emit a final Span. NOT fired on hard
		// process crash — that case relies on the periodic sampling.
		public event Action<Reaction> OnExecutionStopped;


		internal ActorHandler ActorHandler => actorHandler;

		internal bool IsCued => mode == ReactionMode.Cue;

		internal ReactionMode Mode => mode;

		internal ReactionActivation Activation => activation;

		// Decide whether an activation runs given the node's live role. DirectorOnly
		// only on the director/primary; CastOnly only on the Cast/follower; Company
		// on both.
		private static bool ActivationAllowsRole(ReactionActivation activation, bool isActingAsDirector)
		{
			switch (activation)
			{
				case ReactionActivation.DirectorOnly: return isActingAsDirector;
				case ReactionActivation.CastOnly: return !isActingAsDirector;
				case ReactionActivation.Company: return true;
				default: throw new LanguageException($"Unknown ReactionActivation '{activation}'.");
			}
		}

		internal PatternsGroup Patterns => new PatternsGroup(this);

		// IActorIntrospection (ShowReaction) reads the hydration configuration
		// to format it as a single readable string like "Shared(untilSeek: 'X')".
		// Default is Shared with no untilSeek; the bool tells us if the user
		// explicitly set it, but for the introspection surface we just report
		// the effective values, not whether they were defaulted.
		internal HydrationMode HydrationMode => hydrationMode;
		internal string HydrationUntilSeek => hydrationUntilSeek;

		// IActorIntrospection (ShowReaction) reads the Action plane configuration
		// to render a human-readable "action" string like "Metadata.Elide" or
		// "Program.Emit" or "None". The MetadataKind sub-distinction is exposed
		// so the introspection surface can also note Materialize destinations.
		internal ReactionActionType ActionType => actionType;
		internal MetadataKind MetadataKind => metadataKind;
		internal string MaterializeDestination => materializeDestination;

		internal ReadOnlyCollection<ReactionEngine> ReactionEngines => reactionEngines.AsReadOnly();

		// IActorIntrospection (ShowReactions) walks every defined Reaction;
		// a Reaction with no Seek declared yet has an empty reactionEngines
		// list. The introspection surface tolerates partial definitions:
		// report what is there.
		internal IReadOnlyList<ReactionEngine> ReactionEnginesOrEmpty => reactionEngines;

		private DiaryStorage DiaryStorage
		{
			get
			{
				if (reactions.DiaryStorage == null) throw new LanguageException("DiaryStorage is not set. Please set it before using Reactions.");

				return reactions.DiaryStorage;
			}
		}


		public Reaction WithSharedHydration(string untilSeek = null)
		{
			if (this.hydrationModenSet) throw new LanguageException("The hydration strategy cannot be set more than once per Reaction.");
			if (reactionEngines.Count > 0) throw new LanguageException("WithSharedHydration() must be called before the first Seek().");

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
			if (reactionEngines.Count > 0) throw new LanguageException("WithIndependentHydration() must be called before the first Seek().");

			this.hydrationMode = HydrationMode.Independent;
			this.hydrationModenSet = true;
			this.hydrationUntilSeek = untilSeek;

			return this;
		}

		internal ForEachSpec ForEachSpec => forEachSpec;

		// Universal quantifier: declares the tuple and the cartesian product of
		// captured collections. Invoked AFTER the Seek that captures the sources
		// (the $vars must be captured so F1b can materialize the product). In
		// F1a it only parses/validates/stores the spec; the materialization and the binding
		// of the tuple variables to the subsequent Seeks arrive in F1b.
		public Reaction ForEach(string spec)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(spec);

			if (this.forEachSpec != null) throw new LanguageException("ForEach() can be declared only once per Reaction.");
			if (reactionEngines.Count == 0) throw new LanguageException("ForEach() must be declared after the Seek that captures the source collections.");

			this.forEachSpec = ForEachSpec.Parse(spec);

			return this;
		}

		// Resume optimization (step 4): opt-in for the snapshot-based cold-start for the
		// pure-consumer replication topology (no local journal to re-read). Must be called before
		// the first Seek(), like the hydration modifiers.
		public Reaction WithSnapshotResume()
		{
			if (reactionEngines.Count > 0) throw new LanguageException("WithSnapshotResume() must be called before the first Seek().");

			useSnapshotResume = true;
			return this;
		}

		// Resume optimization: the closed-frontier / snapshot only apply to coverage
		// reactions (ForEach) that elide. The rest continue with the per-seek scalar checkpoint.
		private bool IsCoverageElide => forEachSpec != null
			&& actionType == ReactionActionType.Metadata
			&& metadataKind == MetadataKind.Elide;

		// Step 5 of the matrix: a Shadow (SkipPreview) does NOT touch the checkpoint — it neither commits (the
		// dry-run branch of ExecuteCompleteMatch returns before the commit) nor resumes; it replays one-shot
		// from genesis. That is why the frontier/snapshot resume is disabled under SkipPreview.
		private bool UseClosedFrontierResume => IsCoverageElide && !actorHandler.SkipPreviewEnabled;

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

			if (!hydrationModenSet) throw new LanguageException("Call WithSharedHydration() or WithIndependentHydration() before the first Seek().");
			if (reactionEngines.Count != 0) throw new LanguageException("Seek() can only be called once per Reaction and must be at the start of the pattern. Use ThenSeek()/ThenFinalSeek() for subsequent steps.");

			var engine = new ReactionEngine(this, patternDescription, isFinalSeek: false);

			this.reactionEngines.Add(engine);

			return engine;
		}

		public ReactionEngine ThenSeek(string patternDescription)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(patternDescription);
			ValidateSeekName(patternDescription);

			if (reactionEngines.Count == 0) throw new LanguageException("ThenSeek() is used to add a subsequent pattern step. At the start of a Reaction you must use Seek() first.");

			if (HasFinalSeek()) throw new LanguageException("Cannot add ThenSeek() after ThenFinalSeek(). ThenFinalSeek() must be the last Seek of the Reaction.");

			var engine = new ReactionEngine(this, patternDescription, isFinalSeek: false);

			this.reactionEngines.Add(engine);

			return engine;
		}

		public ReactionEngine ThenFinalSeek(string patternDescription)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(patternDescription);
			ValidateSeekName(patternDescription);

			if (reactionEngines.Count == 0) throw new LanguageException("ThenFinalSeek() is used to add a subsequent pattern step. At the start of a Reaction you must use Seek() first.");

			if (HasFinalSeek()) throw new LanguageException("Only one ThenFinalSeek() is allowed per Reaction, and it must be the last Seek.");

			var engine = new ReactionEngine(this, patternDescription, isFinalSeek: true);

			this.reactionEngines.Add(engine);

			return engine;
		}

		private bool HasFinalSeek()
		{
			foreach (var engine in reactionEngines)
			{
				if (engine.IsFinalSeek) return true;
			}

			return false;
		}

		private void ValidateFinalSeek()
		{
			if (reactionEngines.Count == 0) return;

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

		// K.2: exact-family quantifiers (None/One/Exactly) are undecidable without
		// a closing point in an open journal. They require .Within(...) which defines the
		// range (EntryIds or TimeSpan) where the quantifier is evaluated. Without Within,
		// the count never finalizes and the fire stays pending forever.
		private void ValidateExactRequiresWithin()
		{
			for (int i = 0; i < reactionEngines.Count; i++)
			{
				var engine = reactionEngines[i];
				if (engine.IsExact && !engine.HasWithinWindow)
				{
					throw new LanguageException(
						$"Seek '{engine.PatternDescription}' uses an exact-family quantifier (None/One/Exactly) without .Within(...). " +
						"Exact counts are indecidible in an open journal without a closing window — chain .Within(entries) or .Within(span) after the quantifier.");
				}
			}
		}

		internal int MaxDepth
		{
			get
			{
				if (this.reactionEngines.Count == 0) throw new LanguageException("MaxDepth is not available until at least one Seek() has been called.");

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
			// ReactionActivation gate: the Reaction only runs if its activation
			// matches the node's live role. DirectorOnly only on the
			// director/primary; CastOnly only on a Cast/follower; Company on
			// both. The role is provided by Choreography (Stage.IsDirector /
			// Performance.!isFollower); a standalone actor defaults to director.
			// This is the single chokepoint (invoked by both Reactions.Execute and the
			// Cued/Continuous path), so a replicated fan-out does not re-fire on the
			// wrong node.
			if (!ActivationAllowsRole(activation, actorHandler.IsActingAsDirector))
				return;

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

			// K.2: Exact-family quantifiers (None/One/Exactly) require .Within(...)
			// — without a window there is no closing point in an open journal.
			ValidateExactRequiresWithin();

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
			reactionAction.ElideTargetSeeks = elideTargetSeeks;

			// Resolve the Seek index up to which the rehydration mode applies.
			int untilSeekIndex = GetSeekIndexByName(hydrationUntilSeek);

			matchTree = new MatchTree(ActorHandler, reactionAction, hydrationMode, untilSeekIndex, checkpointVector, diaryStorage, reactionId, forEachSpec: forEachSpec);
			symbolTable = new SymbolTable();
			cachedProgramas = new Dictionary<int, (Program program, int lastAccessTick)>();

			// Resume optimization (checkpoint redesign, steps 2-4 — notes/reactions-checkpoint-policy.md).
			// For coverage the resume is NOT from genesis: it is governed by the closed-frontier (local
			// re-read) or a snapshot (pure-consumer). It is decided here, with the matchTree already created, because
			// the restore injects the open-match nodes BEFORE rehydration.
			if (UseClosedFrontierResume && reactionId > 0)
			{
				var (highWater, closedFrontier) = diaryStorage.GetReactionFrontier(reactionId);

				bool restoredFromSnapshot = false;
				if (useSnapshotResume)
				{
					var snapshots = CoverageSnapshotCodec.Decode(diaryStorage.GetReactionMatchSnapshot(reactionId));
					if (snapshots.Count > 0)
					{
						matchTree.RestoreOpenCoverageMatches(snapshots, reactionEngines);
						restoredFromSnapshot = true;
					}
				}

				// Pure-consumer with restored snapshot: the transport does not rewind -> resume
				// at the frontier, without re-reading. In any other case (local Job/Cue, or empty snapshot) the
				// window [closed, high-water] is re-read; closedFrontier=0 on the first Execute => genesis.
				afterEntryId = restoredFromSnapshot ? highWater : closedFrontier;
			}

			// PHASE 5: register a Temporal instance as the global 'time' in the table.
			// Lets Where expressions use time.Days(14), time.Hours(3), etc. to obtain a TimeSpan.
			// A single shared instance per Reaction.Execute (Temporal is stateless).
			symbolTable.SetVariable("time", new Temporal(), typeof(Temporal));

			// Per-Reaction Parser pool. Created AFTER symbolTable so the pool's
			// constructor binds to the SymbolTable that holds 'time' and the
			// other Reaction-local globals. actorHandler.ParsersPool cannot be
			// reused here because it is bound to a different SymbolTable.
			ParsersPool = new ActorHandler.ConcurrentParsersPool(actorHandler.Libraries, symbolTable);

			// PHASE 3: statically validate every engine's Where expression at startup.
			// Phase 4.5 Playbill refactor: the valid symbols in Where are @Now and @EntryId
			// (Ip/User are no longer injected). The lexer treats '@' as whitespace and
			// discards it, so '@Now' parses to 'Now' and matches the Now parameter
			// pre-populated by the pool; EntryId is injected per-event.
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

			// Reset Phase A counters so each Execute() starts from zero.
			// Equivalent in scope to ResetCounters() but happens automatically
			// at the natural entry point. Tests that snapshot before/after a
			// single Execute() see MatchCount and SeekEntered/Matched as deltas.
			ResetCounters();

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
			diaryStorage.RehydrateFromEvent(actorReactions, afterEntryId, includeExposeData);

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

			// Resume optimization (checkpoint redesign, steps 2-4). Persist the two global
			// cursors BEFORE clearing the matchTree (the closed-frontier is computed from its open
			// roots). Coverage only, never under Shadow/SkipPreview (step 5).
			if (UseClosedFrontierResume && reactionId > 0)
			{
				var (prevHighWater, prevClosedFrontier) = diaryStorage.GetReactionFrontier(reactionId);

				// high-water = max entryId scanned (monotonic across Executes). lastProcessedEntryId
				// is long.MinValue if no event was replayed; it is clamped to >= 0.
				long scanned = lastProcessedEntryId > 0 ? lastProcessedEntryId : 0;
				long highWater = Math.Max(prevHighWater, scanned);

				// closed-frontier = (oldest open anchor)-1, or high-water if everything closed. Monotonic
				// clamp: it never moves backward (re-reading too much is correct/idempotent, but moving
				// the persisted frontier backward would only waste work in the next Execute).
				long closedFrontier = matchTree.ComputeClosedFrontier(highWater);
				if (closedFrontier < prevClosedFrontier) closedFrontier = prevClosedFrontier;

				diaryStorage.SaveReactionFrontier(reactionId, highWater, closedFrontier);

				// Step 4: snapshot of open matches for the pure-consumer cold-start.
				if (useSnapshotResume)
				{
					var openMatches = matchTree.SnapshotOpenCoverageMatches();
					diaryStorage.SaveReactionMatchSnapshot(reactionId, CoverageSnapshotCodec.Encode(openMatches));
				}
			}

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
		// step like WithSharedHydration().
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

		// Outbox — `.Outbox.Emit(destination, payload)` — journal-outbox emit. A
		// write-mode emit: records the outgoing message atomically with the cursor
		// (see OutboxPlane / notes/reactions-outbox-emit.md). New plane, not a
		// MetadataKind: it carries an outgoing payload, not journal bookkeeping.
		public OutboxPlane Outbox => new OutboxPlane(this);

		// Sub-distinction inside the Metadata plane: which journal-level
		// bookkeeping verb the developer chose. Set together with
		// actionType = Metadata via SetMetadataAction.
		private MetadataKind metadataKind = MetadataKind.None;

		// Destination set by .Metadata.Materialize(destination). Read in
		// ExecuteAction / OnRehydrationCompleted to build the
		// (DiaryId, ReactionId, Destination) row consumed by the external
		// delivery worker. Null when metadataKind != Materialize.
		private string materializeDestination;

		// F4 Elide(seek:/seeks:): Seeks whose entryIds are elided. null = full chain.
		private string[] elideTargetSeeks;

		// Outbox plane (.Outbox.Emit). Destination + payload template recorded into
		// the diary's outbox table when a match completes. Both null when the
		// action is not an Outbox emit.
		private string outboxDestination;
		private string outboxPayloadTemplate;

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
			SetCausationAction(script, null);
		}

		internal void SetCausationAction(string script, string check)
		{
			EnsureNoActionConfigured();
			this.scriptForCmd = script;
			this.causationCheck = check;  // null when no check: was supplied
			this.actionType = ReactionActionType.Causation;
		}

		internal void SetMetadataAction(MetadataKind kind, string destination, string[] elideSeeks = null)
		{
			EnsureNoActionConfigured();
			this.actionType = ReactionActionType.Metadata;
			this.metadataKind = kind;
			this.materializeDestination = destination;

			// F4: validate that each target Seek of Elide(seek:/seeks:) exists. Elide is the
			// terminator (after all the ThenSeek), so reactionEngines is already complete.
			if (elideSeeks != null)
			{
				foreach (string seekName in elideSeeks)
				{
					bool found = false;
					foreach (var engine in reactionEngines)
					{
						if (string.Equals(engine.PatternDescription, seekName, StringComparison.OrdinalIgnoreCase))
						{
							found = true;
							break;
						}
					}
					if (!found)
						throw new LanguageException($"Elide(seek/seeks): the Seek '{seekName}' does not exist in this Reaction. Seeks: {string.Join(", ", reactionEngines.ConvertAll(e => e.PatternDescription))}.");
				}
			}
			this.elideTargetSeeks = elideSeeks;
		}

		internal void SetOutboxAction(string destination, string payload)
		{
			EnsureNoActionConfigured();
			this.actionType = ReactionActionType.Outbox;
			this.outboxDestination = destination;
			this.outboxPayloadTemplate = payload;
		}

		private void EnsureNoActionConfigured()
		{
			if (actionType != ReactionActionType.None)
			{
				throw new LanguageException($"Reaction '{name}' already has an Action plane configured ({actionType}). Each Reaction has exactly one Action; remove the redundant plane verb.");
			}
		}

		internal bool IsOutboxEmit => actionType == ReactionActionType.Outbox;
		internal string OutboxDestination => outboxDestination;

		// Render the recorded payload: substitute `@name` tokens in the template
		// with the matched parameter values. Deterministic — a fixed match yields a
		// fixed payload. Unknown tokens are left as-is (the recorded data stays
		// inspectable); well-known non-domain params (Now/Ip/User) are not used.
		internal string RenderOutboxPayload(Parameters matched)
		{
			if (string.IsNullOrEmpty(outboxPayloadTemplate) || matched == null)
				return outboxPayloadTemplate ?? string.Empty;

			string rendered = outboxPayloadTemplate;
			foreach (var p in matched)
			{
				if (p.Name == "Now" || p.Name == "Ip" || p.Name == "User") continue;
				object value = p.GetValue();
				rendered = rendered.Replace("@" + p.Name, value?.ToString() ?? string.Empty);
			}
			return rendered;
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

				case ReactionActionType.Outbox:
					// No-op here on purpose: the recording (outbox row insert) is
					// committed atomically with the cursor in MatchTree's outbox
					// branch BEFORE this runs. There is no separate external effect
					// in the reaction path — the relay (actor.Outbox) delivers.
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
		// Follower mode: the single-writer invariant of the canonical journal
		// prevents a follower from writing to the actor's shared journal.
		// Stage 2: PerformCmd still runs the same — TellStatement.Execute
		// builds the envelope and enqueues it into SymbolTable.PendingTells,
		// and ActorHandler.SuppressReactionJournaling gates the writeNewEntry
		// inside ExecuteCommandWithWriteLock so the sentence does NOT
		// touch the journal. The post-lock drain dispatches the envelope over
		// the Transport just as on the primary; the cross-actor side effect
		// is preserved and the footprint in the follower's journal disappears.
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
					// The check (if any) is NOT evaluated here: it is baked into the
					// TellEnvelope.Check that TellStatement.Execute builds during
					// this PerformCmd, so the RECEIVER runs it as
					// CheckThenCommand. It is always cleared after the body.
					actorHandler.CausationTellCheck = this.causationCheck;
					try
					{
						actorHandler.PerformCmd(this.scriptForCmd, parameters);
					}
					finally
					{
						actorHandler.CausationTellCheck = null;
					}
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
					// Filter by NAME to exclude well-known non-domain parameters.
					// Now is the only special parameter still live after Phase 4.5 (Ip/User
					// are no longer injected, but we filter them defensively in case
					// some legacy script still passes them as user parameters).
					// The pool is shared with PerformEmit which sets Now=DateTime.Now,
					// leaving the wall-clock visible to the hash; excluding these names
					// keeps the hash a pure function of the matched captures.
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

			// Write each ReactionEngine
			foreach (var engine in reactionEngines)
			{
				sb.AppendLine($"Seek: {engine.PatternDescription}");

				// Write this engine's patterns
				for (int i = 0; i < engine.Patterns.Count; i++)
				{
					var pattern = engine.Patterns[i];
					sb.AppendLine($"  OnMatch[{i}]: {pattern.PatternText}");
				}

				sb.AppendLine();
			}

			return sb.ToString();
		}

		// PERF (Tier 1): per-event resolution, hoisted out of the level loop in
		// ReplayEvent. Returns false (and a null script) when the event cannot be
		// matched (e.g. ActionId not found in cache), so the caller skips the loop.
		// Everything here is level-independent: script extraction, cached-Program
		// resolution, parameter (re)load and the one-time symbol-table update that
		// previously sat behind `level == 0`.
		private bool ResolveEventForMatching(EventData eventData, out string script, out Program cachedProgram, out bool cachedProgramIsCanonical)
		{
			cachedProgram = null;
			cachedProgramIsCanonical = false;

			script = ExtractScript(eventData);
			if (script == null) return false;

			if (eventData is ActionEventData actionData)
			{
				// Check whether the Program is already in our local cache.
				bool isFirstTime = !cachedProgramas.ContainsKey(actionData.ActionId);

				if (isFirstTime)
				{
					// First sighting of this ActionId: parse, register references and
					// load THIS event's argument values (SolveActionReferences does the
					// LoadArguments internally), then cache the Program.
					SolveActionReferences(actionData);
				}
				else
				{
					// Cached structure: reload only this event's parameter values.
					SolveActionParameters(actionData);
				}

				// Get the cached Program to pass it into pattern matching.
				if (cachedProgramas.TryGetValue(actionData.ActionId, out var entry))
				{
					cachedProgram = entry.program;
					// Canonical: same Program reference is reused across every event
					// with this ActionId, so MatchCache entries on (Program, ...)
					// have meaningful hit rates.
					cachedProgramIsCanonical = true;
				}
			}
			else
			{
				// B.2 ext: last-executed-script fast path. Push-mode Cue/Job
				// Reactions typically consume an entry within microseconds of
				// the writer publishing it, so the entry just executed under
				// the actor's write lock is highly likely to be the very next
				// EntryId the Reaction processes. On hit we reuse the parsed
				// + reference-solved Program from the writer and skip both
				// the parse and the SolveReferences walk inside UpdateSymbolTable.
				cachedProgram = actorHandler.TryGetLastExecutedScript(eventData.EntryId);

				if (cachedProgram != null)
				{
					UpdateSymbolTableFromProgram(cachedProgram);
				}
				else
				{
					UpdateSymbolTable(script);
				}
				// cachedProgramIsCanonical stays false: the Program is per-EntryId
				// (single-use); engaging the per-Pattern MatchCache on it would
				// add entries that can never re-hit (each EntryId is unique) and
				// would retain the Program in memory unnecessarily. Pattern.Match
				// treats this as a parse-skip-only optimization.
			}

			return true;
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
				var parser = ParsersPool.Rent();
				Program program;
				try
				{
					parser.SetSource(entry.Script);
					program = parser.Parse(isQuery: false, isCheck: false);
				}
				finally
				{
					ParsersPool.Return(parser);
				}

				// Validate that the cache's parameter structure matches the event's,
				// and build real Parameters for IN/INOUT/EVAL from actionData.Arguments.
				var cacheParameters = entry.Program.Parameters;

				var eventParameters = new Parameters(cacheParameters.ParametersAsString());
				eventParameters.LoadArguments(actionData.Arguments);

				if (!cacheParameters.IsStructuralEquivalentTo(eventParameters)) throw new LanguageException("Parameter structure mismatch between cache and event");

				// Logging: show registered parameters.
				// Playbill final refactor: IsNow is no longer filtered (there is no SystemParameter — everything is a user param).
				System.Diagnostics.Debug.WriteLine($"[SolveActionReferences] Parameters registered for ActionId={actionData.ActionId}:");
				foreach (var param in eventParameters)
				{
					string modifierStr = param.ParameterModifier == Parameter.In ? "IN" :
										 param.ParameterModifier == Parameter.Out ? "OUT" :
										 param.ParameterModifier == Parameter.InOut ? "INOUT" :
										 param.ParameterModifier == Parameter.Eval ? "EVAL" : "UNKNOWN";

					string valueStr = param.ParameterModifier == Parameter.Out ? "(no value - OUT)" :
									  (param.IsEmpty ? "(empty)" : param.GetValue()?.ToString() ?? "(null)");

					System.Diagnostics.Debug.WriteLine($"  - {param.Name} : {param.ParameterType.Name} [{modifierStr}] = {valueStr}");
				}

				// Load parameters into the program and resolve references.
				program.LoadArguments(eventParameters);
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
				// Playbill final refactor: IsNow is no longer filtered — every parameter is a user parameter.
				int userParamCount = 0;
				foreach (var param in eventParameters)
				{
					userParamCount++;
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

				// Playbill final refactor: IsNow is no longer filtered.
				System.Diagnostics.Debug.WriteLine($"[SolveActionParameters] Reloaded parameter values for ActionId={actionData.ActionId}:");
				foreach (var param in cacheEntry.program.Parameters)
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
			catch (Exception ex)
			{
				// Classify the error type (Commit G).
				argumentsDeserializationErrors++;
				System.Diagnostics.Debug.WriteLine($"[SolveActionParameters] ERROR (Commit G): LoadArguments failed for ActionId={actionData.ActionId}: {ex.Message} (total errors: {argumentsDeserializationErrors})");
			}
		}

		// PHASES 3+2: statically validate Where expressions at startup and pre-process
		// SeekName.@Symbol references into placeholders that are resolved at runtime.
		// Where compilation: the parsed Program is cached on the engine
		// (CachedWhereProgram) so MatchTree.EvaluateWhere can compile-once /
		// execute-many via Program.ExecuteExpression instead of re-parsing per event.
		private static readonly System.Text.RegularExpressions.Regex SeekScopedRefPattern =
			new System.Text.RegularExpressions.Regex(@"(\w+)\.@(Now|User|Ip|EntryId)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

		// Test-only switch that forces EvaluateWhere down the re-parse fallback path
		// by suppressing population of engine.CachedWhereProgram. Used by parity and
		// micro-benchmark tests to A/B the cached vs uncached implementations on the
		// same journal. Set to true under [TestInitialize] and reset under
		// [TestCleanup] in tests that need it; left false in production paths.
		internal static bool BypassWhereCompilationCacheForTests = false;

		private void CompileWhereExpressions()
		{
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
					var parser = ParsersPool.Rent();
					Program program;
					try
					{
						parser.SetSource(wrappedScript);
						program = parser.Parse(isQuery: true, isCheck: false);
					}
					finally
					{
						ParsersPool.Return(parser);
					}
					// Where compilation: cache the parsed Program. SolveReferences + Compile
					// are deferred to the first per-event invocation (lazy JIT) because the
					// concrete parameter types are not known until a Pattern produces a
					// match. Force compiled mode regardless of the actor's policy: a Where
					// is evaluated N times per Reaction.Execute (one per candidate event),
					// so amortizing a single Compile is always net-positive vs the previous
					// per-event re-parse path.
					program.AdjustCompilationMode(useInterpretedMode: false, CompilationModePolicy.AlwaysCompiled);
					if (!BypassWhereCompilationCacheForTests)
					{
						engine.CachedWhereProgram = program;
					}
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
				var parser = ParsersPool.Rent();
				Program program;
				try
				{
					parser.SetSource(script);
					program = parser.Parse(isQuery: false, isCheck: false);
				}
				finally
				{
					ParsersPool.Return(parser);
				}

				// We need a temporary Parameters for SolveReferences.
				// The parser adds variables during parsing, but SolveReferences
				// validates and completes type information.
				var tempParams = actorHandler.ParametersPool.Rent();
				try
				{
					program.SolveReferences(tempParams, withStaticValidation: true);

					UpdateSymbolTableFromProgram(program);
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

		// B.2 ext: extract global variable declarations from an already-parsed
		// and reference-solved Program. Used by the last-executed-script fast
		// path so the Reaction's symbolTable receives the same globals as
		// UpdateSymbolTable(script) without re-parsing.
		private void UpdateSymbolTableFromProgram(Program program)
		{
			ArgumentNullException.ThrowIfNull(program);

			var declaraciones = program.Declaraciones;
			if (declaraciones == null) return;
			foreach (var id in declaraciones)
			{
				if (id.IsGlobalVariable && id.ForcedType != null)
				{
					symbolTable.SetVariable(id.Name, null, id.ForcedType);
				}
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

		IPuppeteerLogger DB.IActorEventJournalClient.Logger => actorHandler.Logger;

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

			// PERF (Tier 1): resolve the per-event work ONCE before the level loop.
			// Script extraction, cached-Program resolution and parameter (re)load do
			// not depend on `level`; previously they ran inside ConsumeEvent on every
			// iteration, repeating LoadArguments + dictionary lookups MaxDepth times
			// for the same event. The only level-dependent work is the per-level
			// match attempt (matchTree.TryMatchAtLevel), which stays inside the loop.
			if (ResolveEventForMatching(eventData, out string script, out Program cachedProgram, out bool cachedProgramIsCanonical))
			{
				for (int level = 0; level < MaxDepth; level++)
				{
					matchTree.TryMatchAtLevel(level, eventData, reactionEngines, script, symbolTable, cachedProgram, cachedProgramIsCanonical);
				}
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
