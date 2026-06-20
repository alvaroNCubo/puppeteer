using System;
using System.Collections.Generic;
using System.Text;

namespace Puppeteer.EventSourcing.Follower
{
	// Spec de cuantificacion universal de un Reaction:
	//   (estado, hora, fecha) in ($states, $hours) × $dates
	// Declara las variables de la tupla (estado, hora, fecha) y los FACTORES fuente
	// que se cruzan. Cada factor es:
	//   - una coleccion simple ($dates), o
	//   - un grupo ZIP entre parentesis ($states, $hours) cuyas colecciones se
	//     emparejan por indice (lockstep): states[i] solo va con hours[i]; deben tener
	//     la misma longitud.
	// El conjunto de obligaciones es el producto cartesiano de los factores, donde un
	// factor zip itera indice-alineado. Las variables de la tupla mapean POSICIONALMENTE
	// al orden APLANADO de las fuentes en la expresion (por eso los zipeados van
	// adyacentes): (estado, hora, fecha) <- [states, hours, dates]. El '×' es el cruce
	// entre factores; se acepta tambien '*' como alterno tecleable. El cartesiano puro
	// ($x × $y × $z, cada factor una sola fuente) sigue siendo el caso default.
	internal sealed class ForEachSpec
	{
		private readonly string[] tupleVars;
		private readonly string[] sourceVars;
		private readonly int[] groupSizes;
		private readonly ReadOnlyGroups sourceGroups;

		internal IReadOnlyList<string> TupleVars => Array.AsReadOnly(tupleVars);
		internal IReadOnlyList<string> SourceVars => Array.AsReadOnly(sourceVars);
		internal IReadOnlyList<IReadOnlyList<string>> SourceGroups => sourceGroups;
		internal string RawText { get; }

		private ForEachSpec(string[] tupleVars, string[] sourceVars, int[] groupSizes, string rawText)
		{
			this.tupleVars = tupleVars;
			this.sourceVars = sourceVars;
			this.groupSizes = groupSizes;
			this.sourceGroups = new ReadOnlyGroups(sourceVars, groupSizes);
			RawText = rawText;
		}

		internal static ForEachSpec Parse(string text)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(text);

			string s = text.Trim();

			int close = s.IndexOf(')');
			if (s.Length == 0 || s[0] != '(' || close < 0)
				throw new LanguageException($"ForEach debe tener la forma '(a, b, ...) in $x × ($y, $z) × ...'. Recibido: '{text}'.");

			string tuplePart = s.Substring(1, close - 1);
			string[] tuple = SplitTrim(tuplePart, ',');
			if (tuple.Length == 0)
				throw new LanguageException($"ForEach: la lista de variables de la tupla no puede estar vacia. Recibido: '{text}'.");
			foreach (string t in tuple) ValidateBareIdentifier(t, text);

			string rest = s.Substring(close + 1).TrimStart();
			if (rest.Length < 3 || rest[0] != 'i' || rest[1] != 'n' || !char.IsWhiteSpace(rest[2]))
				throw new LanguageException($"ForEach: se esperaba 'in' despues de la tupla. Recibido: '{text}'.");
			rest = rest.Substring(2).TrimStart();

			string[] rawFactors = rest.Split(new[] { '×', '*' }, StringSplitOptions.RemoveEmptyEntries);

			List<string> flatSources = new List<string>();
			List<int> sizes = new List<int>();
			foreach (string rawFactor in rawFactors)
			{
				string factor = rawFactor.Trim();
				if (factor.Length == 0) continue;

				if (factor[0] == '(')
				{
					if (factor[factor.Length - 1] != ')')
						throw new LanguageException($"ForEach: grupo zip mal formado '{factor}' (falta ')') en '{text}'.");

					string inner = factor.Substring(1, factor.Length - 2);
					string[] groupVars = SplitTrim(inner, ',');
					if (groupVars.Length == 0)
						throw new LanguageException($"ForEach: grupo zip vacio '()' en '{text}'.");

					foreach (string gv in groupVars) flatSources.Add(ParseSourceVar(gv, text));
					sizes.Add(groupVars.Length);
				}
				else
				{
					if (factor[factor.Length - 1] == ')')
						throw new LanguageException($"ForEach: factor mal formado '{factor}' (')' sin '(') en '{text}'.");

					flatSources.Add(ParseSourceVar(factor, text));
					sizes.Add(1);
				}
			}

			if (flatSources.Count == 0)
				throw new LanguageException($"ForEach: se esperaba al menos una coleccion fuente ($var) despues de 'in'. Recibido: '{text}'.");

			string[] sources = flatSources.ToArray();
			if (tuple.Length != sources.Length)
				throw new LanguageException($"ForEach: la tupla tiene {tuple.Length} variable(s) pero las fuentes suman {sources.Length}; deben coincidir 1:1. Recibido: '{text}'.");

			return new ForEachSpec(tuple, sources, sizes.ToArray(), text);
		}

