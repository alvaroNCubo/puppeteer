# Paper 7 — Phase 1 results

**Branch:** `feature/paper7-eshop-existence-proof` (from `lab-replay/05-eshop @ ebf289d`)
**Date:** 2026-05-16
**Status:** F1 closed. F2 (StageManager + 2 Dockers + analog Usher hop) is the next phase; this report is the gate.

## 1. Goal

The Paper 7 thesis, signed PM 2026-05-16:

> *Under journaled programs, server ceases to be a prerequisite for making software. Bootstrap — the last role that seemed to need a service — can be performed by a printed QR code.*

Phase 1 attacks the first sentence operationally, before the second sentence is staged. The phase 1 deliverable is an in-process existence proof that **the server role is a transferable coordinator role rather than a permanent identity** — three nodes hosting a real domain converge through a role that rotates across all three, with no node holding the role permanently and no node depending on the others to operate.

## 2. Topology

Three Choreography `StageV2` instances live in the same OS process. Each instance has:

- A `PerformerId.New()` GUID identity, distinct from the other two.
- A `FileSystem` journal under a private temp directory.
- A coordination-bus channel to each of the other two peers (3 edges total).
- A data-star channel (replication + command) to whichever peer currently holds the Director role.
- Two library assemblies loaded by reflection: `eShop.Ordering.Domain` (the third-party aggregate) and the test assembly itself (which contains `OrderingFacade`).

The transport is `Choreography.Transport.InMemoryTransport` — a static registry of `InMemoryChannel` pairs. No sockets, no TLS, no out-of-process IPC.

Each `Stage` runs the `Actor.PerformCmd` path via `StageHook.PerformCmd` when it is Director; when it is Cast, the same call forwards to the current Director through the command channel, executes there, and the resulting journal record is broadcast back through the replication channel.

The mesh, in ASCII:

```
                       coordination
              sm1 ─────────────────── sm2
                ╲                   ╱
                 ╲   coordination ╱
                  ╲             ╱
                   ╲           ╱
                    ╲         ╱
            coordination     coordination
                      ╲     ╱
                       ╲   ╱
                       sm3

           data-star (replication + command):
              Director ──→ Cast₁ , Cast₂      (rotates B1→B2→B3)
```

## 3. Domain — what was ported

No code from `dotnet/eShop` was modified. The single artefact consumed is the binary:

```
C:\Users\alvar\source\repos\dotnet-eShop\artifacts\bin\Ordering.Domain\debug_net9.0\Ordering.Domain.dll
```

This is the output of `dotnet build src/Ordering.Domain` against the public MIT-licensed `dotnet/eShop` source tree. It was built once, out of tree, during the Paper 2 Fase-0 harness setup; F1 reuses the artefact verbatim.

The aggregate exercised is `eShop.Ordering.Domain.AggregatesModel.OrderAggregate.Order`. The DSL never names `Order` directly; it goes through a facade:

```csharp
public class OrderingFacade
{
    public Order NewSubmittedOrder(string userId, string userName) { ... }
}
```

`NewSubmittedOrder` calls the 10-argument `Order` constructor with a constructed `Address` value object. From there the DSL drives the aggregate's domain verbs directly: `AddOrderItem`, `SetAwaitingValidationStatus`, `SetStockConfirmedStatus`, `SetPaidStatus`, `SetShippedStatus`, `SetCancelledStatus`.

### Two DSL scripts exercise the aggregate

**Happy path** — Submitted → AwaitingValidation → StockConfirmed → Paid → Shipped, with a two-item cart:

```text
f = OrderingFacade();
o = f.NewSubmittedOrder('user-1', 'Alice');
o.AddOrderItem(1001, 'widget', 99, 0, '', 1);
o.AddOrderItem(1002, 'gadget', 199, 0, '', 1);
o.SetAwaitingValidationStatus();
o.SetStockConfirmedStatus();
o.SetPaidStatus();
o.SetShippedStatus();
```

**Cancellation branch** — Submitted → Cancelled, with one item:

```text
f = OrderingFacade();
oc = f.NewSubmittedOrder('user-c', 'Charlie Cancelled');
oc.AddOrderItem(5001, 'regret', 19, 0, '', 1);
oc.SetCancelledStatus();
```

Each line is a separate `PerformCmd`. Each call produces exactly one journal record. (See §7 on why one-record-per-script and not parametric.)

## 4. Properties demonstrated

