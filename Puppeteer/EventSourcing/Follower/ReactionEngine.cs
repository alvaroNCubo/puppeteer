using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Threading;

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

		// Where compilation: lazy JIT pattern. The Program in CachedWhereProgram is
		// parsed at startup (Reaction.CompileWhereExpressions). SolveReferences +
		// Compile happen on the first per-event invocation, when the matched
		// parameters provide concrete types. The compiled lambda captures the
		// Parameter.instance VariableSymbol objects as Expression.Constant at
		// compile time (see Parameter.ParameterInitializationExpression), so the
		// Program is bound to ONE specific Parameters instance for its lifetime.
		// We therefore own a dedicated Parameters per engine and reuse it across
		// events — only the Values mutate per call. The lock serializes both the
		// first-call compile and the population+invoke per event because the
		// Parameters instance is engine-shared mutable state.
		private readonly object whereCompileLock = new object();
		private Parameters cachedWhereParameters;

		// Phase A counters: how many events were evaluated against this Seek's
		// patterns (SeekEntered) and how many produced a match that advanced
		// the trajectory (SeekMatched). The delta SeekEntered - SeekMatched
		// diagnoses drop-off per stage when a multi-Seek Reaction underfires.
		// Single-writer-per-actor in the common case; Interlocked is defensive
		// for push-mode callbacks dispatched from other threads.
		private long seekEntered;
		private long seekMatched;

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

		internal object WhereCompileLock => whereCompileLock;

		internal Parameters GetOrCreateCachedWhereParameters()
		{
			if (cachedWhereParameters == null)
			{
				cachedWhereParameters = new Parameters();
			}
			return cachedWhereParameters;
		}

		// Many (K.1): modificador existencial. Colapsa multiplicidad — fire en primer
		// match, ignora subsequentes en la misma trayectoria. Valido en cualquier nivel
		// (Seek/ThenSeek/ThenFinalSeek). Refinamientos admitidos: AtLeast(n) (cuantificador
		// estructural) y Accumulate(name) (captura cruda al dominio). NO se admiten
		// operadores que transformen/agreguen datos antes de entregarlos (Distinct,
		// GroupBy, Sum, etc.) — esa frontera es del Dominio, no del DSL.
		private bool isMany;
		private string accumulateName;
		private int? atLeastCount;

		internal bool IsMany => isMany;
		internal string AccumulateName => accumulateName;
		internal int? AtLeastCount => atLeastCount;

		public ReactionEngine Many()
		{
			if (this.isMany)
				throw new LanguageException("Many() can be set only once per Seek.");
			if (this.exactCount.HasValue)
				throw new LanguageException("Many() cannot be combined with None/One/Exactly — one quantifier per Seek.");

			this.isMany = true;
			return this;
		}

		public ReactionEngine Accumulate(string name)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(name);
			if (!this.isMany)
				throw new LanguageException("Accumulate(...) requires .Many() previously on this Seek.");
			this.accumulateName = name;
			return this;
		}

		public ReactionEngine AtLeast(int count)
		{
			if (count <= 0) throw new ArgumentException("count must be greater than 0.", nameof(count));
			if (!this.isMany)
				throw new LanguageException("AtLeast(n) requires .Many() previously on this Seek.");
			this.atLeastCount = count;
			return this;
		}

		// Phase I.entry / I.time: ventanas por EntryId o TimeSpan. La ventana se
		// cierra cuando llega un evento fuera del rango — sin timer, sin eviction
		// mechanism; el journal cierra la ventana naturalmente. Anchor por defecto:
		// el ultimo evento activo del parent (creation para Seeks regulares, ultimo
		// accumulated para Many). Exclusivas entre si (un solo Within por Seek, sea
		// EntryId o TimeSpan). No validas en el primer Seek (sin parent).
		private long? withinEntries;
		private TimeSpan? withinTime;
		// F3 .Aged(span): periodo de ASENTAMIENTO. NO es filtro ni ventana entre seeks
		// — gobierna el DISPARO: un match cuyo seek de cierre tiene Aged no dispara (no
		// elide) hasta que el evento de cierre haya envejecido 'span' respecto al FRENTE
		// del journal (max OccurredAt visto, reloj logico determinista, NO wall-clock).
		// Subsume el invariante "no elidir el ultimo registro".
		private TimeSpan? agedSpan;
		internal TimeSpan? AgedSpan => agedSpan;
		internal long? WithinEntries => withinEntries;
		internal TimeSpan? WithinTime => withinTime;
		internal bool HasWithinWindow => withinEntries.HasValue || withinTime.HasValue;

		public ReactionEngine Within(long entries)
		{
			if (entries <= 0) throw new ArgumentException("entries must be greater than 0.", nameof(entries));

			if (this.reaction.ReactionEngines.Count > 0 && this.reaction.ReactionEngines[0] == this)
				throw new LanguageException("Within(...) cannot be used on the first Seek — there is no previous match to anchor the window to. Use it on ThenSeek/ThenFinalSeek.");

			if (this.HasWithinWindow)
				throw new LanguageException("Within(...) can be set only once per Seek (entries or time, not both).");

			this.withinEntries = entries;
			return this;
		}

		public ReactionEngine Within(TimeSpan span)
		{
			if (span <= TimeSpan.Zero) throw new ArgumentException("span must be positive.", nameof(span));

			if (this.reaction.ReactionEngines.Count > 0 && this.reaction.ReactionEngines[0] == this)
				throw new LanguageException("Within(...) cannot be used on the first Seek — there is no previous match to anchor the window to. Use it on ThenSeek/ThenFinalSeek.");

			if (this.HasWithinWindow)
				throw new LanguageException("Within(...) can be set only once per Seek (entries or time, not both).");

			this.withinTime = span;
			return this;
		}

		// F3: periodo de asentamiento del seek de cierre. El match no dispara/elide hasta
		// que su evento de cierre quede 'span' atras del frente del journal. Gate de
		// disparo, no filtro. Solo tiene sentido en un seek de cierre (no el primero).
		public ReactionEngine Aged(TimeSpan span)
		{
			if (span <= TimeSpan.Zero) throw new ArgumentException("span must be positive.", nameof(span));

			if (this.reaction.ReactionEngines.Count > 0 && this.reaction.ReactionEngines[0] == this)
				throw new LanguageException("Aged(...) no aplica al primer Seek — es el gate de asentamiento del seek de cierre. Usalo en ThenSeek/ThenFinalSeek.");

			if (this.agedSpan.HasValue)
				throw new LanguageException("Aged(...) solo puede declararse una vez por Seek.");

			this.agedSpan = span;
			return this;
		}

		// K.2: cuantificadores exact-family. None ~ Exactly(0), One ~ Exactly(1).
		// Disparan al cerrar la ventana con count == expected (lazy success), o
		// eager-fail si el count excede expected antes del cierre. Requieren
		// .Within(...) — sin window no hay punto de cierre en journal abierto.
		// Exclusivos con Many (un solo cuantificador por Seek).
		private int? exactCount;
		internal int? ExactCount => exactCount;
		internal bool IsExact => exactCount.HasValue;

		public ReactionEngine None()
		{
			SetExactCount(0, "None");
			return this;
		}

		public ReactionEngine One()
		{
			SetExactCount(1, "One");
			return this;
		}

		public ReactionEngine Exactly(int count)
		{
			if (count < 0) throw new ArgumentException("count must be non-negative.", nameof(count));
			SetExactCount(count, "Exactly");
			return this;
		}

		private void SetExactCount(int count, string verbName)
		{
			if (this.isMany)
				throw new LanguageException($"{verbName}() cannot be combined with Many() — one quantifier per Seek.");
			if (this.exactCount.HasValue)
				throw new LanguageException($"{verbName}() cannot be combined with another exact quantifier on the same Seek.");
			this.exactCount = count;
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

		// Cuantificador universal del Reaction, declarado tras el Seek que captura las
		// colecciones fuente: .OnMatch(...).ForEach("(a,b,c) in $x × $y × $z").ThenSeek(...).
		public ReactionEngine ForEach(string spec)
		{
			this.reaction.ForEach(spec);
			return this;
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

		public long SeekEntered => Interlocked.Read(ref seekEntered);
		public long SeekMatched => Interlocked.Read(ref seekMatched);

		internal void IncrementSeekEntered() => Interlocked.Increment(ref seekEntered);
		internal void IncrementSeekMatched() => Interlocked.Increment(ref seekMatched);

		internal void ResetSeekCounters()
		{
			Interlocked.Exchange(ref seekEntered, 0);
			Interlocked.Exchange(ref seekMatched, 0);
		}
	}
}
