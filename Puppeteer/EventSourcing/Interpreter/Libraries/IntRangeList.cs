using System.Collections.Generic;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// An integer list materialized from a range literal {start..end}. It IS a
	// List<int>, so it flows through foreach and every collection-parameter path
	// unchanged, but it remembers the bounds it was built from. That lets the
	// journal serialize it by comprehension ({start..end}) instead of enumerating
	// every element, keeping the event O(1) in the range length.
	//
	// v1 materializes the elements eagerly (see docs/rfc/foreach-range-literal.md,
	// section 6); a lazy backing is a later optimization.
	sealed class IntRangeList : List<int>
	{
		internal int Start { get; }
		internal int End { get; }

		// Public so the compiled path's Expression.New can bind the constructor
		// (GetConstructor's default lookup is public-only); the type itself stays internal.
		public IntRangeList(int start, int end)
		{
			Start = start;
			End = end;
			// Inclusive both ends. start <= end ascends with step +1; start > end
			// descends with step -1 ({5..1} => 5,4,3,2,1). A range always has at least
			// one element (start == end => one); the empty collection is the {} literal.
			// long counter so int.MaxValue / int.MinValue endpoints terminate instead of wrapping.
			if (start <= end)
			{
				for (long i = start; i <= end; i++) Add((int)i);
			}
			else
			{
				for (long i = start; i >= end; i--) Add((int)i);
			}
		}

		// O(1) sanity check: the contents still describe the [Start..End] run (either
		// direction). It only re-checks length and endpoints (not a full contiguity scan,
		// which would be the dropped B2), so an instance mutated after construction falls
		// back to enumeration on serialization rather than silently losing data.
		internal bool StillDescribesBounds()
		{
			if (Count == 0)
			{
				return false; // a range always has at least one element
			}
			long span = Start <= End ? (long)End - Start : (long)Start - End;
			return Count == span + 1 && this[0] == Start && this[Count - 1] == End;
		}
	}
}
