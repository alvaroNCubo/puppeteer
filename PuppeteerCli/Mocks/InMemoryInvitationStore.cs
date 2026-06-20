using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Choreography.Usher;

namespace PuppeteerCli.Mocks
{
    // Paper 7 Phase 2 mock for IUsherInvitationStore.
    //
    // Per the F2 briefing the CLI process is short-lived: one run per
    // invitation, exit when the handshake completes. So persistence
    // across restarts is not required and an in-memory ConcurrentDictionary
    // is sufficient. A SQLite-backed implementation is listed in the
    // Usher handoff as a future hardening for production ContactSecret.
    public sealed class InMemoryInvitationStore : IUsherInvitationStore
    {
        private readonly ConcurrentDictionary<Guid, PendingInvitation> store = new();

        public Task SaveAsync(PendingInvitation invitation, CancellationToken ct)
        {
            store[invitation.Nonce] = invitation;
            return Task.CompletedTask;
        }

        public Task<PendingInvitation> FindByNonceAsync(Guid nonce, CancellationToken ct)
        {
            store.TryGetValue(nonce, out var pending);
            return Task.FromResult(pending);
        }

        public Task MarkConsumedAsync(Guid nonce, CancellationToken ct)
        {
            // Idempotente: el Usher.HandleSingleOnboardingAsync llama
            // pending.MarkConsumed() ANTES del MarkConsumedAsync,
            // asi que cuando llegamos aqui Status ya esta Consumed sobre la
            // misma instancia (el store guarda referencias). Skip silencioso
            if (store.TryGetValue(nonce, out var pending)
                && pending.Status == PendingInvitationStatus.Pending)
                pending.MarkConsumed();
            return Task.CompletedTask;
        }

        public Task MarkRejectedAsync(Guid nonce, string reason, CancellationToken ct)
        {
            if (store.TryGetValue(nonce, out var pending))
                pending.MarkRejected(reason);
            return Task.CompletedTask;
        }
    }
}