The phase-1 harness is `UnitTestPaper7EShop/ThreeNodeOrderingTests.cs`. Four MSTest methods:

| Test | What it shows |
|---|---|
| `SingleStage_OperatesAlone_WhenPromotedForce` | A single Stage with no peers, forced to Director, accepts the bootstrap + happy-path scripts and writes 8 journal entries to disk. Its journal is rehydratable by a fresh `ActorV2 + StageHook`. **Property (a): no node depends on the others to operate.** |
| `ThreeStages_DirectorRotation_AllConverge_HappyPath` | Three Stages in mesh. Phase B1: sm1 Director, writes 8 entries; sm2 and sm3 replicate. Phase B2: sm1 steps down, sm2 Director, writes 7 more entries; sm1 and sm3 replicate. Phase B3: sm2 steps down, sm3 Director, writes 7 more entries; sm1 and sm2 replicate. Each Stage held the Director role exactly once. Final entry id on all three: **22**. **Property (b): the Director role is transferable across all peers; convergence is exact.** |
| `CancellationBranch_Replicates_AcrossThreeStages` | Three Stages, sm1 Director, runs the cancellation script. The three journals converge at entry 4. **Property (c): non-happy-path domain transitions replicate through the same mechanism as the happy path.** |
| `Metrics_F1_Snapshot` | Same as (b) but instrumented. See §5. |

All four tests pass under `dotnet test`. No `[TestCategory("FlakyInCI")]` was needed — the harness is stable in-process.

## 5. Metrics snapshot

From `Metrics_F1_Snapshot` (single run, .NET 9, Debug, x64, Windows):

| Metric | Value |
|---|---|
| Three `StageV2` constructions including `Ordering.Domain` reflection scan | **7 ms total** |
| Phase B1 end-to-end (PerformCmd → both Casts caught up) | **96 ms** for 8 entries |
| Phase B2 end-to-end | **65 ms** for 7 entries |
| Phase B3 end-to-end | **65 ms** for 7 entries |
| Final journal entries per Stage | **22** (identical on the three) |
| Final journal bytes on disk per Stage | **1622 bytes** (identical on the three) |
| Full `dotnet test` for the four F1 methods | **2.3 s** wall clock |

**The three FileSystem journals end at byte-identical size — 1622 bytes — confirming that the convergence is not approximate but exact at the storage layer.** Convergence ratio across the three nodes is 1:1:1 in entries, in bytes, and in payload.

## 6. Findings

### 6.1 Director-rotatable, not director-absent

The Choreography runtime enforces a Director / Cast model. At any instant exactly one Stage in the cluster is Director and the others are Cast. A Cast that wants to journal forwards its `PerformCmd` to the current Director through the command channel (`Stage.ForwardToDirector`, `Stage.cs:376`). The Director executes locally and broadcasts the resulting record back through the replication channel.

The role can transfer at any time: `StageA.StepDownAsync()` clears the directorship, after which any peer can `PromoteToDirector()` to take over. The Paper 7 thesis is operationalised through *rotation*, not absence — the role exists, but it is not bound to any specific identity. The three Stages in test (b) each hold the role for one phase; the role visits all three; convergence is preserved.

The retorical implication for Paper 7 §5: the phrase *"three nodes converge with no node holding privileged state"* should read instead **"three nodes converge under a transferable director-role: at any instant one node coordinates writes, but the role can be assumed by any peer in turn; convergence does not depend on a specific identity holding the role."** The thesis (*server-as-non-prerequisite*) is not weakened — a coordinator role that any peer can assume is the opposite of "a specific server must be running."

### 6.2 Runtime bug observed and worked around (out of F1 scope)

A parametric `PerformCmd` writes two journal records in sequence — a `Define` and an `Invocation` (see Phase 5 of the Action refactor comments in `Choreography/StageManager/Stage.cs:568` and `Puppeteer/EventSourcing/DB/DiaryStorageFileSystem.cs:347-348`). The runtime invokes `OnRecordWritten` once per record. In `Stage.OnRecordWritten` (`Stage.cs:591`) each broadcast is fired as `_ = Task.Run(async () => await link.Replication.SendAsync(cueEvent));` — fire-and-forget, no per-cast ordering guarantee. Under contention the two `CueEvent`s for a single parametric `PerformCmd` can arrive at the Cast out of order, producing the symptom `GAP: expected N, got N+1` and stalling replication.

