using Puppeteer.EventSourcing;
using Puppeteer.EventSourcing.DB;
using Puppeteer.EventSourcing.Follower;
using System;
using System.Collections.Generic;

namespace Puppeteer
{
	// Shadow Replay — S1. Resultado in-process del primitivo actor.Shadow(cfg):
	// un actor derivado-laboratorio aislado del primary. Lee el journal real del
	// primary por replay (SyncUntil) pero escribe en su PROPIO storage y produce
	// CERO efecto externo (Tells neutralizados; no registrado como destination de
	// Materialization del primary).
	//
	// Ontologia (design §3.0): el Shadow es un PRIMITIVO del actor, NO un subtipo de
	// Performance. Para deployment (pod K8s con HTTP console, S6) un ShadowPerformance
	// (Choreography) lo HOSPEDA por composicion. Esta clase es la cara in-process —
	// experimentos rapidos, loops de Claude, bug-repro point-in-time.
	//
	// TTL kill-all: Dispose() tira abajo el shadow (graceful shutdown de reactions +
	// limpieza del storage para los backends que lo soportan). El TTL por K8s Job es
	// S6, no aqui.
	public sealed class Shadow : IDisposable
	{
		private readonly Actor shadowActor;
		private readonly ActorHandler shadowHandler;
		private readonly ShadowConfig config;
		private bool disposed;
		private int diffCounter;

		internal Shadow(Actor shadowActor, ShadowConfig config)
		{
			ArgumentNullException.ThrowIfNull(shadowActor);
			ArgumentNullException.ThrowIfNull(config);
			this.shadowActor = shadowActor;
			this.shadowHandler = shadowActor.Handler;
			this.config = config;
		}

		// El actor shadow (familia V1/V2 igual al primary). Expuesto para registrar
		// reactions experimentales (shadow.Actor.Reactions.DefineReaction(...)) y, en
		// V1, drivearlo con PerformCmdAsync.
		public Actor Actor => shadowActor;

		// Reactions del shadow — misma API de Tema A apuntando al shadow.
		public Reactions Reactions => shadowActor.Reactions;

		// EntryId actual del shadow (su propia linea de tiempo, divergente tras fork).
		public long CurrentEntryId => shadowHandler.CurrentEntryId;

		public ShadowMode Mode => config.Mode;
		public TimeSpan? Ttl => config.Ttl;

		// SyncUntil(toEntryId): replay del journal del primary desde GENESIS hasta
		// toEntryId inclusive, aplicado al storage propio del shadow. Techo, no piso.
		// Tras esto el shadow queda forkeado y acepta comandos locales.
		public void SyncUntil(long toEntryId)
		{
			ThrowIfDisposed();
			shadowHandler.SyncUntil(toEntryId);
		}

		// Shadow Replay — S3. Activa skip-preview (dry-run de Elide): las reactions Elide
		// del shadow capturan en Reaction.WouldSkip los EntryIds que marcarian, SIN
		// commitear la elision en ningun journal. Para inspeccionar "que se elidiria"
		// sobre datos reales replicados, sin efecto.
		public void EnableSkipPreview()
		{
			ThrowIfDisposed();
			shadowHandler.EnableSkipPreview();
		}

