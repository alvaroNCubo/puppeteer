using Puppeteer.EventSourcing;
using System;

namespace Puppeteer
{
	// Shadow Replay — S1 (handoff_shadow_S1_implementation.md). Modo de replay del
	// shadow: PointInTime = catch-up hasta un techo y para (fork); Continuous =
	// seguir el caudal real near-real-time (S2 — stub en S1).
	public enum ShadowMode
	{
		PointInTime,
		Continuous
	}

	// Configuracion de un Shadow. El shadow es una derivacion-laboratorio de un actor
	// de produccion: lee el journal real (replay) pero escribe en su PROPIO storage y
	// produce CERO efecto externo (Tells neutralizados, no se registra como destination
	// de Materialization). Ver §3.0 del design.
	//
	// Invariante de aislamiento: el storage del shadow JAMAS puede ser el mismo del
	// primary. ActorHandler.CreateShadow valida la separacion por nombre de actor; ademas
	// el shadow corre con un nombre derivado (`<primary>-shadow-<Id>`) que en los backends
	// por-nombre (InMemory) y por-path (FileSystem) garantiza un storage distinto cuando
	// la connection coincide.
	public sealed class ShadowConfig
	{
		// Identificador del experimento. Se combina con el nombre del primary para
		// producir el nombre del actor shadow: `<primary>-shadow-<Id>`.
		public string Id { get; }

		// Backend del storage PROPIO del shadow.
		public DatabaseType ShadowStorageType { get; }

		// Connection / path del storage propio del shadow. Para InMemory cualquier
		// string no vacio sirve (el backend particiona por nombre de actor). Para
		// FileSystem debe ser un path DISTINTO del primary.
		public string ShadowStorageConnection { get; }

		// Modo de replay. S1 solo implementa PointInTime de forma completa (via
		// SyncUntil). Continuous (StartShadowing) es S2 y queda como stub.
		public ShadowMode Mode { get; }

		// TTL opcional del shadow completo (kill-all: storage + reactions). null =
		// sin TTL gestionado por el framework (el host dispone manualmente). El TTL
		// por K8s Job es S6 y no se implementa aqui.
		public TimeSpan? Ttl { get; }

		// Callback opcional para registrar reactions sobre el shadow recien construido.
		// S1: las definiciones de reactions del primary no se serializan/clonan
		// automaticamente (el builder es imperativo). El host re-declara aqui las
		// reactions estaticas que quiera observar + las experimentales nuevas, usando
		// la MISMA API de Tema A apuntando al shadow. Si es null, el shadow arranca
		// sin reactions.
		public Action<Actor> ConfigureReactions { get; }

		public ShadowConfig(
			string id,
			DatabaseType shadowStorageType,
			string shadowStorageConnection,
			ShadowMode mode = ShadowMode.PointInTime,
			TimeSpan? ttl = null,
			Action<Actor> configureReactions = null)
		{
			ArgumentNullException.ThrowIfNullOrWhiteSpace(id);
			ArgumentNullException.ThrowIfNullOrWhiteSpace(shadowStorageConnection);
			if (ttl.HasValue && ttl.Value <= TimeSpan.Zero)
				throw new LanguageException($"Shadow TTL must be greater than zero when specified (got {ttl.Value}).");

			this.Id = id;
			this.ShadowStorageType = shadowStorageType;
			this.ShadowStorageConnection = shadowStorageConnection;
			this.Mode = mode;
			this.Ttl = ttl;
			this.ConfigureReactions = configureReactions;
		}
	}
}
