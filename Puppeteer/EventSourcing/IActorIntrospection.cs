using System;

namespace Puppeteer.EventSourcing
{
	// Read-only introspection surface for CLI / AI / MCP / test-harness use.
	// Separated from the domain DSL by construction: the verbs live in an
	// interface that EVERY actor exposes by virtue of being Puppeteer, not by
	// virtue of any concrete domain. The domain DSL stays intact.
	//
	// Implemented by ActorHandler (internal). Exposed as
	// actor.Introspection (public) in Actor.cs — same pattern as
	// Materialization / Reactions / Distill.
	//
	// Read-only by contract: writing to the journal goes through the
	// invocation surface (Perform / Tell), never through here. That asymmetry
	// enables the shadow mode of the AI-native CLI — introspection over a
	// shadow can never mutate the primary's journal even if the AI tries.
	//
	// Stage 1 (signed 2026-05-31): a single verb ShowEntry. Range / Find /
	// Describe arrive in later steps of the AI-native CLI.
	public interface IActorIntrospection
	{
		// Returns the journal record with EntryId == entryId, formatted
		// as Toon (Token-Oriented Object Notation). If it does not exist, throws
		// LanguageException with a diagnostic message.
		//
		// Toon shape (tentative, subject to refinement from feedback):
		//
		//   id: <long>
		//   kind: "script" | "invocation" | "define"
		//   at: <DateTime>
		//   <kind-specific fields>
		//
		// Script:     script
		// Invocation: actionId, arguments
		// Define:     actionId, define
		//
		// exposeData appears only when present in the record.
		string ShowEntry(long entryId);

		// Returns the current Define entry for an actionId, formatted as
		// Toon. The invocation entry only records actionId — to know WHAT that
		// action is (its DSL signature) the AI queries this surface.
		//
		// Redefinition policy: if the journal contains multiple Define
		// entries for the same actionId (atypical case — re-declaration during
		// development), the one with the greatest EntryId wins. The asymmetry is
		// deliberate: the current version is the one observed by the code running now.
		//
		// Toon shape:
		//
		//   actionId: <int>
		//   defineEntryId: <long>     # where it was declared (suitable for show entry)
		//   at: <DateTime>
		//   define: "<canonical DSL of the action>"
		//
		// If no Define exists for that actionId, throws LanguageException.
		string ShowAction(int actionId);

		// Returns the set of globals currently live in the actor's symbol table,
		// formatted as Toon. Solves the problem of "how does the AI know that
		// 'obj' already exists in this actor": before defining any singleton the
		// AI queries this verb and learns what the domain has already put there.
		//
		// The symbol table contains ONLY globals by construction of the interpreter —
		// block locals { ... } and action parameters never reach the table,
		// they live in a separate transient cache (SymbolTable.cacheVariables).
		//
		// Toon shape:
		//
		//   symbols:
		//     - name: "obj"
		//       staticType: "Base"
		//       runtimeType: "Base"
		//     - name: "other"
		//       staticType: "Base"
		//       runtimeType: "Derived"
		//
		// staticType: the type registered in the table — the polymorphic upper-bound
		//   chosen on the first assignment. Useful to validate calls statically.
		// runtimeType: value?.GetType() — the actual type of the value at this moment.
		//   May be a subclass of staticType when polymorphism is active.
		// If the table is empty: 'symbols: []'.
		string ShowSymbols();

		// Returns detail for a single symbol by name (case-insensitive). If it does
		// not exist, throws LanguageException.
		//
		// Toon shape:
		//
		//   name: "obj"
		//   staticType: "Base"
		//   runtimeType: "Base"
		//   value: "Base('sample')"     # only if the class overrides ToString
		//
		// The `value` field appears ONLY when the runtimeType class overrides
		// ToString(); the default object.ToString() = type FullName would be redundant
		// with runtimeType and is omitted. The legacy print(StringBuilder) path is NOT honored
		// here — that output is JSON-shaped and breaks the Toon contract; a domain
		// that wants an inspectable representation must override ToString.
		string ShowSymbol(string name);

