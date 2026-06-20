using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Puppeteer;
using Puppeteer.EventSourcing.Follower;

namespace Choreography.Theater
{
    // Shadow Replay — S1 (handoff_shadow_S1_implementation.md / design §3.0).
    //
    // ShadowPerformance hospeda (por composicion) un Shadow — un actor derivado
    // laboratorio aislado de un actor de produccion. Es un tipo DELIBERADAMENTE
    // DISTINTO de Performance (NO hereda de Performance): el compilador debe impedir
    // que un shadow sustituya silenciosamente a un Performance real en cualquier API
    // que espere una Performance de produccion. Lo unico que comparte con Performance
    // es la forma de la superficie de hosting (Start / PerformCmd / PerformQry) para
    // que un pod S6 lo pueda servir, mas la superficie shadow-only (SyncUntil,
    // StartShadowing, Reactions).
    //
    // Un ShadowPerformance se obtiene de performance.Shadow(cfg). NUNCA escribe al
    // journal del primary; los Tells cross-actor de sus reactions se dropean; no se
    // registra como destination de Materialization del primary.
    public sealed class ShadowPerformance : IDisposable
    {
        private readonly Puppeteer.Shadow shadow;
        private bool started;
        private CancellationTokenSource reactionsCts;
        private readonly List<Task> reactionTasks = new List<Task>();
        private bool disposed;

        internal ShadowPerformance(Puppeteer.Shadow shadow)
        {
            this.shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        }

        // El actor shadow subyacente (familia V1/V2 igual al primary). Expuesto para
        // declarar reactions experimentales y, en V1, drivearlo directo.
        public Actor Actor => shadow.Actor;

        // Reactions del shadow — misma API de Tema A apuntando al shadow.
        public Reactions Reactions => shadow.Reactions;

        public long CurrentEntryId => shadow.CurrentEntryId;

        // Arranca las cued reactions del shadow (mismo patron que Performance.Start,
        // pero sobre el actor shadow). El storage del shadow ya quedo configurado por
        // CreateShadow, asi que Start aqui solo activa el motor de reactions push.
        public ShadowPerformance Start()
        {
            ThrowIfDisposed();
            if (started) throw new InvalidOperationException("ShadowPerformance is already started.");
            started = true;
            StartCuedReactions();
            return this;
        }

        private void StartCuedReactions()
        {
            var cuedReactions = shadow.Actor.Reactions.CuedReactions;
            bool hasCued = false;

            foreach (var reaction in cuedReactions)
            {
                if (!hasCued)
                {
                    reactionsCts = new CancellationTokenSource();
                    hasCued = true;
                }

                var ct = reactionsCts.Token;
                var task = Task.Run(() => reaction.Execute(ReactionExecutionMode.Continuous, ct));
                reactionTasks.Add(task);
            }
        }

        // SyncUntil(toEntryId): replay del journal del primary desde genesis hasta
        // toEntryId inclusive, contra el storage propio del shadow. Techo, no piso.
        // Tras esto el shadow queda forkeado y acepta comandos locales.
        public void SyncUntil(long toEntryId)
        {
            ThrowIfDisposed();
            shadow.SyncUntil(toEntryId);
        }

        // S2 — Continuous shadowing. STUB en S1.
        public void StartShadowing()
        {
            ThrowIfDisposed();
            shadow.StartShadowing();
        }

        // Driver de comando local (V1). Induce la divergencia local del experimento.
        public string PerformCmd(string script, string ip, string user)
        {
            ThrowIfDisposed();
            return shadow.PerformCmd(script, ip, user);
        }

        // Query de solo lectura sobre el estado del shadow.
        public string PerformQry(string script)
        {
            ThrowIfDisposed();
            return shadow.PerformQry(script);
        }

        // TTL kill-all (S1): detiene las cued reactions y dispone el shadow (graceful
        // shutdown + limpieza de su storage propio). Idempotente.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (reactionsCts != null)
            {
                reactionsCts.Cancel();
                if (reactionTasks.Count > 0)
                {
                    try
                    {
                        Task.WaitAll(reactionTasks.ToArray(), TimeSpan.FromSeconds(30));
                    }
                    catch (AggregateException)
                    {
                    }
                }
                reactionsCts.Dispose();
                reactionsCts = null;
            }
            reactionTasks.Clear();

            shadow.Dispose();
            started = false;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new LanguageException("This ShadowPerformance has been disposed (TTL kill-all). Build a new one via performance.Shadow(cfg).");
        }
    }
}
