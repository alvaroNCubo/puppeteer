using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Follower
{
	public class ReactionEngine
	{
		private readonly string patternDescription;
		private readonly Reaction reaction;
		private readonly Patterns patterns;
		private readonly bool isFinalSeek;

		private string whereExpression;
		private Program cachedWhereProgram;

		// FASE 2: mapping de placeholders generados por pre-procesamiento de SeekName.@Simbolo.
		// Clave: placeholder ("_seek_Compra_Now"). ValueGetter: (seekName, symbolName) para resolver en runtime.
		private Dictionary<string, (string seekName, string symbolName)> seekScopedRefs;
		private string normalizedWhereExpression;

		internal ReactionEngine(Reaction reaction, string patternDescription, bool isFinalSeek = false)
		{
			ArgumentNullException.ThrowIfNull(reaction);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(patternDescription);

			this.patternDescription = patternDescription;
			this.reaction = reaction;
			this.patterns = new Patterns(this);
			this.isFinalSeek = isFinalSeek;
		}

		public string PatternDescription => patternDescription;

		internal Patterns Patterns => patterns;

		internal Reaction Reaction => reaction;

		internal bool IsFinalSeek => isFinalSeek;

		private static readonly HashSet<string> ReservedSystemSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"Now", "User", "Ip", "EntryId"
		};

		public ReactionEngine Where(string expression)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(expression);

			if (!string.IsNullOrWhiteSpace(whereExpression))
				throw new LanguageException("Where no se puede encadenar mas de una vez en el mismo Seek. Combine condiciones con && dentro de la misma expression.");

			// FASE 7: validacion build-time de comparaciones siempre-falsas.
			// @Now y @EntryId son tipos no-nullable; compararlos contra null nunca es true.
			ValidateNonNullableNotComparedAgainstNull(expression);

			this.whereExpression = expression;
			return this;
		}

		private static void ValidateNonNullableNotComparedAgainstNull(string expression)
		{
			string[] nonNullableSymbols = { "@Now", "@EntryId" };
			foreach (var symbol in nonNullableSymbols)
			{
				if (System.Text.RegularExpressions.Regex.IsMatch(expression, $@"{System.Text.RegularExpressions.Regex.Escape(symbol)}\s*(==|!=)\s*null", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
				    || System.Text.RegularExpressions.Regex.IsMatch(expression, $@"null\s*(==|!=)\s*{System.Text.RegularExpressions.Regex.Escape(symbol)}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
				{
					throw new LanguageException($"Comparar {symbol} contra null es siempre falso (su type no tiene sentinela); use otra condicion.");
				}
			}
		}

		internal bool HasWhere => !string.IsNullOrWhiteSpace(whereExpression);

		internal string WhereExpression => whereExpression;

		internal string NormalizedWhereExpression
		{
			get => normalizedWhereExpression;
			set => normalizedWhereExpression = value;
		}

		internal Dictionary<string, (string seekName, string symbolName)> SeekScopedRefs
		{
			get => seekScopedRefs;
			set => seekScopedRefs = value;
		}

		internal Program CachedWhereProgram
		{
			get => cachedWhereProgram;
			set => cachedWhereProgram = value;
		}

		// RepeatSeek: acumulacion de multiples eventos en el mismo nivel
		private bool isRepeatSeek;
		private string groupByVariable;
		private string accumulateName;
		private bool useDistinct;
		private int? untilCount;
		private TimeSpan? untilTimeout;

		internal bool IsRepeatSeek => isRepeatSeek;
		internal string GroupByVariable => groupByVariable;
		internal string AccumulateName => accumulateName;
		internal bool UseDistinct => useDistinct;
		internal int? UntilCount => untilCount;
		internal TimeSpan? UntilTimeout => untilTimeout;

		internal void SetRepeatSeek()
		{
			this.isRepeatSeek = true;
		}

		public ReactionEngine GroupBy(string variableName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(variableName);
			this.groupByVariable = variableName;
			return this;
		}

		public ReactionEngine Accumulate(string name)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			this.accumulateName = name;
			return this;
		}

		public ReactionEngine Distinct(string variableName)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(variableName);
			this.useDistinct = true;
			return this;
		}

		public ReactionEngine Until(int count)
		{
			if (count <= 0) throw new ArgumentException("count must be greater than 0.", nameof(count));
			this.untilCount = count;
			return this;
		}

		public ReactionEngine Until(TimeSpan timeout)
		{
			this.untilTimeout = timeout;
			return this;
		}

		public ReactionEngine OnMatch(string pattern)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(pattern);

			Pattern p = new Pattern(this, pattern);
			patterns.Add(p);

			return this;
		}

		public ReactionEngine ThenSeek(string name)
		{
			return this.reaction.ThenSeek(name);
		}

		public ReactionEngine ThenFinalSeek(string name)
		{
			return this.reaction.ThenFinalSeek(name);
		}
		// Plane passthroughs so the fluent chain finishes naturally after
		// `.OnMatch(...)` without an explicit hop back through Reaction.
		// Each plane delegates to its owner; verbs on the plane configure
		// the Reaction's Action.
		public ProgramPlane Program => this.reaction.Program;
		public CausationPlane Causation => this.reaction.Causation;
		public MetadataPlane Metadata => this.reaction.Metadata;

		internal void ExecuteAction(Parameters matchedParameters, long triggeringEntryId)
		{
			this.reaction.ExecuteAction(matchedParameters, triggeringEntryId);
		}

	}
}
