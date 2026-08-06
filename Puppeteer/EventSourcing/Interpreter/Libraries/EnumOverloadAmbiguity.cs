using System;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// Ambient scope that marks a resolution as REPLAY of journaled text rather than ADMISSION of
	// new text. The distinction is the whole contract of the ambiguity refusal below:
	//
	//   LIVE   (admitting new text)  -> a genuinely ambiguous binding is REFUSED, while the author
	//                                   is present to state which one they meant.
	//   REPLAY (admitting nothing)   -> EXACTNESS governs. The entry binds what its text says, with
	//                                   no author action and no refusal.
	//
	// The asymmetry is safe because it changes only WHICH TEXT IS ACCEPTED, never WHICH METHOD RUNS:
	// an ambiguity that the gate can refuse never reaches the journal in the first place, and one
	// that appeared later (because the author added an overload to their own library after the entry
	// was written) must not reach back and change the meaning of history. Refusing at replay would
	// be a diagnosis with no treatment — a journaled literal has no declaration anywhere that could
	// carry the author's intent.
	//
	// Backed by AsyncLocal, matching AuthoredRenderScope: the value is captured when the rehydration
	// pipeline creates its stages, so the resolution a stage performs later — including the LAZY
	// resolution a compiled Action defers to its first invocation — sees the scope its Program was
	// replayed under. That ordering is the reason this is not a plain per-call argument.
	internal static class ReplayResolutionScope
	{
		private static readonly AsyncLocal<bool> _active = new AsyncLocal<bool>();

		internal static bool Active => _active.Value;

		internal static IDisposable Enter()
		{
			bool prev = _active.Value;
			_active.Value = true;
			return new Restorer(prev);
		}

		private sealed class Restorer : IDisposable
		{
			private readonly bool prev;
			private bool disposed;

			internal Restorer(bool prev)
			{
				this.prev = prev;
			}

			public void Dispose()
			{
				if (disposed) return;
				_active.Value = prev;
				disposed = true;
			}
		}
	}

	// One argument, two readings: a string value whose text happens to name an enum member can bind
	// an enum parameter (by name) or a parameter of its own type. When BOTH readings are available
	// on the same call, nothing in the text says which the author meant, so the engine refuses
	// instead of resolving by fiat.
	//
	// This replaces a preference. The method path used to short-circuit on the enum reading BEFORE
	// it had even finished looking for an exact match, so the enum overload outranked exactness —
	// while the constructor path already preferred exactness explicitly
	// (NewInstance.IsConstructorExactTypeMatch: "no widening, no enum-binding"). The two disagreed
	// about the same question. Exactness now decides in both, and the case where a preference used
	// to be needed is refused in both.
	//
	// Refusing rather than silently switching to the exact reading is what makes the change safe to
	// adopt: a call that relied on the old preference fails loudly, with the remedy in the message,
	// instead of quietly starting to invoke a different method.
	internal static class EnumOverloadAmbiguity
	{
		// Granularity note: the condition is evaluated over the CANDIDATE SET of one call, not per
		// argument position. A candidate that binds some argument as an enum member, coexisting with
		// a candidate that satisfies the same call without any enum-binding, is the ambiguity —
		// naming the position would not change which calls are refused, only the wording.
		internal static bool IsAmbiguous(int enumBoundCandidates, bool hasCandidateWithoutEnumBinding)
		{
			if (enumBoundCandidates == 0) return false;
			return enumBoundCandidates > 1 || hasCandidateWithoutEnumBinding;
		}

		internal static LanguageException Refuse(Type receiverType, string memberName, MethodBase enumCandidate, MethodBase plainCandidate)
		{
			ArgumentNullException.ThrowIfNull(receiverType);
			ArgumentNullException.ThrowIfNull(memberName);

			StringBuilder message = new StringBuilder();
			message.Append("The call to '").Append(receiverType.Name).Append('.').Append(memberName);
			message.Append("' is ambiguous: the argument can bind ").Append(Describe(enumCandidate));
			message.Append(" reading its value as an enum member");
			if (plainCandidate != null)
			{
				message.Append(", or ").Append(Describe(plainCandidate)).Append(" reading it as a value of its own type");
			}
			else
			{
				message.Append(", and more than one enum overload accepts it");
			}
			message.Append(". Say which one you mean by casting the argument at the call site: ");
			message.Append("a cast to the value type binds the value overload, a cast to the enum type binds the enum one.");
			return new LanguageException(message.ToString());
		}

		private static string Describe(MethodBase candidate)
		{
			if (candidate == null) return "an overload";

			StringBuilder signature = new StringBuilder();
			// A constructor's reflected name is '.ctor'; the author wrote the class name.
			string writtenName = candidate is ConstructorInfo
				? (candidate.DeclaringType?.Name ?? candidate.Name)
				: candidate.Name;
			signature.Append('\'').Append(writtenName).Append('(');
			ParameterInfo[] parameters = candidate.GetParameters();
			for (int i = 0; i < parameters.Length; i++)
			{
				if (i > 0) signature.Append(", ");
				signature.Append(parameters[i].ParameterType.Name);
			}
			signature.Append(")'");
			return signature.ToString();
		}
	}
}