		// Shadow Replay — S4. Elision-impact diff (la "movida killer" del estado de
		// cuenta): para un set candidato de EntryIds a elidir, compara las salidas
		// observables (queries) entre rehidratar el journal SIN elision y CON la elision.
		// Diff vacio (result.IsSafe) => elidir ese set no cambia ninguna observacion =
		// seguro respecto a los observadores provistos. Construye dos twins aislados
		// (shadow-of-shadow del journal de ESTE shadow) y los descarta al final. Convierte
		// "¿es seguro elidir esto?" de juicio estatico de dominio en propiedad medible.
		public ElisionImpactResult ElisionImpactDiff(long[] candidateSkipEntryIds, params string[] observationQueries)
		{
			ThrowIfDisposed();
			ArgumentNullException.ThrowIfNull(candidateSkipEntryIds);
			ArgumentNullException.ThrowIfNull(observationQueries);
			if (observationQueries.Length == 0) throw new LanguageException("ElisionImpactDiff needs at least one observation query to compare.");

			long head = shadowHandler.CurrentEntryId;

			// Nombres unicos por llamada (los twins son storage propio; nombres reusados
			// arriesgarian estado residual en backends por-nombre como InMemory).
			int n = ++diffCounter;
			ActorHandler twinFull = shadowHandler.CreateShadow(new ShadowConfig(config.Id + "-s4full-" + n, config.ShadowStorageType, config.ShadowStorageConnection));
			ActorHandler twinElided = shadowHandler.CreateShadow(new ShadowConfig(config.Id + "-s4elided-" + n, config.ShadowStorageType, config.ShadowStorageConnection));
			try
			{
				twinFull.SeedElided(head, Array.Empty<long>());
				twinElided.SeedElided(head, candidateSkipEntryIds);

				List<ElisionObservationDiff> diffs = new List<ElisionObservationDiff>();
				foreach (string q in observationQueries)
				{
					if (q == null) throw new ArgumentNullException(nameof(observationQueries), "An observation query cannot be null.");
					string withoutElision = twinFull.PerformQry(q, new Parameters());
					string withElision = twinElided.PerformQry(q, new Parameters());
					if (!string.Equals(withoutElision, withElision, StringComparison.Ordinal))
						diffs.Add(new ElisionObservationDiff(q, withoutElision, withElision));
				}

				return new ElisionImpactResult(diffs);
			}
			finally
			{
				twinFull.GracefulExit();
				twinFull.TryClearShadowStorage();
				twinElided.GracefulExit();
				twinElided.TryClearShadowStorage();
			}
		}

		// Driver de comando local contra el shadow (su propio storage). Util para
		// inducir la divergencia local del experimento. Path V1 (ActorV1) — el flujo
		// async es el publico del handler. Para V2, el caller usa shadow.Actor
		// (ActorV2) y su superficie Using(...)/Dispatch.
		public string PerformCmd(string script, string ip, string user)
		{
			ThrowIfDisposed();
			if (shadowHandler.IsShadowingActive) throw new LanguageException("Cannot drive a shadow with local commands while continuous shadowing is active — continuous mirror and fork are mutually exclusive. Call StopShadowing() first.");
			ArgumentNullException.ThrowIfNull(script);
			if (ip == null) throw new ArgumentNullException(nameof(ip));
			if (user == null) throw new ArgumentNullException(nameof(user));
			return shadowHandler.PerformCmd(script, ip, user);
		}

		// Query de solo lectura contra el estado del shadow.
		public string PerformQry(string script)
		{
			ThrowIfDisposed();
			ArgumentNullException.ThrowIfNull(script);
			return shadowHandler.PerformQry(script, new Parameters());
		}

		// Shadow Replay — S2. Continuous shadowing: arranca un mirror en background que
		// sigue el head del primary en near-real-time (pull incremental de records nuevos
		// + rehidratacion). Mutuamente excluyente con el fork de SyncUntil — mientras esta
		// activo, PerformCmd local se rechaza. Parar via StopShadowing() o Dispose().
		public void StartShadowing()
		{
			ThrowIfDisposed();
			shadowHandler.StartShadowing();
		}

		// Detiene el continuous mirror (idempotente). Tras esto el shadow vuelve a
		// aceptar comandos locales (fork).
		public void StopShadowing()
		{
			ThrowIfDisposed();
			shadowHandler.StopShadowing();
		}

		public bool IsShadowing => shadowHandler.IsShadowingActive;

		// TTL kill-all (S1): graceful shutdown de las reactions del shadow y limpieza
		// del storage propio. Idempotente. El teardown por K8s Job (activeDeadlineSeconds)
		// es S6.
		public void Dispose()
		{
			if (disposed) return;
			disposed = true;

			shadowHandler.StopShadowing();
			shadowHandler.GracefulExit();
			shadowHandler.TryClearShadowStorage();
		}

		private void ThrowIfDisposed()
		{
			if (disposed) throw new LanguageException("This shadow has been disposed (TTL kill-all). Build a new one via actor.Shadow(cfg).");
		}
	}
}