		// Materializa el conjunto de obligaciones: el producto cartesiano de los FACTORES.
		// Un factor simple (grupo de 1 fuente) aporta una fila por elemento; un factor zip
		// (grupo de >=2 fuentes) aporta una fila por indice, emparejando las fuentes por
		// posicion (deben tener la misma longitud, si no LanguageException). Cada obligacion
		// es un object[] de longitud TupleVars.Count en el orden APLANADO de las fuentes
		// (el mismo orden en que se reciben aqui y en que mapean las variables de la tupla).
		// Recibe las colecciones en orden aplanado, igual que las alimenta
		// MatchTree.MaterializeObligations leyendo por SourceVars (que sigue plano).
		internal List<object[]> CrossProduct(IReadOnlyList<System.Collections.IEnumerable> sources)
		{
			ArgumentNullException.ThrowIfNull(sources);
			if (sources.Count != sourceVars.Length)
				throw new LanguageException($"ForEach: se esperaban {sourceVars.Length} coleccion(es) fuente pero se recibieron {sources.Count}.");

			List<List<object>> materialized = new List<List<object>>(sources.Count);
			for (int i = 0; i < sources.Count; i++)
			{
				System.Collections.IEnumerable src = sources[i];
				if (src == null)
					throw new LanguageException($"ForEach: la coleccion fuente '${sourceVars[i]}' es null.");

				List<object> elements = new List<object>();
				foreach (object o in src) elements.Add(o);
				materialized.Add(elements);
			}

			// Filas por factor: un factor simple -> una fila [elem] por elemento; un factor
			// zip -> una fila [src0[i], src1[i], ...] por indice (longitudes iguales).
			List<List<object[]>> factorRows = new List<List<object[]>>(groupSizes.Length);
			int offset = 0;
			for (int g = 0; g < groupSizes.Length; g++)
			{
				int size = groupSizes[g];
				if (size == 1)
				{
					List<object> only = materialized[offset];
					List<object[]> rows = new List<object[]>(only.Count);
					foreach (object element in only) rows.Add(new object[] { element });
					factorRows.Add(rows);
				}
				else
				{
					int length = materialized[offset].Count;
					for (int k = 1; k < size; k++)
					{
						if (materialized[offset + k].Count != length)
							throw new LanguageException(
								$"ForEach: las fuentes del grupo zip ({GroupSourceNames(offset, size)}) deben tener la misma longitud (emparejamiento por indice); " +
								$"'${sourceVars[offset]}' tiene {length} y '${sourceVars[offset + k]}' tiene {materialized[offset + k].Count}.");
					}

					List<object[]> rows = new List<object[]>(length);
					for (int i = 0; i < length; i++)
					{
						object[] row = new object[size];
						for (int k = 0; k < size; k++) row[k] = materialized[offset + k][i];
						rows.Add(row);
					}
					factorRows.Add(rows);
				}
				offset += size;
			}

			List<object[]> result = new List<object[]>();
			BuildTuples(factorRows, 0, new object[sourceVars.Length], 0, result);
			return result;
		}

		private static void BuildTuples(List<List<object[]>> factorRows, int factorIndex, object[] current, int pos, List<object[]> result)
		{
			if (factorIndex == factorRows.Count)
			{
				object[] tuple = new object[current.Length];
				Array.Copy(current, tuple, current.Length);
				result.Add(tuple);
				return;
			}

			foreach (object[] row in factorRows[factorIndex])
			{
				for (int k = 0; k < row.Length; k++) current[pos + k] = row[k];
				BuildTuples(factorRows, factorIndex + 1, current, pos + row.Length, result);
			}
		}

		private string GroupSourceNames(int offset, int size)
		{
			var sb = new StringBuilder();
			for (int k = 0; k < size; k++)
			{
				if (k > 0) sb.Append(", ");
				sb.Append('$').Append(sourceVars[offset + k]);
			}
			return sb.ToString();
		}

		private static string ParseSourceVar(string src, string context)
		{
			string s = src.Trim();
			if (s.Length < 2 || s[0] != '$')
				throw new LanguageException($"ForEach: cada coleccion fuente debe ser una variable capturada '$nombre'. Recibido: '{s}' en '{context}'.");
			string name = s.Substring(1);
			ValidateBareIdentifier(name, context);
			return name;
		}

		private static string[] SplitTrim(string s, char sep)
		{
			string[] parts = s.Split(sep, StringSplitOptions.RemoveEmptyEntries);
			List<string> result = new List<string>(parts.Length);
			foreach (string p in parts)
			{
				string t = p.Trim();
				if (t.Length > 0) result.Add(t);
			}
			return result.ToArray();
		}

		private static void ValidateBareIdentifier(string id, string context)
		{
			if (string.IsNullOrWhiteSpace(id))
				throw new LanguageException($"ForEach: identificador vacio en '{context}'.");
			if (!(char.IsLetter(id[0]) || id[0] == '_'))
				throw new LanguageException($"ForEach: identificador invalido '{id}' en '{context}' (debe empezar con letra o '_').");
			for (int i = 1; i < id.Length; i++)
			{
				if (!(char.IsLetterOrDigit(id[i]) || id[i] == '_'))
					throw new LanguageException($"ForEach: identificador invalido '{id}' en '{context}'.");
			}
		}

		// Vista de solo lectura de los grupos zip (cada grupo = las fuentes que se
		// emparejan por indice). Un grupo de longitud 1 es una fuente simple.
		private sealed class ReadOnlyGroups : IReadOnlyList<IReadOnlyList<string>>
		{
			private readonly IReadOnlyList<string>[] groups;

			internal ReadOnlyGroups(string[] flatSources, int[] groupSizes)
			{
				groups = new IReadOnlyList<string>[groupSizes.Length];
				int offset = 0;
				for (int g = 0; g < groupSizes.Length; g++)
				{
					string[] members = new string[groupSizes[g]];
					Array.Copy(flatSources, offset, members, 0, groupSizes[g]);
					groups[g] = Array.AsReadOnly(members);
					offset += groupSizes[g];
				}
			}

			public IReadOnlyList<string> this[int index] => groups[index];
			public int Count => groups.Length;
			public IEnumerator<IReadOnlyList<string>> GetEnumerator() => ((IEnumerable<IReadOnlyList<string>>)groups).GetEnumerator();
			System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => groups.GetEnumerator();
		}
	}
}
