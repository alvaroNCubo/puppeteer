using Puppeteer.EventSourcing.Interpreter.Libraries;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Puppeteer.EventSourcing.Follower
{
	// B.2: per-Reaction match cache keyed by (Pattern, parsed Program AST,
	// initial captured variables signature, expose data JSON). On cache hit
	// the hot path is O(hash + dict lookup): the matcher is not invoked and
	// the cached captures are written back into the caller's Parameters.
	// Negative caching is mandatory — a stored entry with Matched=false
	// signals "0 matches confirmed for this combination", letting subsequent
	// events skip immediately. Distinct Patterns within the same Reaction
	// produce different keys because the Pattern reference participates in
	// hashing; deduplication across literally-identical patterns is deferred.
	internal sealed class ReactionMatchCache
	{
		private readonly Dictionary<MatchCacheKey, MatchCacheEntry> cache = new Dictionary<MatchCacheKey, MatchCacheEntry>();
		private long hits;
		private long misses;

		internal long Hits => hits;
		internal long Misses => misses;
		internal int Count => cache.Count;

		internal bool TryGet(MatchCacheKey key, out MatchCacheEntry entry)
		{
			ArgumentNullException.ThrowIfNull(key);

			if (cache.TryGetValue(key, out entry))
			{
				hits++;
				return true;
			}
			misses++;
			entry = null;
			return false;
		}

		internal void Store(MatchCacheKey key, MatchCacheEntry entry)
		{
			ArgumentNullException.ThrowIfNull(key);
			ArgumentNullException.ThrowIfNull(entry);

			cache[key] = entry;
		}

		internal void Clear()
		{
			cache.Clear();
			hits = 0;
			misses = 0;
		}
	}

	internal readonly struct CapturedValue
	{
		internal readonly string Name;
		internal readonly Type Type;
		internal readonly object Value;
		internal CapturedValue(string name, Type type, object value)
		{
			ArgumentNullException.ThrowIfNull(name);
			ArgumentNullException.ThrowIfNull(type);
			Name = name;
			Type = type;
			Value = value;
		}
	}

	internal sealed class MatchCacheEntry
	{
		internal readonly bool Matched;
		internal readonly IReadOnlyList<CapturedValue> Captures;

		internal MatchCacheEntry(bool matched, IReadOnlyList<CapturedValue> captures)
		{
			Matched = matched;
			Captures = captures;
		}

		// Sentinel for negative caching. A "no match confirmed" outcome carries
		// no captures, so a single shared instance is safe to reuse.
		internal static readonly MatchCacheEntry NegativeMiss = new MatchCacheEntry(matched: false, captures: null);
	}

	internal sealed class MatchCacheKey : IEquatable<MatchCacheKey>
	{
		private readonly Pattern pattern;
		private readonly Program program;
		private readonly string initialVarsSignature;
		private readonly string programParametersSignature;
		private readonly string exposeDataJson;
		private readonly int hashCode;

		internal MatchCacheKey(Pattern pattern, Program program, string initialVarsSignature, string programParametersSignature, string exposeDataJson)
		{
			ArgumentNullException.ThrowIfNull(pattern);
			ArgumentNullException.ThrowIfNull(program);

			this.pattern = pattern;
			this.program = program;
			this.initialVarsSignature = initialVarsSignature ?? string.Empty;
			this.programParametersSignature = programParametersSignature ?? string.Empty;
			this.exposeDataJson = exposeDataJson ?? string.Empty;

			HashCode hc = new HashCode();
			hc.Add(RuntimeHelpers.GetHashCode(pattern));
			// Program identity is canonical for ActionEvents: the ActorHandler
			// already stores one CommandCacheEntry per ActionId, and the
			// Reaction's local LRU (cachedProgramas[ActionId]) reuses a single
			// Program reference across every event with that ActionId. So the
			// Program reference's identity hash IS the canonical key — no
			// structural walk needed, no textual hash, no recomputed digest.
			// ScriptEvents (no ActionId) are passed through without consulting
			// the cache; see Pattern.Match.
			hc.Add(RuntimeHelpers.GetHashCode(program));
			hc.Add(this.initialVarsSignature, StringComparer.Ordinal);
			// ActionEvents share a Program reference per ActionId but rebind
			// its Parameters per event (e.g. @currency=100.50 vs 200.75).
			// Literal pattern matching depends on those values; without this
			// dimension the cache would incorrectly elide events whose
			// arguments differ from the first one observed.
			hc.Add(this.programParametersSignature, StringComparer.Ordinal);
			hc.Add(this.exposeDataJson, StringComparer.Ordinal);
			hashCode = hc.ToHashCode();
		}

		internal Pattern Pattern => pattern;
		internal Program Program => program;
		internal string InitialVarsSignature => initialVarsSignature;
		internal string ProgramParametersSignature => programParametersSignature;
		internal string ExposeDataJson => exposeDataJson;

		public bool Equals(MatchCacheKey other)
		{
			if (other is null) return false;
			if (ReferenceEquals(this, other)) return true;
			if (hashCode != other.hashCode) return false;
			if (!ReferenceEquals(pattern, other.pattern)) return false;
			if (!ReferenceEquals(program, other.program)) return false;
			if (!string.Equals(initialVarsSignature, other.initialVarsSignature, StringComparison.Ordinal)) return false;
			if (!string.Equals(programParametersSignature, other.programParametersSignature, StringComparison.Ordinal)) return false;
			return string.Equals(exposeDataJson, other.exposeDataJson, StringComparison.Ordinal);
		}

		public override bool Equals(object obj) => Equals(obj as MatchCacheKey);

		public override int GetHashCode() => hashCode;

		// Deterministic signature of the input Parameters' state. Used to
		// discriminate cache entries when the same Pattern + AST is invoked
		// with different prior-Seek captures. Order is stabilized by name
		// (ordinal) so equivalent contents produce identical signatures.
		internal static string SignatureOf(Parameters parameters)
		{
			if (parameters == null) return string.Empty;

			List<Parameter> ordered = new List<Parameter>();
			foreach (Parameter p in parameters)
			{
				ordered.Add(p);
			}
			if (ordered.Count == 0) return string.Empty;

			ordered.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
			StringBuilder sb = new StringBuilder();
			foreach (Parameter p in ordered)
			{
				if (sb.Length > 0) sb.Append(';');
				sb.Append(p.Name);
				sb.Append('|');
				sb.Append(p.ParameterType != null ? p.ParameterType.FullName : "?");
				sb.Append('=');
				if (p.IsEmpty)
				{
					sb.Append("<empty>");
				}
				else
				{
					object v = p.GetValue();
					sb.Append(v == null ? "<null>" : v.ToString());
				}
			}
			return sb.ToString();
		}
	}
}
