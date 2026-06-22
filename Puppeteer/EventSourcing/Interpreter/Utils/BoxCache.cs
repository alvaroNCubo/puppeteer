using System;
using System.Runtime.CompilerServices;

namespace Puppeteer.EventSourcing.Interpreter.Utils
{
	// Perf improvement A: cache of boxes for common value-types on the Parameters
	// deserialization path (LoadArguments) and for the defaults of Out parameters.
	// Sharing the box is safe because boxed primitives are immutable: the code always
	// replaces the reference in VariableSymbol.value, never mutates the box content
	// in-place, and there are no identity comparisons over the parameter values.
	internal static class BoxCache
	{
		internal static readonly object True = true;
		internal static readonly object False = false;

		internal static readonly object DateTimeDefault = default(DateTime);
		internal static readonly object DecimalDefault = default(decimal);
		internal static readonly object DoubleDefault = default(double);

		private const int SmallIntCount = 256; // 0..255
		private static readonly object[] SmallInts;

		internal static readonly object IntZero;

		static BoxCache()
		{
			SmallInts = new object[SmallIntCount];
			for (int i = 0; i < SmallIntCount; i++) SmallInts[i] = i;
			IntZero = SmallInts[0];
		}

		internal static object Box(int value)
		{
			if ((uint)value < SmallIntCount) return SmallInts[value];
			return value;
		}

		internal static object Box(bool value) => value ? True : False;

		// Improvement (d): generic de-box for the preparation path (UserParameter<T>),
		// where the value arrives as T unboxed. For int/bool it extracts the value with
		// Unsafe.As (without boxing) and returns the cached box; for the other T it boxes
		// normally (decimal/double/DateTime are not cacheable; string and refs do not box).
		// The typeof(T)==typeof(...) idiom is resolved by the JIT per instantiation, so
		// only the branch for its type runs. NOTE: the indexer object this[string,
		// Type] CANNOT be de-boxed — the box is emitted by the compiler at the call-site
		// before entering the setter.
		internal static object Box<T>(T value)
		{
			if (typeof(T) == typeof(int)) return Box(Unsafe.As<T, int>(ref value));
			if (typeof(T) == typeof(bool)) return Box(Unsafe.As<T, bool>(ref value));
			return value;
		}
	}
}