		// Returns constructors, interfaces and DSL-accessible methods of a
		// class loaded in the actor's LibraryAssemblies. Solves "what can I do
		// with a type X" — the AI chains: show symbols -> sees obj: Base ->
		// show class Base -> sees the invocable methods.
		//
		// Case-insensitive match against Type.Name (consistent with class
		// resolution in the rest of the Puppeteer parser). If the class is not in
		// any loaded library, throws LanguageException.
		//
		// Toon shape:
		//
		//   class: "Derived"
		//   constructors:
		//     - signature: "Derived(String)"
		//   interfaces:
		//     - name: "IBehavior"
		//   fields:
		//     - signature: "Count : Int32"
		//       declaredOn: "Derived"
		//     - signature: "Tag : String"
		//       declaredOn: "Base"
		//   properties:
		//     - signature: "Name : String { get; }"
		//       declaredOn: "Derived"
		//     - signature: "Ready : Boolean { get; set; }"
		//       declaredOn: "Derived"
		//   methods:
		//     - signature: "Act() -> Void"
		//       declaredOn: "Derived"
		//     - signature: "GetKind() -> String"
		//       declaredOn: "Base"
		//
		// Applied filters (uniform for methods, fields and properties):
		//   - Visibility: public + internal + protected-internal (aligned with
		//     the interpreter's ParserValidation). Excludes private and pure protected.
		//   - IsSpecialName on methods: excludes get_/set_ of properties + operator
		//     overloads (properties already have their own collection).
		//   - DeclaringType == typeof(object): excludes the 4 inherited base methods.
		//   - CompilerGenerated on fields: excludes backing fields of auto-properties
		//     (those compiler details are not part of the domain API).
		//
		// Inheritance: GetFields/GetProperties/GetMethods without DeclaredOnly include
		// inherited members. The declaredOn field makes explicit where each comes from.
		//
		// Properties: included if AT LEAST ONE accessor (get or set) is callable. The
		// signature emits only the callable accessors — a property with a private
		// setter is shown as '{ get; }'. One with both as '{ get; set; }'.
		//
		// Readonly fields: 'readonly' prefix in the signature
		// ('readonly Tag : String').
		//
		// Generics: types like IEnumerable&lt;Item&gt; are formatted legibly
		// ('IEnumerable&lt;Item&gt;'), not with the CLR suffix (`1[Item]).
		//
		// Interfaces: includes transitive ones — all interfaces the type
		// satisfies, not only those declared directly on the class.
		string ShowClass(string className);

		// Returns ALL reactions registered in the actor, with their aggregate
		// MatchCount and the per-Seek counters (entered/matched + checkpoint
		// detected/confirmed). Solves "what reactions does this actor have and how
		// are they doing" without forcing the AI to page the counters one by one.
		//
		// Toon shape:
		//
		//   reactions:
		//     - name: "FirstReaction"
		//       matchCount: 17
		//       seeks:
		//         - name: "FirstSeek"
		//           entered: 17
		//           matched: 17
		//           detected: 142
		//           confirmed: 142
		//
		// Detected/confirmed come from DiaryStorage.GetReactionCheckpoint(reactionId,
		// level). If the reaction never ran (reactionId == long.MinValue), there
		// is no checkpoint yet and both are reported as 0 — consistent with what
		// GetReactionCheckpoint returns for unknown reactionIds.
		// If there are no reactions: 'reactions: []'.
		string ShowReactions();

