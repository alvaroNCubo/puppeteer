# Paper 7 Phase 2 — Cross-container existence proof

Two Docker containers + one host-side CLI demonstrate the Paper 7 thesis
operationally:

> Under journaled programs, server ceases to be a prerequisite for making
> software. Bootstrap — the last role that seemed to need a service —
> can be performed by a printed QR code.

## What this directory contains

| File | Purpose |
|---|---|
| `Dockerfile.host` | Runtime image for `PuppeteerHost`. Copies pre-published artifacts; does not compile inside Docker. |
| `docker-compose.yml` | Two services (`ordering-a`, `ordering-b`) on a Docker bridge network, with a shared `bootstrap` volume for peer rendezvous. |
| `run-demo.sh` | End-to-end orchestrator: publish host, start CLI, capture share-links, bring up compose, wait for convergence. |
| `build/` | Generated. `host/` holds `dotnet publish` artifacts; `share-links.txt` captures CLI stdout; `share-link-{a,b}.png` are optional QR renderings. |

## Topology

```
                 +-----------------------+
                 |   Operator's laptop   |
                 |  (Usher: puppeteer    |
                 |   issue-invitation)   |
                 |   port 5443 TLS       |
                 +-----------+-----------+
                             |
                             | (1) F1-F5 Usher handshake
                             | over real TLS, both directions
                             |
            +----------------+----------------+
            |                                 |
  +---------v---------+               +-------v---------+
  |    ordering-a     |               |    ordering-b   |
  |  (Director,       |               |  (Cast)         |
  |   inviter role)   |  (2) peer     |                 |
  |  port 5443 TLS    +---------------+  port 5443 TLS  |
  +-------------------+   coord+      +-----------------+
            ^                replication+command
            |                channels over TLS
            |
    (3) bootstrap volume  (shared file rendezvous for
            +--------+    the three peer ConnectionInvitations)
            |
            (analog hop: the Address strings travel between
            the containers via a shared volume the operator
            placed there. The wire is the filesystem, not a
            running service.)
```

## End-to-end flow (single-machine demo)

1. Operator publishes `PuppeteerHost` on the laptop:
   ```
   dotnet publish PuppeteerHost/PuppeteerHost.csproj -c Release \
                  -o docker/build/host
   ```
2. Operator starts the CLI:
   ```
   ./PuppeteerCli/bin/Debug/net9.0/puppeteer issue-invitation \
       --listen    https://0.0.0.0:5443/ \
       --advertise https://host.docker.internal:5443/ \
       --count 2 --ttl-minutes 30
   ```
3. The CLI emits TWO share-links to stdout in `BEGIN/END SHARE-LINK i/2`
   fences. The operator can render them as QR codes (`qrencode`), print
   them on paper, photograph them, dictate them — anything that gets
   ASCII to the other side. **No new wire is opened by the act of
   showing a QR.**
4. Operator exports the two share-links and brings up compose:
   ```
   export ORDERING_A_SHARE_LINK="puppeteer-usher://v1/...A..."
   export ORDERING_B_SHARE_LINK="puppeteer-usher://v1/...B..."
   cd docker && docker compose up --build
   ```
5. Each container completes the F1-F5 Usher handshake with the CLI
   (real Ed25519 signature, real X25519 + ChaCha20-Poly1305 sealed box,
   real TLS pinned by SHA-256 fingerprint).
6. The two containers then exchange three `ConnectionInvitation`s
   (coordination, replication, command) through the shared `bootstrap`
   volume — the in-cluster analog of the QR-on-paper hop.
7. `ordering-a` promotes to Director and issues a small demo workload
   against eShop's `Ordering.Domain` aggregate; `ordering-b` replicates
   and logs `convergence checkpoint reached`.
8. The CLI exits (the "issuer laptop powers off"). The two containers
   keep operating — the journal on disk is the state.

`docker/run-demo.sh` automates all of the above for a screencast or CI
loop.

## Cross-platform notes

| Platform | Notes |
|---|---|
| Docker Desktop (Windows / macOS) | `host.docker.internal` resolves to the host inside any container. The compose file sets `extra_hosts: host-gateway` for parity with Linux. |
| Docker Engine on Linux | `extra_hosts: "host.docker.internal:host-gateway"` works on docker 20.10+. |
| Without Docker Desktop's magic DNS | Substitute the laptop's LAN IP in `--advertise https://<ip>:5443/` and remove `extra_hosts`. |

## Honest limitations (declared up front in Paper 7 §7)

- **Membership** is local to each Stage. The Usher writes to a mock
  `IJournalWriter` (`QueueingJournalWriter` in `PuppeteerCli/`); a real
  RSM-anchored membership journal (handoff item #3) is out of scope
  for Phase 2.
- **Peer rendezvous** between the two Docker containers uses a shared
  filesystem volume rather than journal-replicated `PeerInvitationRecord`s
  (Phase 6 / handoff item #7). The retorical claim of the paper is that
  the rendezvous *can* be analog; F2 demonstrates one such analog
  channel (filesystem).
- **Operator approval** of each handshake is auto-approving
  (`AutoApprovingApprovalQueue` in `PuppeteerCli/Mocks/`). Production
  ContactSecret would present a UI; Phase 2's claim is about topology, not
  the human-in-the-loop.
- The **2×2 matrix** of regimes (inline / parametric × in-process /
  cross-container) is closed for the in-process row by F1. Phase 2 closes
  the cross-container row with the demo above and the metrics in
  `notes/paper7_phase2_results.md`.
