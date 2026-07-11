using Puppeteer;

namespace Puppeteer.EventSourcing.Playbill
{
	// A playbill records audit metadata: self-contained INPUT values, frozen at the
	// moment an invocation is recorded, so the record can be replicated, backed up and
	// audited without re-running anything. Only Parameter.In belongs in it.
	//
	// The other three modifiers are meaningless in a playbill and, left unchecked, would
	// serialize into the audit record as garbage instead of a frozen value:
	//
	//   * Out / InOut are read-back channels. They carry NO input value; they exist to
	//     return a value the actor COMPUTED back into C#. A playbill is never executed,
	//     so there is nothing to compute and nothing to read back — an Out here serializes
	//     as an empty "(no value)" placeholder.
	//   * Eval is capture-at-execution: its value is produced by evaluating a DSL
	//     expression against the actor's domain state at execution time. A playbill has no
	//     execution and no domain state to evaluate against, so an Eval here is a dangling
	//     expression that can never be frozen — it would serialize its raw script text.
	//
	// So WithPlaybill accepts Parameter.In only, and violations fail fast and loud rather
	// than silently corrupting the record. If the value the caller wants in the playbill is
	// also consumed by the domain, it is passed to the script as an ordinary parameter; the
	// domain never reads the playbill, and the playbill never carries a domain channel.
	internal static class PlaybillParameterGuard
	{
		internal static void EnsureInputOnly(Parameters values, string schemaName)
		{
			foreach (Parameter parameter in values)
			{
				if (parameter.ParameterModifier == Parameter.In) continue;

				string modifier =
					parameter.ParameterModifier == Parameter.Out ? "Out" :
					parameter.ParameterModifier == Parameter.InOut ? "InOut" :
					parameter.ParameterModifier == Parameter.Eval ? "Eval" : "Unknown";

				throw new LanguageException(
					$"WithPlaybill field '{parameter.Name}' (schema '{schemaName}') was declared as {modifier}, " +
					$"but a playbill accepts Parameter.In only. A playbill records audit metadata — self-contained " +
					$"input values frozen at record time — not an execution channel. Out/InOut return a value the " +
					$"actor computed back to C# (a playbill is never executed, so there is nothing to read back); " +
					$"Eval captures a DSL expression evaluated against domain state at execution time (a playbill has " +
					$"no execution and no state to evaluate against). Pass '{parameter.Name}' as a plain In value: " +
					$"p[\"{parameter.Name}\", typeof(T)] = value. If the domain also needs it, pass it to the script " +
					$"as its own parameter — the domain must never read the playbill.");
			}
		}
	}
}
