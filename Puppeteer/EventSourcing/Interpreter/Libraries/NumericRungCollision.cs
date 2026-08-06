using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Puppeteer.EventSourcing.Interpreter.Libraries
{
	// Two overloads of one verb whose parameter at some position differs only in NUMERIC RUNG —
	// foo(int) beside foo(decimal), foo(double) beside foo(decimal). The refusal here is not about
	// binding: with both present an int argument binds foo(int) EXACTLY and nothing can outrank exact.
	// It is about what the second overload does to entries ALREADY WRITTEN.
	//
	// Before it existed, `foo(100000)` bound foo(decimal) by widening, and the journal recorded the
	// call TEXT, not the binding. Adding the exact overload makes that same text bind foo(int) on the
	// next replay, so history changes meaning — silently, and the value is even identical, so nothing
	// downstream looks wrong.
	//
	// WHY THE CAST IS ASKED FOR, since the engine does not need it: it is a SIGNATURE, not a
	// mechanism. Writing `100000m` or `(int)100000` is how the author records that they have seen the
	// collision and are taking responsibility for the entries already journaled. The refusal lifts as
	// soon as it is written, because what was missing was the acknowledgement.
	//
	// WHY IT FIRES HERE AND NOT ON EVERY WIDENING: declaring two rungs of one verb is an unusual
	// library shape, so this is quiet almost always — and the moment it speaks is the moment the
	// author actually created the collision. The mirror of it, refusing every widening, would demand a
	// cast in the common case where a single overload makes the call unambiguous, which is noise the
	// author learns to ignore.
	//
	// WHAT IT DOES NOT FIX: rehydration runs BEFORE the first live Perform, so on the boot that
	// follows the library change the older entries have already rebound by the time this speaks. The
	// practice that covers it is replaying a production journal before cutover, where this refusal
	// fires in the rehearsal. It is recorded rather than assumed.
	internal static class NumericRungCollision
	{
		// True when the surviving candidates offer more than one numeric rung at this position, so the
		// same argument text could have bound a different overload under an earlier version of the
		// library. An argument that carries an explicit cast is already signed and is not asked twice.
		internal static bool Collides(
			IReadOnlyList<MethodBase> candidates,
			int position,
			Type argumentType,
			AstExpression argument,
			out MethodBase boundNow,
			out MethodBase boundBefore)
		{
			boundNow = null;
			boundBefore = null;

			if (candidates == null || candidates.Count < 2) return false;
			if (argument is OpCast) return false;
			if (argumentType == null || !AstExpression.IsPromotableNumeric(argumentType)) return false;

			foreach (MethodBase candidate in candidates)
			{
				ParameterInfo[] parameters = candidate.GetParameters();
				if (position >= parameters.Length) continue;
				Type rung = parameters[position].ParameterType;
				if (!AstExpression.IsPromotableNumeric(rung)) continue;

				if (rung == argumentType)
				{
					boundNow ??= candidate;
				}
				else
				{
					boundBefore ??= candidate;
				}
			}

			// Only the shape that rewrites history: one overload takes the argument's own rung — so it
			// wins now — while another takes a wider one, which is what an earlier library would have
			// bound. Two wider rungs with no exact one among them is a different case: nothing changed
			// meaning, because no exact overload appeared.
			if (boundNow == null || boundBefore == null) return false;

			// double <-> decimal was exempt here while it had no signature: the reading such a call HAS
			// could not be written, because the cast keyword `double` was an alias of `decimal` and
			// produced the opposite reading. With the keywords separated both readings are expressible,
			// so the pair is refused like the rest — a refusal is only legitimate once its remedy exists.
			return true;
		}

		internal static LanguageException Refuse(Type receiverType, string memberName, int position, MethodBase boundNow, MethodBase boundBefore)
		{
			ArgumentNullException.ThrowIfNull(receiverType);
			ArgumentNullException.ThrowIfNull(memberName);

			StringBuilder message = new StringBuilder();
			message.Append('\'').Append(receiverType.Name).Append('.').Append(memberName);
			message.Append("' declares two overloads whose argument #").Append(position + 1);
			message.Append(" differs only in numeric rung: ").Append(Describe(boundNow));
			message.Append(" and ").Append(Describe(boundBefore)).Append('.');

			message.Append(" THIS call binds ").Append(Describe(boundNow)).Append(" exactly. Write the rung explicitly");
			message.Append(" — a typed literal such as 100000m, or a cast such as (int)100000 — to record that you have");
			message.Append(" seen this. The engine does not need it to bind; it needs to know the choice was yours.");

			message.Append(" ENTRIES ALREADY IN THE JOURNAL carry this same text and bound ").Append(Describe(boundBefore));
			message.Append(", because ").Append(Describe(boundNow)).Append(" did not exist when they were written, and they");
			message.Append(" will now replay to ").Append(Describe(boundNow)).Append(" instead. Nothing written here changes");
			message.Append(" that, and the journal is not edited: materialize the state under the library that wrote those");
			message.Append(" entries, and they stop needing replay.");

			return new LanguageException(message.ToString());
		}

		private static string Describe(MethodBase candidate)
		{
			if (candidate == null) return "an overload";

			StringBuilder signature = new StringBuilder();
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