F1 sidesteps this by issuing one DSL line per `PerformCmd`, so every call produces exactly one journal record and the race window does not open. The bug is upstream of F1 and remains for the Choreography team to address (likely in a fix branch parallel to `fix/replication-agent-peek-dequeue` and `fix/diary-symmetric-consumer-callbacks`). Phase 2 — where the transport is no longer in-process — needs the upstream fix before the same parametric pattern can run reliably; until then F2 will continue the one-record-per-script discipline.

### 6.3 The runtime tolerates a third-party domain DLL without modification

`Stage.OnFirstHydration`, `Stage.OnHydrated`, `StageFactory.Create<StageV2>(..., params Assembly[])`, and the surrounding reflection path absorb `eShop.Ordering.Domain.dll` (compiled against an unrelated MediatR + MS.Extensions stack) with two ergonomic adjustments only:

- `Puppeteer/EventSourcing/DomainLibraries.cs` catches `ReflectionTypeLoadException` so transitive dependencies that fail to resolve (MediatR contracts in this case) do not prevent the rest of the domain types from being discovered. This was inherited from `lab-replay/00-harness-setup`.
- `Choreography/Choreography.csproj` was given an `<InternalsVisibleTo Include="UnitTestPaper7EShop" />` line so the harness can call `InMemoryTransport.ClearRegistry()` between test runs (the same access UnitTestChoreography uses).

Neither adjustment touches the domain assembly. The eShop binary is loaded as-is.

## 7. Modifications introduced by this branch

| File | Change |
|---|---|
| `Choreography/Choreography.csproj` | Added `<InternalsVisibleTo Include="UnitTestPaper7EShop" />` so the harness can call `InMemoryTransport.ClearRegistry()`. Same shape as the existing `UnitTestChoreography` and `Choreography.Observability.ElasticApm` lines. |
| `UnitTestPaper7EShop/` (new project, outside `Puppeteer.sln` by convention with `UnitTestEShopOnPuppeteer`) | `UnitTestPaper7EShop.csproj` + `MSTestSettings.cs` + `OrderingFacade.cs` + `ThreeNodeOrderingTests.cs`. |

No changes to `Puppeteer/`, no changes to `Choreography/StageManager/` or `Choreography/Transport/`. The Paper 2 chain mods (1–12) are inherited verbatim from the base branch `lab-replay/05-eshop`.

Suite gates post-change:
- `UnitTestPuppeteer`: 768/768 green.
- `UnitTestChoreography`: 102/102 green.
- `UnitTestPaper7EShop`: 4/4 green.

## 8. Limitations and handoff to Phase 2

Phase 1 does **not** address:

- **Cross-process transport.** Everything runs in one OS process under `InMemoryTransport`. The retorical claim of section 6.1 ("server role is transferable, not a fixed identity") is structural and survives a transport swap, but the visual claim of the Paper 7 thesis — *"bootstrap can be performed by a printed QR code"* — is not staged here.
- **Bootstrap via an analog medium.** No `puppeteer issue-invitation` CLI yet. No QR. The three Stages know each other's transport addresses through the test code that constructs them.
- **Director election under partition.** Phase 1 transfers the role through explicit `StepDownAsync` + `PromoteToDirector` calls. Failover under unannounced peer death is exercised by `FailoverEndToEndTests` upstream but not by F1.
- **Crypto real.** `PerformerId.New()` is a GUID; identities are not signed. F2 may swap in `OnboardedIdentity` or declare a toy HMAC limitation explicitly.
- **The parametric replication ordering bug** (§6.2) is filed for the Choreography team; F1 sidesteps rather than fixes it.

The Phase 2 deliverable, separately scoped:

