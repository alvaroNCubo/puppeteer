using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using Choreography.Dispatch;
using Choreography.Input;
using Choreography.Observability;
using Choreography.Theater;
using Puppeteer;
using Puppeteer.EventSourcing.Interpreter.Formatters;

namespace Choreography.Ensemble
{
    public class EnsemblePerformance<T> : IDisposable where T : Performance
    {
        private readonly ConcurrentDictionary<string, T> performers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, T> factory;
        // The address-space SHAPE (Flat by default). Orthogonal to hosting and
        // input routing: the formation only decides how a structured address
        // collapses to the one canonical key that keys `performers`. Flat leaves
        // GetOrCreate(string)/ConsumeFrom exactly as they were.
        private readonly Formation formation;
        // Consume seam (ensemble-level routing): the handlers prototype gives
        // every performer its Dispatch vocabulary at birth; the per-performer
        // Dispatch instances live here so a routed signal reaches the right
        // atom's serial flow. The subscriptions are the bound input sources.
        private Action<Dispatch.Dispatch> dispatchHandlersPrototype;
        private Action<DispatchOptions> dispatchOptionsPrototype;
        private readonly ConcurrentDictionary<string, Dispatch.Dispatch> dispatchers = new(StringComparer.OrdinalIgnoreCase);
        private readonly object dispatchCreationLock = new();
        private readonly ConcurrentBag<IDisposable> consumeSubscriptions = new();
        private IOutputFormatter formatterPrototype;  // null = default JsonFormatter
        // Push channel (Paper 9 / OutputTarget): the sink + optional push
        // formatter, cascaded to V2 performers like Formatter. null = pull-only.
        private IOutputSink outputTargetPrototype;
        private IOutputFormatter outputTargetFormatPrototype;
        // Authoring transpiler (input-side mirror of formatterPrototype),
        // cascaded to V2 performers like the Formatter. Default = Identity so
        // every V2 performer always carries one.
        private INotationTranspiler transpilerPrototype = IdentityTranspiler.Instance;
        // Logger injected by the host. null = each Performance starts with its
        // default ConsoleLogger. Applied to existing performers in .Logger(x)
        // and propagated to new ones in GetOrCreate. Per-actor (not singleton): each
        // Performance in the ensemble receives the same impl but stores it in its
        // own ActorHandler.
        private IPuppeteerLogger loggerPrototype;

        public EnsemblePerformance(Func<string, T> factory)
            : this(factory, Formation.Flat)
        {
        }

        // Formation-aware constructor. Flat (the default above) is the rank-1
        // dictionary; pass Formation.NCube(...) to give the address space more
        // structure. The formation only reshapes keys — every other seam
        // (Dispatch, Formatter, ConsumeFrom, eviction) is untouched.
        public EnsemblePerformance(Func<string, T> factory, Formation formation)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.formation = formation ?? throw new ArgumentNullException(nameof(formation));
        }

        // ── Formatter API (cascades to V2 performers) ─────────────────────
        //
        // Sets the formatter prototype for the ensemble. Apply behavior:
        //  - V2 performers (and any derived class with a Formatter(...)
        //    setter): the prototype is propagated to each existing
        //    performer immediately, AND new performers created via
        //    GetOrCreate are configured with the prototype.
        //  - V1 performers (or any other T:Performance without the
        //    Formatter setter): silent ignore. V1 is fixed JSON by
        //    design; mixing V1+V2 in one ensemble is
        //    allowed but the V1 actors continue emitting JSON.

        public EnsemblePerformance<T> Formatter(IOutputFormatter prototype)
        {
            this.formatterPrototype = prototype;
            foreach (var perf in performers.Values)
            {
                if (perf is PerformanceV2 v2)
                {
                    v2.Formatter(prototype);
                }
                // V1 / others: silent ignore.
            }
            return this;
        }

        // ── Output target API (cascades to V2 performers) ─────────────────
        //
        // Sets the push sink for the ensemble. Same cascade semantics as
        // Formatter: propagated to existing V2 performers immediately and applied
        // to new ones in GetOrCreate. A null sink reverts the ensemble to
        // pull-only. V1 / others: silent ignore (V1 is pull-only by design).
        // The push renders TOON by default; pass a formatter to override.
        public EnsemblePerformance<T> OutputTarget(IOutputSink transport, IOutputFormatter format = null)
        {
            this.outputTargetPrototype = transport;
            this.outputTargetFormatPrototype = format;
            foreach (var perf in performers.Values)
            {
                if (perf is PerformanceV2 v2)
                {
                    v2.OutputTarget(transport, format);
                }
                // V1 / others: silent ignore.
            }
            return this;
        }

