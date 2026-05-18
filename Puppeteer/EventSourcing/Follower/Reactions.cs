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
		private DiaryStorage diaryStorage;

		internal Reactions(ActorHandler actorHandler)
		{
			ArgumentNullException.ThrowIfNull(actorHandler);

			this.actorHandler = actorHandler;
		}

		internal ActorHandler ActorHandler => actorHandler;

		internal PatternsGroup Patterns => new PatternsGroup(this);

		internal DiaryStorage DiaryStorage
		{
			get
			{
				if (diaryStorage == null) throw new LanguageException("DiaryStorage is not set. Please set it before using Reactions.");

				return diaryStorage;
			}
		}

		internal void SetDairyStorage(DiaryStorage storage)
		{
			ArgumentNullException.ThrowIfNull(storage);

			this.diaryStorage = storage;
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
				if (reaction.Direction == RehydrateDirection.Forward &&
					(reactionNames.Length == 0 || Array.Exists(reactionNames, name => string.Equals(name, reaction.Name, StringComparison.OrdinalIgnoreCase))))
				{
					reaction.Execute(executionMode, cancellationToken);
				}
			}

			foreach (var reaction in reactions)
			{
				if (reaction.Direction == RehydrateDirection.Backward &&
					(reactionNames.Length == 0 || Array.Exists(reactionNames, name => string.Equals(name, reaction.Name, StringComparison.OrdinalIgnoreCase))))
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
				System.Diagnostics.Debug.WriteLine($"[Reactions] Ninguna reaction esta activa o vigente. No se ejecuta nada.");
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
}
