using Puppeteer.EventSourcing.DB;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Puppeteer.EventSourcing.Follower
{
	public enum ReactionExecutionMode
	{
		Batch,
		Continuous
	}

	public class Reactions : IEnumerable<Reaction>
	{
		private readonly ActorHandler actorHandler;

		private readonly List<Reaction> reactions = new List<Reaction>();

		// The canonical checkpoint store — derived from the actor's own storage by the
		// ConfigureStorage auto-wire (SetDairyStorage). It is NEVER reassigned by a
		// relocated run: whatever ConfigureStorage set at init stays put.
		private DiaryStorage diaryStorage;

		// Set only for the duration of a relocated run (RelocatedReactions.Execute) and
		// cleared in a finally. When non-null it is the store the reactions read/write
		// FOR THAT RUN. The canonical `diaryStorage` above is untouched. Because the
		// single reaction set is reused (never cloned), redirecting the store here is
		// all a relocated run costs — no per-run allocation of Reaction objects.
		private DiaryStorage executionStoreOverride;

		internal Reactions(ActorHandler actorHandler)
		{
			ArgumentNullException.ThrowIfNull(actorHandler);

			this.actorHandler = actorHandler;
		}

		internal ActorHandler ActorHandler => actorHandler;

		internal PatternsGroup Patterns => new PatternsGroup(this);

		// The store in effect for the current execution: the relocated override while a
		// RelocatedReactions.Execute is running, otherwise the canonical actor-derived
		// store. Every Reaction resolves its storage through here, so a relocated run
		// transparently redirects the same reaction objects without cloning them.
		internal DiaryStorage DiaryStorage
		{
			get
			{
				DiaryStorage store = executionStoreOverride ?? diaryStorage;
				if (store == null) throw new LanguageException("DiaryStorage is not set. Please set it before using Reactions.");

				return store;
			}
		}

		// Internal test seam: point the reactions at a caller-built DiaryStorage.
		// The framework's checkpoint-internal tests use this together with
		// DiaryStorageInMemory to drive reactions without a full ConfigureStorage.
		// NOT the public/taught path — authors use ConfigureStorage (auto-wire) or
		// the public UseCheckpointStore / UseInMemoryCheckpoints overrides below.
		internal void SetDairyStorage(DiaryStorage storage)
		{
			ArgumentNullException.ThrowIfNull(storage);

			this.diaryStorage = storage;
		}

		// Runs `body` with the reactions' checkpoint store temporarily redirected to
		// `store`. Drives RelocatedReactions.Execute: the canonical `diaryStorage` is
		// never reassigned — the override is set only for the call and cleared in a
		// finally, so `actor.Reactions` keeps its actor-derived store at rest. Reuses
		// the one reaction set (no cloning). Not re-entrant / single-threaded per
		// reaction, consistent with the reaction engine's existing threading model.
		internal void RunWithCheckpointStore(DiaryStorage store, Action body)
		{
			ArgumentNullException.ThrowIfNull(store);
			ArgumentNullException.ThrowIfNull(body);

			DiaryStorage previous = executionStoreOverride;
			executionStoreOverride = store;
			try
			{
				body();
			}
			finally
			{
				executionStoreOverride = previous;
			}
		}

		// Relocate reaction checkpoints to an EPHEMERAL in-memory store — a throwaway
		// lab over the SAME reaction definitions. Returns an O(1) handle you call
		// Execute on; the N reactions are never cloned and `actor.Reactions` keeps its
		// canonical (actor-derived) store untouched.
		//
		// A relocated store is a whole journal view (the events the reactions replay
		// plus their checkpoints), so an in-memory lab starts empty — seed the events
		// you want it to see. For the common "just run my reactions durably" case you
		// do NOT relocate at all.
		public RelocatedReactions RelocatedInMemory()
		{
			var store = new DiaryStorageInMemory(actorHandler);
			RejectIfSameAsActorStore(store);
			return new RelocatedReactions(this, store, ownedDiary: null);
		}

		// Relocate reaction checkpoints to a distinct DURABLE backend (a different DB
		// or path). Returns an O(1) handle you call Execute on; the reactions are never
		// cloned and `actor.Reactions` keeps its canonical store untouched. Rejects a
		// target that resolves to the actor's own store (that is not a relocation).
		public RelocatedReactions RelocatedTo(DatabaseType databaseType, string connectionString)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(connectionString);

			var diary = new Diary(databaseType, connectionString, eventJournalClient: actorHandler);
			try
			{
				RejectIfSameAsActorStore(diary.Storage);
			}
			catch
			{
				diary.Dispose();
				throw;
			}
			return new RelocatedReactions(this, diary.Storage, ownedDiary: diary);
		}

		// Guards the validation the author asked for: a relocation target must differ
		// from the actor's own store — otherwise the "relocated" checkpoints would
		// collide with the canonical ones, which is a mistake, not a relocation.
		private void RejectIfSameAsActorStore(DiaryStorage candidate)
		{
			DiaryStorage actorStore = actorHandler.TryGetDiaryStorage();
			if (actorStore != null && candidate.IsSameStoreAs(actorStore))
				throw new LanguageException(
					"Relocation target is the actor's own store — that is not a relocation. " +
					"Point RelocatedTo at a different database/path, or use RelocatedInMemory " +
					"for a throwaway lab.");
		}

		public void Execute(params string[] reactionNames)
		{
			Execute(reactionNames, ReactionExecutionMode.Batch, default);
		}

		internal void Execute(string[] reactionNames, ReactionExecutionMode executionMode, CancellationToken cancellationToken)
		{
			if (reactionNames == null) throw new ArgumentNullException(nameof(reactionNames));
			foreach (string name in reactionNames) ArgumentNullException.ThrowIfNullOrWhiteSpace(name);

			foreach (var reaction in reactions)
			{
				if (reactionNames.Length == 0 || Array.Exists(reactionNames, name => string.Equals(name, reaction.Name, StringComparison.OrdinalIgnoreCase)))
				{
					reaction.Execute(executionMode, cancellationToken);
				}
			}
		}

		public void ExecuteReactions(
			string[] reactionNames,
			ReactionExecutionMode mode,
			CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(reactionNames);

			if (reactionNames.Length == 0)
			{
				throw new ArgumentException("At least one reaction must be specified. Empty arrays are not allowed.", nameof(reactionNames));
			}

			foreach (string name in reactionNames)
			{
				ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			}

			List<Reaction> reactionsToExecute = new List<Reaction>();

			foreach (string name in reactionNames)
			{
				Reaction reaction = null;
				foreach (var r in reactions)
				{
					if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
					{
						reaction = r;
						break;
					}
				}

				if (reaction == null)
				{
					throw new LanguageException($"Reaction '{name}' is not defined. All reactions must exist before execution.");
				}

				if (!reaction.IsActive)
				{
					System.Diagnostics.Debug.WriteLine($"[Reactions] Reaction '{name}' is inactive (IsActive=false). Skipping.");
					continue;
				}

				if (reaction.IsExpired)
				{
					System.Diagnostics.Debug.WriteLine($"[Reactions] Reaction '{name}' ha expirado (ExpirationDate < UtcNow). Se omite.");
					continue;
				}

				reactionsToExecute.Add(reaction);
			}

			if (reactionsToExecute.Count == 0)
			{
				System.Diagnostics.Debug.WriteLine($"[Reactions] No reaction is active or in effect. Nothing is executed.");
				return;
			}

			// Collect reaction names to pass to Execute().
			string[] names = reactionsToExecute.Select(r => r.Name).ToArray();

			// Call the internal Execute() that passes executionMode and cancellationToken.
			// Each Reaction.Execute() will configure its own ActorReactions with the right mode.
			Execute(names, mode, cancellationToken);
		}

		public Reaction this[string name]
		{
			get
			{
				ArgumentNullException.ThrowIfNullOrWhiteSpace(name);

				foreach (var reaction in reactions)
				{
					if (string.Equals(reaction.Name, name, StringComparison.OrdinalIgnoreCase))
						return reaction;
				}

				throw new LanguageException($"Reaction with name '{name}' is not defined.");
			}
		}

		public ReactionModeBuilder DefineReaction(string name)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);

			foreach (var reaction in reactions)
			{
				if (string.Equals(reaction.Name, name, StringComparison.OrdinalIgnoreCase))
					throw new LanguageException($"Reaction with name '{name}' is already defined.");
			}

			return new ReactionModeBuilder(this, name);
		}

		internal Reaction CreateReaction(string name, ReactionMode mode, ReactionActivation activation)
		{
			// Reject .CastOnly() on a director-role host (a Theater PerformanceV2)
			// LOUDLY at declaration time. Such a reaction would never fire — CastOnly
			// only ever activates on a Cast/follower node — so leaving it silent
			// (ActivationAllowsRole returning false) hides an anti-pattern. The host
			// raises ForbidsCastOnlyActivation; a genuine Cast/follower node in a
			// replication topology (a P2P Stage) does not, so it can still use CastOnly.
			if (activation == ReactionActivation.CastOnly && actorHandler.ForbidsCastOnlyActivation)
			{
				throw new LanguageException(
					$"Reaction '{name}': .CastOnly() is not valid on a director-role host (a Theater PerformanceV2). " +
					"A CastOnly reaction only ever fires on a Cast/follower node, so on this host it would never run " +
					"(a silent no-op). Use .Company() (runs on both director and Cast) or .DirectorOnly() instead. " +
					".CastOnly() is reserved for a genuine Cast/follower node in a replication topology (a P2P Stage).");
			}

			var newReaction = new Reaction(this, name, mode, activation);
			reactions.Add(newReaction);
			return newReaction;
		}

		public IEnumerable<Reaction> CuedReactions =>
			reactions.Where(r => r.IsCued && r.IsActive && !r.IsExpired);

		internal void GracefulShutdown()
		{
			foreach (var reaction in reactions)
			{
				if (reaction.IsCued)
				{
					reaction.RequestShutdown();
				}
			}
		}

		public IEnumerator<Reaction> GetEnumerator() => reactions.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => reactions.GetEnumerator();
	}

	// A relocated view over an actor's reactions: the SAME reaction definitions run
	// with their checkpoints redirected to a different store. Obtained from
	// Reactions.RelocatedInMemory() / RelocatedTo(...). It is an O(1) handle — it
	// holds a reference to the actor's one Reactions plus the relocated store; it does
	// NOT clone the reaction set. Reusing one handle across many Execute calls costs
	// nothing beyond the handle itself, and `actor.Reactions` keeps its canonical
	// (actor-derived) store untouched throughout.
	//
	// Dispose to release a durable relocated backend (RelocatedTo). RelocatedInMemory
	// owns nothing disposable.
	public sealed class RelocatedReactions : IDisposable
	{
		private readonly Reactions reactions;
		private readonly DiaryStorage checkpointStore;
		private readonly Diary ownedDiary; // non-null for a durable RelocatedTo; null for in-memory.
		private bool disposed;

		internal RelocatedReactions(Reactions reactions, DiaryStorage checkpointStore, Diary ownedDiary)
		{
			this.reactions = reactions ?? throw new ArgumentNullException(nameof(reactions));
			this.checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
			this.ownedDiary = ownedDiary;
		}

		// Test/introspection handle to the store this relocation writes to. Not part
		// of the taught surface — authors call Execute, not this.
		internal DiaryStorage CheckpointStore => checkpointStore;

		// Run the actor's reactions with checkpoints going to the relocated store.
		public void Execute(params string[] reactionNames)
		{
			ArgumentNullException.ThrowIfNull(reactionNames);
			reactions.RunWithCheckpointStore(checkpointStore, () => reactions.Execute(reactionNames));
		}

		// Mode-aware counterpart of Reactions.ExecuteReactions, relocated.
		public void ExecuteReactions(string[] reactionNames, ReactionExecutionMode mode, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(reactionNames);
			reactions.RunWithCheckpointStore(checkpointStore, () => reactions.ExecuteReactions(reactionNames, mode, cancellationToken));
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			ownedDiary?.Dispose();
		}
	}
}