        // ── Consume API (ensemble-level routing) ──────────────────────────
        //
        // Routing is fractal (medium -> node -> ACTOR -> verb -> args). A single
        // actor consumes with Dispatch.ConsumeFrom, its identity fixed at wiring
        // time; the ensemble consumes one altitude up — the ActorSelector resolves
        // the actor segment of the route, and everything below it is the same
        // per-actor seam, unchanged. Until now that segment travelled out-of-band:
        // a loose actor-name parameter the caller pushed by hand into
        // GetOrCreate(id). With ConsumeFrom the ensemble owns its routing role;
        // GetOrCreate becomes an internal detail of the route.

        // The Dispatch vocabulary prototype: applied to each performer's Dispatch
        // at creation (existing performers immediately, new ones in GetOrCreate).
        // Same cascade semantics as Formatter/OutputTarget/Logger. Unlike those,
        // this is NOT a silent-ignore seam: consuming is data, not cosmetics, so a
        // performer that cannot dispatch (non-V2) throws loudly instead of
        // dropping signals.
        public EnsemblePerformance<T> DispatchHandlers(Action<Dispatch.Dispatch> configure, Action<DispatchOptions> options = null)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            this.dispatchHandlersPrototype = configure;
            this.dispatchOptionsPrototype = options;
            foreach (var kvp in performers)
            {
                EnsureDispatch(kvp.Key, kvp.Value);
            }
            return this;
        }

        // Consume FROM an input source through the two routing facets: the
        // selector picks the performer (actor altitude), the routing shapes the
        // command (verb altitude). Null at either altitude DROPS the signal —
        // nothing is created, enqueued, or idempotency-recorded. Idempotency is
        // per-performer by construction (each atom carries its own window), so a
        // redelivered record for one performer never collides with another's.
        //
        // Several ConsumeFrom calls are the MERGE at ensemble altitude: many
        // media, one route table. Requires DispatchHandlers to be configured
        // first — the vocabulary must exist before signals can animate anyone.
        public EnsemblePerformance<T> ConsumeFrom(IInputSource source, ActorSelector selector, InputRouting routing)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);
            ArgumentNullException.ThrowIfNull(routing);
            if (dispatchHandlersPrototype == null)
                throw new InvalidOperationException(
                    "DispatchHandlers must be configured before ConsumeFrom: the ensemble needs the Dispatch vocabulary to animate the performers it routes to.");

            IDisposable subscription = source.Start(signal =>
            {
                // Actor altitude: which performer does this signal animate?
                string actorId;
                try
                {
                    actorId = selector(signal);
                }
                catch (Exception ex)
                {
                    // A selector that throws must not tear down the source: log and
                    // drop this one signal, mirroring Dispatch.ConsumeFrom's contract.
                    DispatchTracer.Instance.OnHandlerFailed($"actor-selector[{source.SourceName}]", ex);
                    return;
                }
                if (string.IsNullOrWhiteSpace(actorId)) return;   // selector dropped it

                // Verb altitude: same facet a single actor consumes through.
                DispatchCommand? command;
                try
                {
                    command = routing(signal);
                }
                catch (Exception ex)
                {
                    DispatchTracer.Instance.OnHandlerFailed($"input-routing[{source.SourceName}]", ex);
                    return;
                }
                if (command is not DispatchCommand cmd) return;   // routing dropped it
                if (cmd.MessageId == null || string.IsNullOrEmpty(cmd.RawMessage)) return;

                try
                {
                    T performer = GetOrCreate(actorId);
                    Dispatch.Dispatch dispatch = EnsureDispatch(actorId, performer);
                    dispatch.Receive(cmd.MessageId, cmd.RawMessage);
                }
                catch (ObjectDisposedException ex)
                {
                    // Evicted between lookup and receive: drop; on a real broker the
                    // record stays redeliverable and the next delivery recreates the
                    // performer. (The eviction in-flight guard refines this window.)
                    DispatchTracer.Instance.OnHandlerFailed($"ensemble-consume[{source.SourceName}]", ex);
                }
            });

            consumeSubscriptions.Add(subscription);
            return this;
        }

        // ByKey overload: on a partitioned medium the partition key already IS
        // the actor segment of the route.
        public EnsemblePerformance<T> ConsumeFrom(IInputSource source, InputRouting routing)
            => ConsumeFrom(source, ActorSelectors.ByKey, routing);

        private Dispatch.Dispatch EnsureDispatch(string id, T performer)
        {
            if (dispatchers.TryGetValue(id, out var existing)) return existing;
            lock (dispatchCreationLock)
            {
                if (dispatchers.TryGetValue(id, out existing)) return existing;

                if (performer is not PerformanceV2 v2)
                    throw new InvalidOperationException(
                        $"Ensemble consume requires PerformanceV2 performers; '{id}' is {performer.GetType().Name}. Signals are data — dropping them silently is not an option.");

                var dispatch = v2.CreateDispatch(dispatchOptionsPrototype);
                dispatchHandlersPrototype(dispatch);
                dispatchers[id] = dispatch;
                return dispatch;
            }
        }

        // Transpiler seam (input-side mirror of Formatter): cascades the
        // authoring transpiler to existing V2 performers and applies it to new
        // ones in GetOrCreate. V1 / others: silent ignore (no Enact surface).
        public EnsemblePerformance<T> Transpiler(INotationTranspiler prototype)
        {
            if (prototype == null) throw new ArgumentNullException(nameof(prototype));
            this.transpilerPrototype = prototype;
            foreach (var perf in performers.Values)
            {
                if (perf is PerformanceV2 v2)
                {
                    v2.Transpiler(prototype);
                }
            }
            return this;
        }

        // Logger seam: per-actor (not singleton). The prototype is propagated to each
        // existing performer and applied to new ones in GetOrCreate. Fluent to
        // align with Formatter(). Without injection each Performance uses its
        // default ConsoleLogger (Error -> stderr, Debug -> stdout).
        public EnsemblePerformance<T> Logger(IPuppeteerLogger logger)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            this.loggerPrototype = logger;
            foreach (var perf in performers.Values)
            {
                perf.Logger(logger);
            }
            return this;
        }

        // B.3.4: configure automatic Script → Action promotion threshold for
        // the V1 performers hosted by this ensemble. Stores the prototype so
        // new performers created via GetOrCreate also receive the setting,
        // AND propagates immediately to existing PerformanceV1 instances.
        // V2 performers are silently ignored — they explicitly declare their
        // Actions and have no Script-shaped path to promote.
        // null = use the ActorHandler default (30).
        private int? promotionThresholdPrototype;
        public EnsemblePerformance<T> InternalAutomaticPromotion(int threshold)
        {
            this.promotionThresholdPrototype = threshold;
            foreach (var perf in performers.Values)
            {
                if (perf is PerformanceV1 v1)
                {
                    v1.InternalAutomaticPromotion(threshold);
                }
            }
            return this;
        }

        public T GetOrCreate(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentNullException(nameof(id));

            T performer = GetOrCreateCore(id);
            // Consume seam: when the ensemble has a Dispatch vocabulary, every
            // performer carries it from birth (outside GetOrAdd's factory so a
            // losing race never creates a dispatch on a discarded instance).
            if (dispatchHandlersPrototype != null)
            {
                EnsureDispatch(id, performer);
            }
            return performer;
        }

        private T GetOrCreateCore(string id)
        {
            return performers.GetOrAdd(id, key =>
            {
                var perf = factory(key);
                // If the ensemble has a formatter prototype set, propagate
                // it to newly created V2 performers.
                if (formatterPrototype != null && perf is PerformanceV2 v2)
                {
                    v2.Formatter(formatterPrototype);
                }
                // Propagate the push sink (Paper 9 / OutputTarget) to newly
                // created V2 performers (mirror of the formatter propagation).
                if (outputTargetPrototype != null && perf is PerformanceV2 v2OutputTarget)
                {
                    v2OutputTarget.OutputTarget(outputTargetPrototype, outputTargetFormatPrototype);
                }
                // Propagate the authoring transpiler to newly created V2
                // performers (mirror of the formatter propagation above).
                if (perf is PerformanceV2 v2Transpiler)
                {
                    v2Transpiler.Transpiler(transpilerPrototype);
                }
                // Per-actor logger: each newly created Performance receives the
                // sink configured at the ensemble level (if the host wired it
                // via .Logger(x)).
                if (loggerPrototype != null)
                {
                    perf.Logger(loggerPrototype);
                }
                // B.3.4: propagate the promotion threshold to newly created V1
                // performers (V2 doesn't have a Script path to promote).
                if (promotionThresholdPrototype.HasValue && perf is PerformanceV1 v1Promo)
                {
                    v1Promo.InternalAutomaticPromotion(promotionThresholdPrototype.Value);
                }
                return perf;
            });
        }

        // ── Formation addressing API (Index handle) ───────────────────────
        //
        // A structured address for the ensemble's formation. `ensemble["juan"]`
        // starts a chain that accumulates one segment per axis; each `[...]`
        // returns an Index so it composes (`ensemble["juan"]["oct"]["usd"]`). The
        // chain resolves at a terminal:
        //   .Actor   -> the single performer at this (complete, concrete) address
        //               (GetOrCreate — creates or fetches; cardinality-1).
        //   .Actors  -> the LIVE performers matching this address (a slice, when an
        //               axis was left free with .Any); enumerates, NEVER creates.
        // An implicit conversion Index -> T is `.Actor`, so an Index flows straight
        // into a T-typed slot without the terminal — except when the address is a
        // slice, which has no single actor.
        //
        // One overload per identity-safe addressing type. There is deliberately no
        // this[object] — and no this[double]/this[decimal]: a coordinate is a key,
        // and a key is identity, so a real (treacherous float equality; non-canonical
        // decimal form) must not compile as a segment. The compiler enforces the
        // vocabulary here; ValidateAxisType enforces the declared type of THIS axis
        // at run time.
        public Index this[string segment] => Root[segment];
        public Index this[int segment] => Root[segment];
        public Index this[bool segment] => Root[segment];
        public Index this[DateTime segment] => Root[segment];
        public Index this[Enum segment] => Root[segment];

        // A free FIRST axis (a rank-1 slice for Flat, the leading dimension for an
        // NCube). Chain more axes, then resolve with .Actors.
        public Index Any => Root.Any;

        // The Index handle is for fixed-arity formations (Flat, NCube). Tree and
        // Graph are addressed through their own handles (.Tree / .Vertex).
        private Index Root
        {
            get
            {
                if (formation.Shape != AddressingShape.FixedArity)
                    throw new InvalidOperationException(
                        $"{formation.Name} is not a fixed-arity formation; address it through its own handle (.Tree / .Vertex), not the Index.");
                return new Index(this, formation);
            }
        }

        // Resolve a slice to the LIVE performers matching a pattern. A null entry
        // in `pattern` is a free axis (.Any) and matches anything; concrete
        // segments match case-insensitively, mirroring the performer dictionary's
        // OrdinalIgnoreCase identity. Keys not minted by this formation are skipped
        // (TryDecode returns false), never matched. Enumerates only what is alive —
        // nothing is created or rehydrated.
        internal IEnumerable<T> ResolveSlice(string[] pattern)
        {
            foreach (var kvp in performers)
            {
                if (!formation.TryDecode(kvp.Key, out var segments)) continue;
                if (segments.Length != pattern.Length) continue;

                bool match = true;
                for (int axis = 0; axis < pattern.Length; axis++)
                {
                    if (pattern[axis] == null) continue;   // free axis
                    if (!string.Equals(pattern[axis], segments[axis], StringComparison.OrdinalIgnoreCase))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) yield return kvp.Value;
            }
        }

        // A readonly addressing handle over one formation. Structs, not entities:
        // a handle carries only the owner, the formation, and the segments gathered
        // so far (a null slot = a free axis left by .Any). singleton ⊂ selection —
        // .Actor is the cardinality-1 face, .Actors the set-valued face of the SAME
        // handle; they differ only in how the performer set is derived.
        public readonly struct Index
        {
            private readonly EnsemblePerformance<T> owner;
            private readonly Formation formation;
            private readonly string[] segments;   // null entry = free axis (.Any)

            internal Index(EnsemblePerformance<T> owner, Formation formation)
            {
                this.owner = owner;
                this.formation = formation;
                this.segments = Array.Empty<string>();
            }

            private Index(EnsemblePerformance<T> owner, Formation formation, string[] segments)
            {
                this.owner = owner;
                this.formation = formation;
                this.segments = segments;
            }

            private Index Append(string segment)
            {
                var next = new string[segments.Length + 1];
                Array.Copy(segments, next, segments.Length);
                next[segments.Length] = segment;   // null = free axis
                return new Index(owner, formation, next);
            }

            // Validate the value's type against the declared type of THIS axis
            // (the addressing analog of UserParameter<T>), then append its
            // canonical text segment.
            private Index AppendTyped(Type valueType, string canonicalSegment)
            {
                formation.ValidateAxisType(segments.Length, valueType);
                return Append(canonicalSegment);
            }

            // One overload per identity-safe addressing type (no double/decimal: a
            // real is not a safe identity token). The text form follows the journal's
            // serialization (InvariantCulture; enums by member name) so an address
            // segment reads like a journaled argument. DateTime is the exception: it
            // has no default form — it is bucketed by the axis's declared Granularity
            // (CanonicalizeDate), so every instant in a bucket keys the same actor.
            public Index this[string segment] => AppendTyped(typeof(string), segment ?? throw new ArgumentNullException(nameof(segment)));
            public Index this[int segment] => AppendTyped(typeof(int), Formation.Segment(segment));
            public Index this[bool segment] => AppendTyped(typeof(bool), Formation.Segment(segment));
            public Index this[DateTime segment]
            {
                get
                {
                    formation.ValidateAxisType(segments.Length, typeof(DateTime));
                    return Append(formation.CanonicalizeDate(segments.Length, segment));
                }
            }
            public Index this[Enum segment]
            {
                get
                {
                    if (segment == null) throw new ArgumentNullException(nameof(segment));
                    return AppendTyped(segment.GetType(), Formation.Segment(segment));
                }
            }

            // Leave the current axis free: the address becomes a slice, resolved
            // with .Actors. No value, so only the axis's existence is checked.
            public Index Any
            {
                get
                {
                    formation.ValidateAxisExists(segments.Length);
                    return Append(null);
                }
            }

            // The single performer at this address. Requires a complete, concrete
            // address (right arity, no free axis) — a slice has no single actor.
            // Creates or fetches, like GetOrCreate.
            public T Actor
            {
                get
                {
                    formation.ValidateArity(segments.Length);
                    foreach (var segment in segments)
                    {
                        if (segment == null)
                            throw new InvalidOperationException(
                                "A free axis (.Any) has no single actor; resolve the slice with .Actors.");
                    }
                    return owner.GetOrCreate(formation.Encode(segments));
                }
            }

            // The live performers matching this address. A slice (with .Any axes)
            // fans out; a fully concrete address yields the one live performer if
            // it exists (never creating it). Enumerates only what is alive.
            public IEnumerable<T> Actors
            {
                get
                {
                    formation.ValidateArity(segments.Length);
                    return owner.ResolveSlice(segments);
                }
            }

            // Implicit conversion = .Actor, so an Index flows into a T slot. On a
            // slice this throws (no single actor) — resolve with .Actors instead.
            public static implicit operator T(Index index) => index.Actor;
        }

        // ── Formation addressing API (Node handle, Tree only) ─────────────
        //
        // The root of the tree. `ensemble.Tree["usa/tx/austin"].Actor` (path split
        // on the separator) or `ensemble.Tree.Child("usa").Child("tx")` (segment by
        // segment). Every node is addressable at any level with .Actor; the set-
        // valued faces .Children / .Subtree resolve by prefix-scan over the live
        // keys — the tree is sparse and lazy, so they enumerate only what is alive.
        public Node Tree
        {
            get
            {
                if (formation.Shape != AddressingShape.Hierarchical)
                    throw new InvalidOperationException(
                        $"{formation.Name} is not a hierarchical formation; address it through its own handle, not the Tree/Node.");
                return new Node(this, formation);
            }
        }

        // Resolve the live descendants of a tree node by prefix-scan. `ancestor` is
        // the node's decoded path; a live key belongs to the result when its own
        // path has `ancestor` as a prefix (segment-wise, case-insensitively).
        // immediateOnly keeps just the children one level down; otherwise the whole
        // subtree, INCLUDING the node itself when it is a live actor. Keys not minted
        // by this formation are skipped. Nothing is created.
        internal IEnumerable<T> ResolveDescendants(string[] ancestor, bool immediateOnly)
        {
            foreach (var kvp in performers)
            {
                if (!formation.TryDecode(kvp.Key, out var segments)) continue;
                if (segments.Length < ancestor.Length) continue;
                if (immediateOnly && segments.Length != ancestor.Length + 1) continue;

                bool isPrefix = true;
                for (int depth = 0; depth < ancestor.Length; depth++)
                {
                    if (!string.Equals(ancestor[depth], segments[depth], StringComparison.OrdinalIgnoreCase))
                    {
                        isPrefix = false;
                        break;
                    }
                }
                if (isPrefix) yield return kvp.Value;
            }
        }

        // A readonly addressing handle over a Tree formation. Like Index, a struct
        // carrying only the owner, the formation, and the path gathered so far — but
        // hierarchical: every node is an actor (.Actor at any level), navigable up
        // (.Parent) and down (indexer / .Child), and set-valued along its subtree
        // (.Children, .Subtree). There are no free axes: a tree fans out by prefix,
        // not by wildcard.
        public readonly struct Node
        {
            private readonly EnsemblePerformance<T> owner;
            private readonly Formation formation;
            private readonly string[] segments;

            internal Node(EnsemblePerformance<T> owner, Formation formation)
            {
                this.owner = owner;
                this.formation = formation;
                this.segments = Array.Empty<string>();
            }

            private Node(EnsemblePerformance<T> owner, Formation formation, string[] segments)
            {
                this.owner = owner;
                this.formation = formation;
                this.segments = segments;
            }

            private Node AppendRaw(string segment)
            {
                var next = new string[segments.Length + 1];
                Array.Copy(segments, next, segments.Length);
                next[segments.Length] = segment;
                return new Node(owner, formation, next);
            }

            // Descend one raw segment (no path splitting) — the primitive descent,
            // and the way to descend a string segment that itself contains the
            // separator.
            public Node Child(string segment)
            {
                if (string.IsNullOrWhiteSpace(segment))
                    throw new ArgumentException("A tree segment must be non-empty.", nameof(segment));
                return AppendRaw(segment);
            }

            // The string indexer is the path CONVENIENCE: it splits on the tree
            // separator and descends one level per part, so ["usa/tx/austin"] is the
            // three-level descent. A single part (no separator) is one level. For a
            // raw string that must keep a literal separator, use Child(segment).
            public Node this[string path]
            {
                get
                {
                    if (path == null) throw new ArgumentNullException(nameof(path));
                    Node current = this;
                    foreach (var part in path.Split(formation.Separator))
                    {
                        current = current.Child(part);
                    }
                    return current;
                }
            }

            // Typed single-segment descents (identity-safe vocabulary, no
            // double/decimal). A tree has no declared schema, so these coerce but do
            // not type-check. DateTime is deliberately ABSENT: a bucket can only be
            // declared on an NCube dimension, and a full-precision instant is not a
            // safe identity — descend a tree by pre-bucketed parts instead
            // (e.g. [2026][4][18], or .Child("2026-04-18")).
            public Node this[int segment] => AppendRaw(Formation.Segment(segment));
            public Node this[bool segment] => AppendRaw(Formation.Segment(segment));
            public Node this[Enum segment] => AppendRaw(Formation.Segment(segment ?? throw new ArgumentNullException(nameof(segment))));

            // One level up. The root (empty path) has no parent; the parent of a
            // top-level node IS the root — a valid position for listing .Children,
            // though not itself an actor.
            public Node Parent
            {
                get
                {
                    if (segments.Length == 0)
                        throw new InvalidOperationException("The tree root has no parent.");
                    var up = new string[segments.Length - 1];
                    Array.Copy(segments, up, up.Length);
                    return new Node(owner, formation, up);
                }
            }

            // The performer at this exact path. Every node is addressable, at any
            // level — but the root (empty path) is the tree itself, not an actor.
            // Creates or fetches, like GetOrCreate.
            public T Actor
            {
                get
                {
                    if (segments.Length == 0)
                        throw new InvalidOperationException("The tree root is not an actor; address at least one level.");
                    return owner.GetOrCreate(formation.Encode(segments));
                }
            }

            // The live performers exactly one level below this node. From the root,
            // the top-level actors. Enumerates only what is alive; never creates.
            public IEnumerable<T> Children => owner.ResolveDescendants(segments, immediateOnly: true);

            // The live performers in this node's subtree — every descendant at any
            // depth, INCLUDING this node when it is itself a live actor. From the
            // root, every actor in the ensemble. Enumerates only what is alive.
            public IEnumerable<T> Subtree => owner.ResolveDescendants(segments, immediateOnly: false);

            // Implicit conversion = .Actor, so a Node flows into a T slot.
            public static implicit operator T(Node node) => node.Actor;
        }

        // ── Formation addressing API (Vertex handle, Graph only) ──────────
        //
        // A vertex is Flat-anchored by id — `ensemble.Vertices["juan"]` — and the id
        // IS the key (the graph lives in the EDGES, not the key). From a vertex you
        // traverse DOMAIN relations resolved by the injected adjacency: .Along one
        // hop, .Reach a hop-bounded frontier, both yielding a Selection that
        // enumerates the LIVE performers among the reached ids (never creating) and
        // is itself traversable. Bounded only — no shortest path, no unbounded
        // closure. Parallels .Tree: the graph handle is reached by id, not by path.
        public VertexSet Vertices
        {
            get
            {
                if (formation.Shape != AddressingShape.Graph)
                    throw new InvalidOperationException(
                        $"{formation.Name} is not a graph formation; address it through its own handle, not the Vertex.");
                return new VertexSet(this);
            }
        }

        // Anchors a Vertex by id: ensemble.Vertices["juan"]. The id IS the key.
        public readonly struct VertexSet
        {
            private readonly EnsemblePerformance<T> owner;
            internal VertexSet(EnsemblePerformance<T> owner) { this.owner = owner; }

            public Vertex this[string id]
            {
                get
                {
                    if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
                    return new Vertex(owner, id);
                }
            }
        }

        // Resolve a set of vertex ids to the LIVE performers among them (the id IS
        // the key for a Graph). Skips ids with no live actor — traversal never
        // creates. Deterministic order is not promised (dictionary enumeration).
        internal IEnumerable<T> LivePerformers(IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                if (performers.TryGetValue(id, out var performer))
                    yield return performer;
            }
        }

        // A readonly handle anchored at one graph vertex (Flat by id). Unlike Index
        // and Node it does not chain segments with `[...]`; a graph is traversed
        // along LABELLED, DIRECTED domain relations (.Along / .Reach), and direction
        // is carried by the relation name, not the handle.
        public readonly struct Vertex
        {
            private readonly EnsemblePerformance<T> owner;
            private readonly string id;

            internal Vertex(EnsemblePerformance<T> owner, string id)
            {
                this.owner = owner;
                this.id = id;
            }

            // The anchored performer itself (the id is its key). Creates or fetches.
            public T Actor => owner.GetOrCreate(id);

            // One hop along a relation: the neighbours the injected adjacency reports
            // for this vertex. Set-valued and chainable.
            public Selection Along(string relation) => owner.Step(new[] { id }, relation);

            // A hop-bounded frontier: every vertex reachable within `hops` steps
            // along the relation (breadth-first, cycle-safe, excluding the origin).
            // BOUNDED by construction — this is not transitive closure.
            public Selection Reach(string relation, int hops) => owner.Reach(new[] { id }, relation, hops);

            // Implicit conversion = .Actor, so a Vertex flows into a T slot.
            public static implicit operator T(Vertex vertex) => vertex.Actor;
        }

        // The result of a graph traversal: a set of reached vertex ids. Set-valued
        // (enumerates the LIVE performers among them, never creating) and itself
        // traversable (.Along / .Reach continue from the whole set). .Ids exposes the
        // raw reached ids so a host may materialise them explicitly if it wants.
        public readonly struct Selection : IEnumerable<T>
        {
            private readonly EnsemblePerformance<T> owner;
            private readonly IReadOnlyCollection<string> ids;

            internal Selection(EnsemblePerformance<T> owner, IReadOnlyCollection<string> ids)
            {
                this.owner = owner;
                this.ids = ids;
            }

            public IReadOnlyCollection<string> Ids => ids;

            public Selection Along(string relation) => owner.Step(ids, relation);

            public Selection Reach(string relation, int hops) => owner.Reach(ids, relation, hops);

            public IEnumerator<T> GetEnumerator() => owner.LivePerformers(ids).GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        // One traversal hop from a set of ids: the union of the injected adjacency's
        // neighbours over the relation. The origin ids are not re-included.
        internal Selection Step(IReadOnlyCollection<string> fromIds, string relation)
        {
            if (string.IsNullOrWhiteSpace(relation))
                throw new ArgumentNullException(nameof(relation));
            var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fromId in fromIds)
            {
                foreach (var neighbour in formation.Adjacency(fromId, relation))
                {
                    if (!string.IsNullOrWhiteSpace(neighbour))
                        reached.Add(neighbour);
                }
            }
            return new Selection(this, reached);
        }

        // A hop-bounded breadth-first frontier from a set of ids. Cycle-safe (a
        // visited set), origin excluded from the result. BOUNDED by `hops` — it is a
        // frontier walk, not a transitive closure.
        internal Selection Reach(IReadOnlyCollection<string> fromIds, string relation, int hops)
        {
            if (string.IsNullOrWhiteSpace(relation))
                throw new ArgumentNullException(nameof(relation));
            if (hops < 1)
                throw new ArgumentOutOfRangeException(nameof(hops), "Reach needs at least one hop; a graph is only traversed a bounded distance.");

            var visited = new HashSet<string>(fromIds, StringComparer.OrdinalIgnoreCase);
            var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var frontier = new List<string>(fromIds);
            for (int hop = 0; hop < hops && frontier.Count > 0; hop++)
            {
                var next = new List<string>();
                foreach (var fromId in frontier)
                {
                    foreach (var neighbour in formation.Adjacency(fromId, relation))
                    {
                        if (string.IsNullOrWhiteSpace(neighbour)) continue;
                        if (visited.Add(neighbour))
                        {
                            reached.Add(neighbour);
                            next.Add(neighbour);
                        }
                    }
                }
                frontier = next;
            }
            return new Selection(this, reached);
        }

        public bool Evict(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentNullException(nameof(id));

            if (performers.TryRemove(id, out var performance))
            {
                // The performer's Dispatch dies with it (Performance.Dispose owns
                // the disposal); the route table entry goes first so a concurrent
                // signal recreates rather than targets a disposing atom.
                dispatchers.TryRemove(id, out _);
                performance.Dispose();
                return true;
            }
            return false;
        }

        public void EvictInactive(TimeSpan threshold)
        {
            var now = DateTime.Now;
            foreach (var kvp in performers)
            {
                if (now - kvp.Value.LastActivity > threshold)
                {
                    if (performers.TryRemove(kvp.Key, out var performance))
                    {
                        dispatchers.TryRemove(kvp.Key, out _);
                        performance.Dispose();
                    }
                }
            }
        }

        public IEnumerable<string> ListPerformers() => performers.Keys;

        public int Count => performers.Count;

        public string LockAllWhileNotSyncronized()
        {
            var results = new System.Text.StringBuilder();
            foreach (var kvp in performers)
            {
                var result = kvp.Value.LockWhileNotSyncronized();
                results.AppendLine($"{kvp.Key}: {result}");
            }
            return results.ToString();
        }

        public void UnlockAllAndRunAlive()
        {
            foreach (var kvp in performers)
            {
                kvp.Value.UnlockAndRunAlive();
            }
        }

        public bool AreAllAlive
        {
            get
            {
                if (performers.IsEmpty) return false;
                foreach (var kvp in performers)
                {
                    if (!kvp.Value.IsAlive) return false;
                }
                return true;
            }
        }

        // Mirror of Dispatch.Dispose's order: stop the media first so no new
        // signal routes while the atoms drain, then dispose every performer
        // (each Performance.Dispose drains and disposes its own Dispatch).
        public void Dispose()
        {
            foreach (IDisposable subscription in consumeSubscriptions)
            {
                try { subscription.Dispose(); } catch { }
            }

            foreach (var kvp in performers)
            {
                if (performers.TryRemove(kvp.Key, out var performance))
                {
                    try { performance.Dispose(); } catch { }
                }
            }
            dispatchers.Clear();
        }
    }
}
