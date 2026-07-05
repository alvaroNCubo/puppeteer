using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Choreography.Ensemble
{
	// The FORMATION facet of the route: the SHAPE of the address space, i.e. how
	// legible a performer's key is. It is a third axis, orthogonal to the other two
	// the ensemble already carries — hosting (NodePlacement, which node hosts an
	// actor) and input routing (ActorSelector, which actor a signal animates).
	// Formation touches neither: it does not move a journal and it does not pick a
	// performer from a signal. It only decides how a structured address collapses
	// to the ONE canonical key that IS the actor's identity. Geometry of addressing
	// ⊥ geometry of hosting; the notation an Index handle offers is the CONSEQUENCE
	// of that separation, not its purpose.
	//
	// Formation is a STRATEGY carried by constructor, not an entity in the model —
	// the ensemble stays a dictionary of performers; the formation only shapes its
	// keys. Flat (rank-1) is the ROOT: the current dictionary, verbatim. A single
	// segment IS the key, so the default changes nothing and GetOrCreate(string) /
	// ConsumeFrom keep working unchanged. Every richer formation extends Flat by
	// giving the address more structure while still resolving to a single key:
	// NCube adds a fixed number of TYPED, named dimensions; later, Tree adds a
	// navigable hierarchy and Graph adds anchored traversal.
	//
	// Dimensions are TYPED, declared as a list of Dimension["name", typeof(T)]
	// entries — echoing the framework's own two-argument parameter idiom, the
	// Parameters this[name, type] indexer. The type is not decoration: addressing
	// an axis with the wrong type throws, exactly as a command rejects a value
	// that is not the declared parameter type. The type vocabulary is the
	// IDENTITY-SAFE subset of the framework's serializable parameter types:
	// string, int, bool, DateTime, and domain enums. A coordinate IS a key, and a
	// key IS identity, so the vocabulary is not "everything the framework
	// serializes" but only the types with a canonical, injective, deterministic
	// string form. The reals are excluded on purpose: double carries treacherous
	// float equality (0.1 + 0.2 != 0.3, so logically-equal values differ in bits
	// and would key distinct actors), and decimal has a non-canonical
	// representation (1.0m and 1.00m are equal in value but round-trip to "1.0"
	// vs "1.00", so equal values would key distinct actors). Anyone who genuinely
	// needs a real-valued coordinate must quantise/format it to a string
	// themselves — making the discretisation decision visible — rather than have
	// the ensemble guess it.
	//
	// The canonical encoding MUST be injective and deterministic: the key is the
	// actor's identity and the name of its journal, so two distinct addresses that
	// collided — or one address that encoded differently across runs — would fuse
	// or lose actors on rehydration. NCube therefore uses a length-prefix encoding
	// (e.g. "4:juan7:octubre3:usd"), NOT a naive separator join: with a separator,
	// ["a|b"]["c"] and ["a"]["b|c"] would both become "a|b|c"; length-prefix keeps
	// them distinct and decodable. Segment text forms follow the framework's
	// journal serialization (InvariantCulture; enums by member name).
	//
	// A DateTime coordinate is a calendar BUCKET, not an instant. The business intent
	// of addressing by time is a bucket — "the April 18 performance", "the 3pm
	// payments" — so a DateTime NCube dimension declares its Granularity (Day, Hour,
	// ...), once, in the formation. The developer keeps passing the native DateTime
	// the domain already produced (cube[april18]); the framework does NOT truncate or
	// guess. Instead it VALIDATES that the value matches the declared granularity:
	// every component finer than the grain must be zero. new DateTime(2026, 4, 18) IS
	// a Day; new DateTime(2026, 4, 18, 10, 11, 12) is NOT — it carries a time of day,
	// so it is rejected. Whoever wants the day bucket forms it explicitly (pass
	// 2026-04-18 00:00:00), and whoever wants finer addressing declares a finer
	// Granularity. This keeps the identity decision the developer's, and visible —
	// the framework never silently discards precision. Granularity is MANDATORY: a
	// DateTime dimension without one fails at declaration. Within a grain the bucket
	// label is canonical, injective and deterministic — which is what makes DateTime
	// identity-safe. Granularity is the first case of a per-dimension declared
	// CANONICALISER; the design leaves room for other dimensions to declare their own
	// later. (This does NOT reopen double/decimal: their quantisation is arbitrary,
	// whereas the calendar has canonical, business-meaningful buckets.)
	//
	// NOTE (out of scope, deeper increment): today key == identity — a structured
	// address still resolves to ONE key, so one actor has one address. Separating
	// the journal identity from an address index (several geometric VIEWS over the
	// SAME actor, e.g. Flat by id and NCube by attributes simultaneously) is a
	// later, deeper increment; it is deliberately NOT built here.
	public sealed class Formation
	{
		// Inverse of the encoder: recover the segments of a stored key so a slice
		// (a free axis, .Any) can match it position-by-position. Returns false for
		// a key not minted by this formation — foreign keys are skipped, never
		// matched, so mixing a raw GetOrCreate(id) into a structured ensemble stays
		// honest rather than silently aliasing.
		internal delegate bool KeyDecoder(string key, out string[] segments);

		// The escape character for the navigable Tree path encoding. A segment that
		// contains the separator (or the escape char itself) is escaped, so the path
		// stays both readable and injective.
		private const char EscapeChar = '\\';

		private readonly Func<IReadOnlyList<string>, string> encoder;
		private readonly KeyDecoder decoder;
		private readonly string[] dimensionNames;
		private readonly Type[] dimensionTypes;
		// Per-axis declared canonicaliser. Today only a DateTime dimension carries
		// one (its Granularity); every other axis is null and uses the default
		// Segment(...) form. This is the seam for future per-dimension canonicalisers.
		private readonly Granularity?[] dimensionGranularities;
		private readonly char separator;

		// Human-readable formation name for diagnostics ("Flat", "NCube", "Tree", "Graph").
		public string Name { get; }

		// How many segments a complete address carries. Flat is rank-1; an NCube's
		// arity is the number of dimensions it was declared with. Tree and Graph have
		// no fixed arity (arity 0).
		public int Arity => dimensionNames.Length;

		// Which handle the ensemble vends for this formation, and rejects otherwise:
		// FixedArity (Flat, NCube) -> Index; Hierarchical (Tree) -> Node; Graph -> Vertex.
		internal AddressingShape Shape { get; }

		// The path separator, meaningful only for a Tree (used by the Node's path
		// convenience). '\0' otherwise.
		internal char Separator => separator;

		// The domain adjacency for a Graph — an INJECTED strategy (the ensemble does
		// not decide where edges come from). Null unless Shape == Graph.
		internal GraphAdjacency Adjacency { get; }

		private Formation(string name, string[] dimensionNames, Type[] dimensionTypes,
			Granularity?[] dimensionGranularities,
			Func<IReadOnlyList<string>, string> encoder, KeyDecoder decoder,
			AddressingShape shape = AddressingShape.FixedArity, char separator = '\0',
			GraphAdjacency adjacency = null)
		{
			this.Name = name;
			this.dimensionNames = dimensionNames;
			this.dimensionTypes = dimensionTypes;
			this.dimensionGranularities = dimensionGranularities;
			this.encoder = encoder;
			this.decoder = decoder;
			this.Shape = shape;
			this.separator = separator;
			this.Adjacency = adjacency;
		}

		// Flat (rank-1): the current dictionary. The single segment IS the key, and
		// its declared type is string — a Flat id is an opaque string.
		public static readonly Formation Flat = new Formation(
			"Flat", new[] { "id" }, new[] { typeof(string) }, new Granularity?[] { null },
			encoder: segments => segments[0],
			decoder: (string key, out string[] segments) => { segments = new[] { key }; return true; });

		// The entry point for a dimension literal: Dimension["citizen", typeof(string)].
		// It is a static factory whose two-argument indexer mirrors the framework's
		// Parameters this[name, type] idiom, so a dimension reads like a parameter
		// slot. Each literal is validated at the point it is written (fail-fast on a
		// blank name or an unsupported type).
		public static readonly DimensionFactory Dimension = new DimensionFactory();

		// NCube: Flat extended with a fixed number of TYPED, named dimensions,
		// declared as a list of Dimension[name, typeof(T)] literals. The address is
		// complete only when every dimension has a segment; its canonical key is the
		// injective length-prefix encoding of the segments in dimension order.
		public static Formation NCube(params DimensionSpec[] dimensions)
		{
			ArgumentNullException.ThrowIfNull(dimensions);
			if (dimensions.Length == 0)
				throw new ArgumentException("An NCube needs at least one dimension.", nameof(dimensions));

			var names = new string[dimensions.Length];
			var types = new Type[dimensions.Length];
			var granularities = new Granularity?[dimensions.Length];
			for (int i = 0; i < dimensions.Length; i++)
			{
				DimensionSpec dimension = dimensions[i];
				// Guard against a default(DimensionSpec) that never passed the factory.
				if (dimension.Type == null || string.IsNullOrWhiteSpace(dimension.Name))
					throw new ArgumentException("A dimension must be built with Dimension[name, typeof(T)].", nameof(dimensions));
				for (int j = 0; j < i; j++)
				{
					if (string.Equals(names[j], dimension.Name, StringComparison.OrdinalIgnoreCase))
						throw new ArgumentException($"Duplicate dimension name '{dimension.Name}'.", nameof(dimensions));
				}
				names[i] = dimension.Name;
				types[i] = dimension.Type;
				granularities[i] = dimension.Granularity;   // non-null only for DateTime dimensions
			}
			return new Formation(
				"NCube", names, types, granularities,
				encoder: EncodeLengthPrefixed,
				decoder: (string key, out string[] segments) => TryDecodeLengthPrefixed(key, out segments));
		}

		// Tree: the hierarchy. Flat extended along depth instead of width — every
		// node is addressable at any level, and there is no declared schema (the tree
		// is sparse and lazy: only the nodes that actually became actors exist). It
		// is addressed with the Node handle, whose set-valued faces (.Children,
		// .Subtree) resolve by a PREFIX-SCAN over the live keys. That is why the
		// encoding is a navigable ESCAPED PATH (segments joined by `separator`, with
		// the separator and the escape char escaped within a segment) rather than
		// NCube's opaque length-prefix: the scan must read the path to compare
		// prefixes, yet the encoding must stay injective so distinct paths never
		// alias.
		public static Formation Tree(char separator)
		{
			if (separator == EscapeChar)
				throw new ArgumentException($"The Tree separator cannot be the escape character '{EscapeChar}'.", nameof(separator));
			if (separator == '\0')
				throw new ArgumentException("The Tree separator cannot be the null character.", nameof(separator));
			return new Formation(
				"Tree", Array.Empty<string>(), Array.Empty<Type>(), Array.Empty<Granularity?>(),
				encoder: segments => EncodeTreePath(segments, separator),
				decoder: (string key, out string[] segments) => TryDecodeTreePath(key, separator, out segments),
				shape: AddressingShape.Hierarchical,
				separator: separator);
		}

		// Graph: the network. A vertex is Flat-anchored by id (the id IS the key —
		// the graph structure lives in the EDGES, not in the key), and it is
		// addressed with the Vertex handle. Edges are DOMAIN relations, supplied by
		// an INJECTED adjacency delegate — the ensemble does not decide where they
		// come from (a table, a rehydrated performer's domain state, anything). This
		// is the seam that keeps topology out of the DSL: a `tell` addresses in
		// domain terms; the adjacency resolves those terms to neighbour actors.
		//
		// Only BOUNDED traversal is offered (.Along = one hop, .Reach = a hop-limited
		// frontier). Graph algorithms — shortest path, unbounded transitive closure —
		// are NOT: those are domain-library territory, not an addressing concern.
		// Edges are labelled and directed; direction lives in the relation name
		// (a domain word) rather than in the handle.
		public static Formation Graph(GraphAdjacency adjacency)
		{
			ArgumentNullException.ThrowIfNull(adjacency);
			return new Formation(
				"Graph", Array.Empty<string>(), Array.Empty<Type>(), Array.Empty<Granularity?>(),
				encoder: segments => segments[0],
				decoder: (string key, out string[] segments) => { segments = new[] { key }; return true; },
				shape: AddressingShape.Graph,
				adjacency: adjacency);
		}

		// The canonical text form of a typed segment, shared by the Index and Node
		// handles so every formation coerces a value identically. The forms follow
		// the journal's argument serialization (InvariantCulture; enums by member
		// name). DateTime is NOT here — it has no default form: it must be bucketed
		// by a declared Granularity (see CanonicalizeDate), because a full-precision
		// instant is not a business-meaningful identity.
		internal static string Segment(int value) => value.ToString(CultureInfo.InvariantCulture);
		internal static string Segment(bool value) => value ? "True" : "False";
		internal static string Segment(Enum value) => Enum.GetName(value.GetType(), value) ?? Enum.Format(value.GetType(), value, "D");

		// Validate a DateTime coordinate against its declared granularity and return
		// its canonical bucket label. The caller (the Index handle) has already
		// validated that `position` is a DateTime axis, so a Granularity is present.
		// The framework does NOT truncate: a value carrying any component finer than
		// the declared grain is REJECTED, so the developer's identity decision stays
		// explicit. Kind is not converted — the value's own calendar components are
		// the coordinate.
		internal string CanonicalizeDate(int position, DateTime value)
		{
			Granularity level = dimensionGranularities[position].Value;
			DateTime aligned = TruncateTo(level, value);
			if (value.Ticks != aligned.Ticks)
				throw new ArgumentException(
					$"DateTime coordinate {value:o} does not match the declared {level} granularity of dimension " +
					$"'{dimensionNames[position]}': it carries components finer than {level}. Zero them (the framework " +
					$"does not truncate — the identity decision is yours) or declare a finer Granularity.");

			var inv = CultureInfo.InvariantCulture;
			return level switch
			{
				Granularity.Year => value.ToString("yyyy", inv),
				Granularity.Month => value.ToString("yyyy-MM", inv),
				Granularity.Day => value.ToString("yyyy-MM-dd", inv),
				Granularity.Hour => value.ToString("yyyy-MM-ddTHH", inv),
				Granularity.Minute => value.ToString("yyyy-MM-ddTHH:mm", inv),
				Granularity.Second => value.ToString("yyyy-MM-ddTHH:mm:ss", inv),
				_ => throw new InvalidOperationException($"Unknown granularity {level}."),
			};
		}

		// The value truncated to the grain (Kind-agnostic — compared by Ticks). A
		// coordinate is valid iff it already equals its own truncation.
		private static DateTime TruncateTo(Granularity level, DateTime value)
			=> level switch
			{
				Granularity.Year => new DateTime(value.Year, 1, 1),
				Granularity.Month => new DateTime(value.Year, value.Month, 1),
				Granularity.Day => value.Date,
				Granularity.Hour => new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0),
				Granularity.Minute => new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0),
				Granularity.Second => new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second),
				_ => throw new InvalidOperationException($"Unknown granularity {level}."),
			};

		// The IDENTITY-SAFE addressing vocabulary — the subset of the framework's
		// serializable parameter types whose string form is canonical, injective and
		// deterministic, so it is safe as an identity token. It deliberately EXCLUDES
		// the reals: double (treacherous float equality) and decimal (non-canonical
		// representation, e.g. 1.0m vs 1.00m) both let logically-equal values key
		// distinct actors. A dimension may only be declared with one of these.
		internal static bool IsSupportedDimensionType(Type type)
			=> type == typeof(string)
			|| type == typeof(int)
			|| type == typeof(bool)
			|| type == typeof(DateTime)
			|| type.IsEnum;

		// Collapse a complete, all-concrete address to its canonical key. Callers
		// (the Index handle) validate arity and concreteness first; this is the
		// pure encoding step.
		internal string Encode(IReadOnlyList<string> segments) => encoder(segments);

		// Recover the segments of a stored key (see KeyDecoder). False = the key
		// was not minted by this formation.
		internal bool TryDecode(string key, out string[] segments) => decoder(key, out segments);

		// Arity gate shared by both terminals (.Actor and .Actors), so a malformed
		// address fails the same way whichever terminal is reached.
		internal void ValidateArity(int segmentCount)
		{
			if (segmentCount != Arity)
				throw new InvalidOperationException(
					$"{Name} formation has arity {Arity} (dimensions: {string.Join(", ", dimensionNames)}), " +
					$"but the address supplied {segmentCount} segment(s).");
		}

		// Type gate for a concrete segment: the axis must exist and its declared
		// type must match the value's type — the addressing analog of
		// UserParameter<T> rejecting a value that is not a T.
		internal void ValidateAxisType(int position, Type valueType)
		{
			ValidateAxisExists(position);
			Type declared = dimensionTypes[position];
			if (declared != valueType)
				throw new InvalidOperationException(
					$"Dimension '{dimensionNames[position]}' (position {position}) is declared {declared.Name}, " +
					$"but the address supplied {valueType.Name}.");
		}

		// A free axis (.Any) carries no value, so there is nothing to type-check —
		// only that the axis exists.
		internal void ValidateAxisExists(int position)
		{
			if (position >= Arity)
				throw new InvalidOperationException(
					$"{Name} formation has arity {Arity}; there is no dimension at position {position}.");
		}

		private static string EncodeLengthPrefixed(IReadOnlyList<string> segments)
		{
			var builder = new StringBuilder();
			foreach (var segment in segments)
			{
				builder.Append(segment.Length.ToString(CultureInfo.InvariantCulture));
				builder.Append(':');
				builder.Append(segment);
			}
			return builder.ToString();
		}

		private static bool TryDecodeLengthPrefixed(string key, out string[] segments)
		{
			var result = new List<string>();
			int i = 0;
			while (i < key.Length)
			{
				int colon = key.IndexOf(':', i);
				if (colon < 0) { segments = null; return false; }
				if (!int.TryParse(key.AsSpan(i, colon - i), NumberStyles.None, CultureInfo.InvariantCulture, out int length))
				{
					segments = null;
					return false;
				}
				int start = colon + 1;
				if (start + length > key.Length) { segments = null; return false; }
				result.Add(key.Substring(start, length));
				i = start + length;
			}
			segments = result.ToArray();
			return true;
		}

		// Navigable escaped path: segments joined by `separator`, with the separator
		// and the escape char escaped inside a segment. Injective (the escape makes
		// a literal separator distinguishable from a delimiter) and readable (a
		// prefix-scan can split it back into segments).
		private static string EncodeTreePath(IReadOnlyList<string> segments, char separator)
		{
			var builder = new StringBuilder();
			for (int i = 0; i < segments.Count; i++)
			{
				if (i > 0) builder.Append(separator);
				foreach (char c in segments[i])
				{
					if (c == EscapeChar || c == separator) builder.Append(EscapeChar);
					builder.Append(c);
				}
			}
			return builder.ToString();
		}

		private static bool TryDecodeTreePath(string key, char separator, out string[] segments)
		{
			var result = new List<string>();
			var segment = new StringBuilder();
			int i = 0;
			while (i < key.Length)
			{
				char c = key[i];
				if (c == EscapeChar)
				{
					if (i + 1 >= key.Length) { segments = null; return false; }   // dangling escape
					segment.Append(key[i + 1]);
					i += 2;
				}
				else if (c == separator)
				{
					result.Add(segment.ToString());
					segment.Clear();
					i++;
				}
				else
				{
					segment.Append(c);
					i++;
				}
			}
			result.Add(segment.ToString());
			segments = result.ToArray();
			return true;
		}
	}

	// One declared NCube axis: a name, a type, and (for a DateTime axis) the
	// Granularity that buckets it. Produced only by the Dimension factory (its
	// constructor is internal), so every DimensionSpec that reaches NCube has
	// already passed name/type/granularity validation.
	public readonly struct DimensionSpec
	{
		public string Name { get; }
		public Type Type { get; }
		// Set only for a DateTime dimension: the declared bucket. Null axes use the
		// default Segment(...) form.
		internal Granularity? Granularity { get; }

		internal DimensionSpec(string name, Type type, Granularity? granularity)
		{
			Name = name;
			Type = type;
			Granularity = granularity;
		}
	}

	// The Dimension[name, type] literal factory. Its two-argument indexer mirrors
	// the framework's Parameters this[name, type] idiom, so a dimension declaration
	// reads like a parameter slot. Validation is fail-fast: a blank name or a type
	// outside the identity-safe addressing vocabulary throws at the literal itself.
	// A DateTime dimension takes a third argument, the Granularity — it has no
	// default bucket, so declaring one bare throws.
	public sealed class DimensionFactory
	{
		internal DimensionFactory() { }

		public DimensionSpec this[string name, Type type]
		{
			get
			{
				ValidateNameAndType(name, type);
				if (type == typeof(DateTime))
					throw new ArgumentException(
						$"DateTime dimension '{name}' must declare a Granularity: " +
						"Dimension[name, typeof(DateTime), Granularity.Day]. A DateTime coordinate is a " +
						"bucket, not an instant; there is no default.", nameof(type));
				return new DimensionSpec(name, type, granularity: null);
			}
		}

		// DateTime axis: the third argument declares the bucket grain
		// (Granularity.Day, Granularity.Hour, ...). Only a DateTime dimension takes one.
		public DimensionSpec this[string name, Type type, Granularity granularity]
		{
			get
			{
				ValidateNameAndType(name, type);
				if (!Enum.IsDefined(typeof(Granularity), granularity))
					throw new ArgumentException($"Dimension '{name}': unknown Granularity '{granularity}'.", nameof(granularity));
				if (type != typeof(DateTime))
					throw new ArgumentException(
						$"Dimension '{name}': a Granularity may only be declared for a DateTime dimension.", nameof(granularity));
				return new DimensionSpec(name, type, granularity);
			}
		}

		private static void ValidateNameAndType(string name, Type type)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ArgumentException("A dimension name must be non-empty.", nameof(name));
			ArgumentNullException.ThrowIfNull(type);
			if (!Formation.IsSupportedDimensionType(type))
				throw new ArgumentException(
					$"Dimension '{name}' type {type.Name} is not an identity-safe addressing type " +
					"(string, int, bool, DateTime, or a domain enum).", nameof(type));
		}
	}

	// Which addressing handle a formation vends. FixedArity (Flat, NCube) is the
	// Index; Hierarchical (Tree) is the Node; Graph is the Vertex. The ensemble
	// routes to the matching handle and rejects the others.
	internal enum AddressingShape
	{
		FixedArity,
		Hierarchical,
		Graph
	}

	// The domain adjacency of a Graph formation: given a vertex id and a (labelled,
	// directed) relation, the neighbour vertex ids. INJECTED by the host — the
	// ensemble never decides where edges come from, so the same delegate may read a
	// static table, a rehydrated performer's domain state, or any domain source.
	// Direction is carried by the relation name (a domain word), not by a separate
	// argument. Return empty (not null) for a vertex with no such edges.
	public delegate IEnumerable<string> GraphAdjacency(string vertexId, string relation);

	// The calendar grain a DateTime addressing dimension buckets to. Declared as the
	// third argument of a DateTime dimension literal:
	//   Dimension["day", typeof(DateTime), Granularity.Day]
	//
	// It names which components of a DateTime coordinate are significant; every
	// finer component must be zero in the value the developer passes (the framework
	// validates, it does not truncate — see Formation.CanonicalizeDate). It is the
	// first per-dimension declared CANONICALISER (see Formation's header); the value
	// is read by its own calendar components, with no timezone conversion.
	//
	// Second is the FINEST grain — there is deliberately no Millisecond. The
	// framework's own event time is second-precision: Now and a journaled OccurredAt
	// drop sub-second when serialized, so a finer addressing grain could never be
	// satisfied by a value that has round-tripped through the journal. Bounding the
	// vocabulary at Second keeps addressing consistent with the framework's clock.
	public enum Granularity
	{
		Year,
		Month,
		Day,
		Hour,
		Minute,
		Second
	}
}
