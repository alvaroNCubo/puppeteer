// Lab 5 instrumentation bridge — lab branch only, not mergeable.
// Counts AST dispatch events in interpreted-mode execution. Compiled mode
// emits IL that bypasses Execute() and is therefore NOT counted by this
// counter — see Lab 5 headline.md for the methodological rationale.
//
// Counted dispatch sites (rule C: domain library invocations):
//   - DotAccess.Execute()        — property reads + method calls (and via
//                                  inheritance, ChainedDotAccess).
//   - NewInstance.Execute()      — constructor invocations.
//
// Excluded dispatch sites (control flow / arithmetic primitives):
//   Id, Literal*, OpAdd/Subtract/Multiply/Divide/Cast/Equal/etc.,
//   Parenthesis, Ternary, ForStatement, IfStatement.
//
// Public API; Increment() is called from inside Puppeteer.dll, Reset/Count
// from the lab test assembly. Static + Interlocked is used (not ThreadStatic)
// because actor dispatch may happen on a worker thread distinct from the
// caller; ThreadStatic would split the counter across threads and lose data.

using System.Threading;

namespace Puppeteer
{
    public static class LabCounter
    {
        private static long _count;

        public static void Reset() => Interlocked.Exchange(ref _count, 0);
        public static long Count => Interlocked.Read(ref _count);
        public static void Increment() => Interlocked.Increment(ref _count);
    }
}
