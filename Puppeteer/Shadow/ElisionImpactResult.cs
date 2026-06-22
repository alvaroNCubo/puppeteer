using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Shadow Replay — S4. Result of the elision-impact diff: comparison of the
	// observable outputs (queries) between rehydrating the journal WITHOUT elision vs WITH the
	// candidate elision. IsSafe == true means that eliding the candidate set does NOT change any
	// provided observation. Honest caveat: safe with respect to the CURRENT observers
	// that are passed in, NOT against future or external observers — that "someone will
	// need it someday" remains a domain judgment.
	public sealed class ElisionImpactResult
	{
		public bool IsSafe => Differences.Count == 0;
		public IReadOnlyList<ElisionObservationDiff> Differences { get; }

		internal ElisionImpactResult(IReadOnlyList<ElisionObservationDiff> differences)
		{
			ArgumentNullException.ThrowIfNull(differences);
			Differences = differences;
		}
	}

	// An observation (query) whose output differs between the without-elision replay and the
	// with-elision replay: evidence that something observable depended on the elided events.
	public sealed class ElisionObservationDiff
	{
		public string Observation { get; }
		public string WithoutElision { get; }
		public string WithElision { get; }

		internal ElisionObservationDiff(string observation, string withoutElision, string withElision)
		{
			ArgumentNullException.ThrowIfNull(observation);
			Observation = observation;
			WithoutElision = withoutElision;
			WithElision = withElision;
		}
	}
}
