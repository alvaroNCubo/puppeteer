using System;
using System.Runtime.CompilerServices;

namespace Puppeteer.EventSourcing.Interpreter.Utils
{
	// Mejora A de perf: cache de boxes para value-types comunes en la ruta de
	// deserializacion de Parameters (LoadArguments) y para los defaults de los
	// parametros Out. Compartir el box es seguro porque los primitivos boxeados son
	// inmutables: el codigo siempre reemplaza la referencia en VariableSymbol.value,
	// nunca muta el contenido del box in-place, y no hay comparaciones por identidad
	// sobre los valores de los parametros.
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

		// Mejora (d): de-box generico para el path de preparacion (UserParameter<T>),
		// donde el value llega como T sin boxear. Para int/bool extrae el valor con
		// Unsafe.As (sin boxear) y devuelve el box cacheado; para los demas T boxea
		// normal (decimal/double/DateTime no son cacheables; string y refs no boxean).
		// El idioma typeof(T)==typeof(...) es resuelto por el JIT por instanciacion, asi
		// que solo se ejecuta la rama de su tipo. NOTA: el indexer object this[string,
		// Type] NO puede de-boxearse — el box lo emite el compilador en el call-site
		// antes de entrar al setter.
		internal static object Box<T>(T value)
		{
			if (typeof(T) == typeof(int)) return Box(Unsafe.As<T, int>(ref value));
			if (typeof(T) == typeof(bool)) return Box(Unsafe.As<T, bool>(ref value));
			return value;
		}
	}
}
