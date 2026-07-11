using System;
using System.Collections;
using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Follower
{
	// The captured variables of a pattern match — the fixed-shape bag
	// `($p1:type, …, $pn:type)` the matcher fills as it walks a pattern. This is a
	// DEDICATED concept, not a bare `Parameters` doing double duty: the private
	// `Parameters` is an internal implementation detail (competence of this concept
	// only), and the type name says what the bag is FOR. See
	// notes/reactions-resolution-reuse.md ("MatchParameters — name the capture bag").
	//
	// The intention-revealing verbs (Capture / Has / Get / Reset) are the preferred
	// surface. The indexer / ContainsParameter / enumerator mirrors are compatibility
	// shims that let the matcher's existing call sites compile unchanged while only the
	// declared TYPE flips to MatchParameters — this is the behavior-preserving first step
	// of the read-only-reuse refactor. `Underlying` is exposed ONLY at the matcher's
	// return boundary, where the match result must still hand a `Parameters` to MatchTree;
	// that boundary is tightened in a later step of the same refactor.
	internal sealed class MatchParameters : IEnumerable<Parameter>
	{
		private readonly Parameters parameters;

		internal MatchParameters(Parameters parameters)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			this.parameters = parameters;
		}

		// ----- Intention-revealing surface -----

		// Capture (or overwrite) the value of a pattern variable, typed by the signature.
		internal void Capture(string name, Type type, object value) => parameters[name, type] = value;

		// Whether a pattern variable has already been captured (used for cross-Seek
		// correlation: a re-seen name is a constraint, not a re-capture).
		internal bool Has(string name) => parameters.ContainsParameter(name);

		// The captured parameter (its value is read via .GetValue()).
		internal Parameter Get(string name) => parameters[name];

		// Recycle the bag for a fresh match attempt.
		internal void Reset()
		{
			parameters.PurgeUserParameters();
			parameters.Clear();
		}

		// The private Parameters — internal competence of this concept. Exposed ONLY at the
		// matcher return boundary (the match result still flows into MatchTree as a
		// Parameters). Do not use elsewhere.
		internal Parameters Underlying => parameters;

		// ----- Compatibility shims (delegate 1:1 to the private Parameters) -----

		internal object this[string name, Type type]
		{
			set => parameters[name, type] = value;
		}

		internal Parameter this[string name] => parameters[name];

		internal bool ContainsParameter(string name) => parameters.ContainsParameter(name);

		internal void PurgeUserParameters() => parameters.PurgeUserParameters();

		internal void Clear() => parameters.Clear();

		public IEnumerator<Parameter> GetEnumerator() => ((IEnumerable<Parameter>)parameters).GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)parameters).GetEnumerator();
	}
}
