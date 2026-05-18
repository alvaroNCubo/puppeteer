using System;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Follower
{

	internal class Patterns
	{
		private readonly ReactionEngine reactionEngine;
		private readonly List<Pattern> patterns = new List<Pattern>();

		internal Patterns(ReactionEngine reactionEngine)
		{
			ArgumentNullException.ThrowIfNull(reactionEngine);

			this.reactionEngine = reactionEngine;
		}

		internal Pattern this[int index]
		{
			get
			{
				if (index < 0 || index >= patterns.Count) throw new LanguageException($"Index {index} is out of range.");

				return patterns[index];
			}
		}

		internal int Count
		{
			get
			{
				return patterns.Count;
			}
		}

		internal void Add(Pattern p)
		{
			ArgumentNullException.ThrowIfNull(p);

			patterns.Add(p);
		}

		internal static List<Pattern> ReactionPatters(Reaction reaction)
		{
			ArgumentNullException.ThrowIfNull(reaction);

			var reactionEngines = reaction.ReactionEngines;
			var result = new List<Pattern>();

			foreach (var engine in reactionEngines)
			{
				for (int i = 0; i < engine.Patterns.Count; i++)
				{
					var pattern = engine.Patterns[i];
					if (pattern != null)
					{
						result.Add(pattern);
					}
				}
			}

			return result;
		}
	}
}