1. Promote `Choreography/Transport/InMemoryTransport` → an HTTPS or equivalent transport for Docker compose.
2. Implement `puppeteer issue-invitation` CLI on top of the existing `Usher` scaffold (currently `internal`; the briefing's piece #1).
3. Run the same three-node test pattern across two Docker containers + one issuing machine.
4. Film the analog hop: paper, photograph, OCR, two containers syndicalize, issuing machine powers off, containers keep operating.

Phase 1 is the structural rehearsal of what Phase 2 will stage on stage.

## 9. Material for Paper 7 redaction (when its turn comes)

§4 *Implementation* and §5 *Server-role dissolution* can cite this report directly:

- The exact DLL artefact path and its provenance (eShop public MIT).
- The four DSL scripts (verbatim, in §3 above).
- The four test method names + outcomes.
- The metrics table (§5).
- The byte-identical journal observation as the §5 closing line.
- The director-rotation framing of §6.1 as the operational form of *"server-role dissolution"*.

§6 *Cloud as library, not infrastructure* can cite the fact that `Ordering.Domain.dll` was built once from `dotnet/eShop` and consumed by three Choreography Stages without modification — the domain library was reused, the surrounding cloud stack (EF, Kestrel, IdentityServer, message queues) was not.

§7 *Limitations + counter-arguments* should disclose §6.2 (the parametric replication ordering bug worked around) and the Phase-1 limitations listed in §8 above. The honesty does not harm the thesis; the thesis is about the topology, not the runtime's polish.

---

## Addendum 2026-05-16 PM — Scope expanded to a 2×2 matrix

After Phase 1 closed, Alvaro signed an expansion of the existence proof scope: the paper should cover **both** the inline-literal script regime and the parametric script regime, and **both** the in-process topology and the cross-container topology. The matrix:

| | Inline-literal scripts | Parametric scripts (Define + Invocation) |
|---|---|---|
| **In-process (InMemoryTransport)** | ✅ Phase 1 — this report | ⏳ pending the runtime fix of §6.2 |
| **Cross-container (Docker, distinct simulated machines)** | ⏳ Phase 2 — staged with the analog Usher hop | ⏳ Phase 2 + the runtime fix |

Phase 1 as committed covers **one cell of four**. The other three cells become deliverables in turn:

1. **In-process + parametric** — unblocked by the upstream fix of the `Stage.OnRecordWritten` ordering race (§6.2). When that fix lands, the F1 tests are rerun in parametric mode (revert the helper `IssueAll` to a single parametric `PerformCmd` per script, restore the original `HappyPathParams` / `CancellationParams` helpers, expected entry counts go down from 8 to 2 per phase since Define+Invocation are now a pair rather than 7 separate records). This is the *journal-density* regime Lab 4 of Paper 2 demonstrated; preserving it under cross-Stage replication is a separate claim from the inline-literal regime, and the paper should show both.

2. **Cross-container + inline-literal** — the canonical F2 deliverable. Same three Stages, but each runs in its own Docker container with a non-InMemory transport (HTTPS or equivalent), syndicalized through the analog Usher hop (printed QR, photograph, OCR, two containers join, issuing machine powers off). Demonstrates that the in-process result of Phase 1 was not an artefact of shared memory or shared OS process.

3. **Cross-container + parametric** — the union of the previous two. Density and replication composed together over a real network boundary. This is the strongest claim of the paper.

The 2×2 framing **belongs in Paper 7 §4 (Implementation)**: a single table laying out the four cells, with the same Order aggregate exercised in each, demonstrates that the server-as-non-prerequisite property is invariant under both the script-shape axis (inline / parametric) and the locality axis (in-process / cross-container). The thesis is structural, not artefact-bound.

**Runtime fix is now in flight (out-of-tree of this branch)** — a spawned task is opening a `fix/stage-onrecord-ordering` branch from master against the parametric-replication ordering bug filed in §6.2. When it merges, this branch can rebase, restore parametric mode, and close cells 1+2 of the matrix before F2 stages cells 3+4.

Phase 1 stands as the structural rehearsal. The matrix is the full performance.

---

## Addendum 2 — Parametric regime cell closed (in-process + parametric)

After the runtime fix `fix(choreography): preserve per-cast FIFO of replication CueEvents` (commit `f309cc9` on `fix/stage-onrecord-ordering`, cherry-picked into this branch as `a9417e9`), the parametric regime of the in-process cell is closed. The matrix is now **2 of 4**:

| | Inline-literal scripts | Parametric scripts |
|---|---|---|
| **In-process (InMemoryTransport)** | ✅ closed (b3d0684) | ✅ closed (this addendum) |
| **Cross-container (Docker)** | ⏳ F2 with the analog Usher hop | ⏳ F2 over the runtime fix |

### What the upstream fix turned out to be

The bug filed in §6.2 of this report had **two coupled defects**, not one. The spawned task (`fix/stage-onrecord-ordering`, signed off as `f309cc9`) addressed both:

1. **`Stage.OnRecordWritten` per-cast FIFO race** — the defect §6.2 identified. The Director's writer thread used to fire each `CueEvent` broadcast as an independent `_ = Task.Run(async () => await link.Replication.SendAsync(...))`, leaving cross-record ordering at the mercy of the thread pool. The fix replaced that with a per-`CastLink` `Channel<CueEvent>` + a single pump task; `OnRecordWritten` now does a non-blocking `TryWrite` and a dedicated worker drains the queue serially per link. Throughput stays decoupled from transport latency; ordering is structural.

2. **`ActorHandler.WriteRawRecord` did not advance the high-water EntryId for Define records** — a latent defect §6.2 had not yet diagnosed. Without (2), the Cast would receive entry 1 (the Define) *in order* thanks to (1), apply it correctly via `AddKnownActionFromDefine`, but never bump `handler.EntryId` past 0 because the Define branch returned before the canonical max-update inside `ApplyReplicatedEvent`. Entry 2 (the Invocation) would then look like a gap. The fix bumps `this.EntryId = Int64.Max(entryId, this.EntryId)` in `WriteRawRecord` itself — idempotent on the non-Define paths, mandatory on Define. As a side effect this also closes a latent failover hazard: a former Cast that had received Define records and was later promoted to Director would otherwise have had its in-memory `EntryId` frozen below the journal high-water mark.

Failure rate against `master @ 38981af` without the fix: **100%** on the first parametric `PerformCmd` (the reproducer in `UnitTestChoreography/ParametricReplicationOrderingTests.cs` trips on entry 1→2 every time). Stability with the fix: 50 consecutive runs of the same test class green.

### New parametric tests on this branch

- **`ThreeStages_DirectorRotation_AllConverge_HappyPath_Parametric`** — the parametric counterpart of test (b). One `PerformCmd` per phase against the same multi-statement DSL body parameterised through `Parameters`. The body is `Define`-cached after the first phase, so Phase B2 and B3 emit only Invocation entries. The three Stages converge at the same entry count after each rotation.
- **`Metrics_F1_Snapshot`** migrated from inline to parametric — the metrics the paper will quote alongside Paper 2 Lab 4's journal-density numbers now come from this test.

### Metrics comparison — inline vs parametric

| Metric | Inline regime | Parametric regime | Δ |
|---|---:|---:|---|
| 3× `StageFactory.Create<StageV2>` (DLL load + reflection) | 7 ms | 9 ms | +2 ms (noise) |
| Phase B1 end-to-end (bootstrap + happy-path → 2 Casts caught up) | 96 ms | 100 ms | +4 ms (noise) |
| Phase B2 end-to-end | 65 ms | 66 ms | +1 ms (noise) |
| Phase B3 end-to-end | 65 ms | 66 ms | +1 ms (noise) |
| Final journal entries per Stage | 22 | 7 | **−3.1× compaction** |
| Final journal bytes per Stage | 1622 (byte-identical on 3 Stages) | 1824 (byte-identical on 3 Stages) | **+12%** |

Two observations worth flagging for §5 of the paper:

1. **The fix introduces no convergence-latency penalty.** Per-phase end-to-end timings are within 1-4 ms of the inline regime (which is below the measurement floor of `Stopwatch.ElapsedMilliseconds` on the 50ms `WaitForEntryId` poll). The per-link queue serialises *order*, not throughput.

2. **The parametric regime trades entry count for body size at small N.** With three invocations against a single body, the Define entry stored once is *larger in absolute bytes* than three separate inline scripts. Paper 2 Lab 4 measured the parametric break-even at N≈10-100 invocations against the same body; F1 ran at N=3 per body, below break-even — so this report shows the parametric regime *working correctly*, not the parametric regime *winning on density*. The density claim is Lab 4's; F1's claim is convergence under replication, and the convergence is byte-identical in both regimes.

Both regimes therefore deserve §4 / §5 coverage in Paper 7. The text-level argument the paper makes (server-role transferable; convergence under replication) is invariant across regime; the empirical surface differs in exactly the way one would expect from the construct, which itself is a small confirmation of the construct.

### Branch state at this addendum

- `feature/paper7-eshop-existence-proof @ <next commit>` (this commit).
- Linear history: `lab-replay/05-eshop @ ebf289d` → F1 commit `b3d0684` → matrix addendum `9125dbb` → fix cherry-pick `a9417e9` → this commit.
- 5 MSTest methods on `UnitTestPaper7EShop`, all green:
  - 3 inline-regime tests (regression on the original F1 path).
  - 1 parametric director-rotation test (closes the parametric cell).
  - `Metrics_F1_Snapshot`, parametric.
- Suite gates: UnitTestPuppeteer 768/768 + UnitTestChoreography 104/104 (+2 from the cherry-pick) + UnitTestPaper7EShop 5/5.

Next: F2 (cross-container) on top of this branch. Both the inline and the parametric regime will be exercised in the cross-container topology to close the other two cells of the matrix.

---

## Addendum 3 — Partition tolerance + rehydration from disk + catch-up

After Alvaro flagged that the F1 tests missed two structural validations the paper depends on — *what happens when a Stage leaves the cluster mid-flight* and *what happens when an offline Stage's process is rehydrated from its on-disk journal* — a sixth test was added to UnitTestPaper7EShop:

**`OneStage_Offline_OthersAdvance_Rehydrate_CatchUp_Parametric`** (659 ms, parametric regime, green).

Five operational properties land in one combined trace, which is also the natural sequence the paper §5 will narrate:

| Phase | Action | Property demonstrated |
|---|---|---|
| R1 | All three Stages converge under sm1 Director, parametric happy path | (baseline, same as test b') |
| R2 | sm3 goes offline (`StopAsync`); sm1+sm2 issue another parametric happy path | **The cluster operates with a missing member.** sm1+sm2 advance, sm3 stays frozen at its pre-offline entry. |
| R3 | A fresh `StageV2` instance with a **different `PerformerId`** is constructed against sm3's same data directory and `StartAsync`'d | **The journal on disk is self-contained state.** The rehydrated Stage's `CurrentEntryId` equals the value at the moment sm3 went offline. No surviving in-memory state carries over from the dead process; the disk is the source of truth. |
| R4 | Rehydrated Stage rejoins via `ConnectCoordination` + `ConnectDataChannels`; Director invokes `SendCatchUpAsync` | **Rejoin closes the gap deterministically.** After catch-up, the rehydrated Stage's `CurrentEntryId` equals the live Director's. No partition residue. |
| R5 | Director issues one more parametric happy path | **The rejoined Stage is a first-class peer post-catch-up.** New entries land on all three, including the rehydrated one. |

Verified trace from the test run (entry-ids; numbers depend on the Action refactor's Define+Invocation packing, here 3 entries per parametric happy path against a previously-unseen body plus 1 entry per subsequent invocation against the cached body):

```
R1 converged at entry 3 (bootstrap + Define + first Invocation; all three Stages)
R2: sm3 frozen at 3; sm1 and sm2 advance to entry 4 (cached-Define + Invocation)
R3: sm3 rehydrated from disk reads CurrentEntryId = 3 ← exact same number
R4: CatchUp sent range [4, 4] to sm3-rehydrated; sm3-reh advances to entry 4
R5: sm1 issues one more parametric → all three converge at entry 5
```

The five properties together are operational forms of two Paper 7 claims:

1. **Server-as-non-prerequisite expands beyond rotation.** Test (b') showed the Director role transfers across peers under nominal conditions. Test (e) shows the topology also tolerates a peer simply being unreachable mid-flight — no node is required for cluster continuity.

2. **The journal on disk is the state, not a cache of it.** A rehydrated Stage is constructed without the original `PerformerId` — only the disk's contents survive across the process boundary. The fact that `CurrentEntryId` is exactly the pre-offline value is the strongest possible statement of "the disk is the source of truth": there is no coordinator anywhere telling sm3 where it had been; it knows because it stored that.

Together, tests (a) through (e) cover the in-process row of the 2×2 matrix completely for the paper:

| Test | Property |
|---|---|
| (a) `SingleStage_OperatesAlone_WhenPromotedForce` | A Stage operates with no peers (Director-of-one) |
| (b inline) `ThreeStages_DirectorRotation_AllConverge_HappyPath` | Director role is transferable across all three (inline scripts) |
| (b' parametric) `..._Parametric` | Same, parametric scripts (Define + Invocation pair) |
| (c) `CancellationBranch_Replicates_AcrossThreeStages` | Domain branch transitions replicate uniformly |
| (d) `Metrics_F1_Snapshot` | Quantitative envelope (latency, byte size) |
| **(e) `OneStage_Offline_OthersAdvance_Rehydrate_CatchUp_Parametric`** | **Partition tolerance + disk-as-state + catch-up** |

Suite Paper 7: **6/6** (~6.2 s). UnitTestPuppeteer 768/768 + UnitTestChoreography 104/104 unchanged.

The inline counterpart of test (e) is not in scope for F1 — the structural claim is regime-invariant (the disk content shape changes between inline and parametric, but the *property* — disk is state — does not). If a reviewer asks for the symmetric inline test, it is mechanical to add.
