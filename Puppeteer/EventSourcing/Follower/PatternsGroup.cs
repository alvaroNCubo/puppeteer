using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Puppeteer.EventSourcing.Follower
{
	internal class PatternsGroup
	{
		private readonly Reactions reactions;
		private readonly Reaction reaction;

		internal PatternsGroup(Reaction reaction)
		{
			ArgumentNullException.ThrowIfNull(reaction);
			this.reaction = reaction;
			this.reactions = null;
		}

		internal PatternsGroup(Reactions reactions)
		{
			ArgumentNullException.ThrowIfNull(reactions);
			this.reactions = null;
			this.reactions = reactions;
		}

		private IEnumerable<Pattern> patternsGroup()
		{
			if (reaction != null)
			{
				return Patterns.ReactionPatters(reaction);
			}
			else if (reactions != null)
			{
				var result = new List<Pattern>();
				foreach (var r in reactions)
				{
					result.AddRange(Patterns.ReactionPatters(r));
				}
				return result;
			}
			throw new LanguageException("PatternsGroup is not properly initialized.");
		}

		private ReadOnlyCollection<ReactionEngine> Engines()
		{
			var result = new List<ReactionEngine>();
			if (reaction != null)
			{
				result.AddRange(reaction.ReactionEngines);
			}
			else if (reactions != null)
			{
				foreach (var r in reactions)
				{
					result.AddRange(r.ReactionEngines);
				}
			}
			return result.AsReadOnly();
		}


		internal int Count
		{
			get
			{
				return Engines().Count();
			}
		}

		internal Patterns this[string patternDescription]
		{
			get
			{
				ArgumentNullException.ThrowIfNullOrWhiteSpace(patternDescription);

				var engines = new List<ReactionEngine>();

				foreach (var engine in Engines())
				{
					if (string.Equals(engine.PatternDescription, patternDescription, StringComparison.OrdinalIgnoreCase))
					{
						return engine.Patterns;
					}
				}

				throw new LanguageException($"Pattern with description '{patternDescription}' not found.");
			}
		}
	}


}
