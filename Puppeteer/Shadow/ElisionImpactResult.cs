using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Shadow Replay — S4. Resultado del elision-impact diff: comparacion de las salidas
	// observables (queries) entre rehidratar el journal SIN elision vs CON la elision
	// candidata. IsSafe == true significa que elidir el set candidato NO cambia ninguna
	// observacion provista. Caveat honesto: seguro respecto a los observadores ACTUALES
	// que se pasan, NO frente a observadores futuros ni externos — ese "alguien lo
	// necesitara alguna vez" sigue siendo juicio de dominio.
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

	// Una observacion (query) cuya salida difiere entre el replay sin-elision y el replay
	// con-elision: evidencia de que algo observable dependia de los eventos elididos.
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