		// Returns detail for ONE reaction, more extensive than the corresponding item
		// in ShowReactions. Includes: direction (Forward/Backward), hydration mode
		// (Shared/Independent + optional untilSeek), Action plane terminator
		// (Program.Emit / Causation.Continue / Metadata.Elide / Metadata.Materialize /
		// None), literal OnMatch patterns per Seek, per-Seek counters, and the ring
		// buffer LastMatches (up to 32 recent captures with bindings).
		//
		// Case-insensitive match against Name. If the reaction does not exist in the
		// actor, throws LanguageException.
		//
		// Toon shape:
		//
		//   name: "FirstReaction"
		//   direction: "Forward"
		//   hydration: "Shared(untilSeek: 'SecondSeek')"
		//   action: "Metadata.Elide"
		//   matchCount: 17
		//   seeks:
		//     - name: "FirstSeek"
		//       isFinal: false
		//       onMatch:
		//         - "obj = Base();"
		//       entered: 17
		//       matched: 17
		//       detected: 142
		//       confirmed: 142
		//   lastMatches:
		//     - entryId: 142
		//       occurredAt: 06/01/2026 14:22:08
		//       bindings:
		//         - name: "obj"
		//           value: "..."
		//
		// hydration is a compacted string to keep the output linear: when there is
		// an untilSeek it is written in parentheses; without untilSeek only the mode
		// remains ("Shared" / "Independent"). action concatenates plane + verb; Materialize
		// adds the destination in quotes ("Metadata.Materialize 'dest'").
		string ShowReaction(string name);

		// Dry-match of a DSL pattern against the actor's current journal, WITHOUT
		// creating a permanent reaction and WITHOUT side effects observable to the
		// domain (no journal entry, no Action plane, no cross-actor effects). Useful to
		// "see where it would match" a reaction before declaring it, or to use the
		// Reactions engine as a correlation query over past events.
		//
		// Consolidation signed 2026-06-01: TryPattern and FindPattern produce identical
		// output and share implementation — the difference was only framing
		// ("test how it would match" vs "find correlated events"). A single verb.
		//
		// The pattern is the same DSL as .Seek().OnMatch(...): bindings without '$' are
		// free names that match the script's identifier; bindings with '$'
		// are parameters that appear in the results ([$variable:type]). The
		// classes ([_:Class], Class(...)) must exist in the actor's LibraryAssemblies.
		//
		// Minimal side effect: the first invocation with a given pattern creates an entry
		// (formattedReaction -> reactionId) in the DiaryStorage persistent reactions
		// registry. Re-invocations of the SAME pattern reuse that id (idempotency
		// by name). The internal name uses a hash of the pattern — it does not pollute
		// the ShowReactions enumeration because the temp reaction is NOT added to the
		// actor's C# registry.
		//
		// Toon shape:
		//
		//   pattern: "<DSL pattern as-is>"
		//   matchesFound: 3
		//   matches:
		//     - entryId: 42
		//       occurredAt: 06/01/2026 14:22:08
		//       bindings:
		//         - name: "anInstance"
		//           value: "..."
		//         - name: "quantity"
		//           value: 5
		//
		// If the pattern does not parse, throws LanguageException with the PatternParser error.
		// If there are no matches: 'matches: []' with matchesFound: 0.
		string FindPattern(string patternDsl);

		// Returns the state of the parameter pool BY SHAPE (shape-keyed). Each shape
		// is the script of a V2 operation; the pool reuses slots (Parameter +
		// VariableSymbol) across invocations of the same shape. highWater is the
		// historical concurrency peak of that shape — the signal that points to tuning
		// the business logic when an endpoint accumulates unbounded concurrency (it is NOT a
		// memory bound; the pool grows freely and decays on its own). Ordered by highWater desc.
		//
		// Toon shape:
		//
		//   parameterPools:
		//     - shape: "{ box = Class(); print box.Goo(id) v; }"
		//       live: 0          # rented (out) now
		//       idle: 2          # idle and reusable now
		//       highWater: 50    # historical simultaneous concurrency peak
		//
		// If there are no live shapes: 'parameterPools: []'.
		string ShowParameterPools();
	}
}
