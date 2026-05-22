#!/usr/bin/env bash
# Paper 7 Phase 2 — end-to-end demo orchestrator.
#
# Drives the laptop-side CLI + two Docker containers through the full
# Usher onboarding + cross-container convergence flow. Produces the
# screencast material for the analog Usher hop video.
#
# Prerequisites:
#   - dotnet 9 SDK on the host (to publish PuppeteerHost)
#   - docker + docker compose plugin
#   - bash 4+ (run via WSL/Git Bash on Windows)
#
# Usage:
#   docker/run-demo.sh                    # run from repo root
#   docker/run-demo.sh --rehydrate-demo   # appends Video 3 scenario: stop
#                                         # ordering-b after convergence, then
#                                         # restart it and demonstrate that
#                                         # it rehydrates from its own
#                                         # on-disk journal (no Usher).
#
set -euo pipefail

# -----------------------------------------------------------------------------
# 0. Parse optional flags.
# -----------------------------------------------------------------------------

REHYDRATE_DEMO=0
PARAMETRIC=0
for arg in "$@"; do
    case "$arg" in
        --rehydrate-demo) REHYDRATE_DEMO=1 ;;
        --parametric)     PARAMETRIC=1 ;;
        --help|-h)
            cat <<EOF
Usage: $0 [--rehydrate-demo] [--parametric]

  --rehydrate-demo   After the normal 3-node convergence + issuer-kill,
                     stop ordering-b, then restart it. The restarted
                     container should detect the pre-existing journal
                     in /data, skip Usher onboarding, and rehydrate
                     its Stage from disk alone. Material for Paper 7
                     §"What the paradigm already guarantees" (Video 3).

  --parametric       Run the demo workload in PARAMETRIC regime instead
                     of inline. The Director issues one multi-statement
                     DSL script + Parameters per round (Define entry
                     once + Invocation entry per round) rather than
                     7 inline scripts. Closes the cross-container ×
                     parametric cell of the Paper 7 2×2 matrix. Final
                     journal entry across the 3 rotation rounds is 5
                     (1 bootstrap + 2 from round 1 with Define + 1
                     from each subsequent round) instead of 22.
EOF
            exit 0
            ;;
        *) echo "Unknown flag: $arg (try --help)" >&2; exit 2 ;;
    esac
done

# Export WORKLOAD_MODE so docker-compose picks it up. Compose's default
# (inline) is used when this is unset, preserving the legacy demo flow.
if [ "$PARAMETRIC" = "1" ]; then
    export PUPPETEER_WORKLOAD_MODE=parametric
fi

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOCKER_DIR="$REPO_ROOT/docker"
BUILD_DIR="$DOCKER_DIR/build/host"
SHARE_LINKS_FILE="$DOCKER_DIR/build/share-links.txt"

LOG()  { printf '\n[demo] %s\n' "$*"; }
WARN() { printf '\n[demo] WARNING: %s\n' "$*" >&2; }
FAIL() { printf '\n[demo] FATAL: %s\n' "$*" >&2; exit 1; }

# -----------------------------------------------------------------------------
# 1. Publish the host runtime artifacts (the eShop DLL still lives on Windows).
# -----------------------------------------------------------------------------

LOG "Publishing PuppeteerHost → $BUILD_DIR"
rm -rf "$BUILD_DIR"
dotnet publish "$REPO_ROOT/PuppeteerHost/PuppeteerHost.csproj" \
    -c Release -o "$BUILD_DIR" \
    --nologo --verbosity minimal

# -----------------------------------------------------------------------------
# 2. Start the host-side CLI in the background. It issues 2 invitations and
#    waits for two handshakes; we run it as a child process and capture its
#    stdout, then extract the share-links via the BEGIN/END fences.
# -----------------------------------------------------------------------------

CLI_DIR="$REPO_ROOT/PuppeteerCli/bin/Debug/net9.0"
CLI_EXE="$CLI_DIR/puppeteer"
[ -x "$CLI_EXE" ] || CLI_EXE="$CLI_DIR/puppeteer.exe"

# Always rebuild the CLI so a runtime fix landed in Choreography.csproj is
# picked up. The build is fast (incremental). Skipping the rebuild leaves
# a stale binary that silently runs an older version of the runtime —
# exactly the failure mode that masked the EnsureStartedAsync race fix
# in demo run #3.
LOG "Rebuilding puppeteer CLI"
dotnet build "$REPO_ROOT/PuppeteerCli/PuppeteerCli.csproj" \
    --nologo --verbosity minimal

mkdir -p "$DOCKER_DIR/build"
rm -f "$SHARE_LINKS_FILE"

# host.docker.internal: the magic DNS name that resolves to the host
# machine from inside a Docker container, on Docker Desktop (Windows/Mac).
# On Linux the docker-compose.yml adds an explicit extra_hosts mapping.
LOG "Starting puppeteer issue-invitation (listen on 0.0.0.0:5443, advertise host.docker.internal:5443)"
"$CLI_EXE" issue-invitation \
    --listen "https://0.0.0.0:5443/" \
    --advertise "https://host.docker.internal:5443/" \
    --count 3 --ttl-minutes 30 \
    > "$SHARE_LINKS_FILE" 2>&1 &
CLI_PID=$!

cleanup() {
    LOG "Cleaning up..."
    if kill -0 "$CLI_PID" 2>/dev/null; then
        kill "$CLI_PID" 2>/dev/null || true
    fi
    (cd "$DOCKER_DIR" && docker compose down -v 2>/dev/null) || true
}
trap cleanup EXIT

# Wait for three share-links to appear in the output.
LOG "Waiting for three share-links from the CLI..."
for try in $(seq 1 30); do
    if grep -q "BEGIN SHARE-LINK 3/3" "$SHARE_LINKS_FILE" 2>/dev/null; then
        break
    fi
    sleep 1
done
grep -q "BEGIN SHARE-LINK 3/3" "$SHARE_LINKS_FILE" 2>/dev/null || \
    FAIL "CLI did not emit three share-links in 30s. See $SHARE_LINKS_FILE"

# Extract them.
SHARE_A="$(awk '/BEGIN SHARE-LINK 1\/3/,/END SHARE-LINK 1\/3/' "$SHARE_LINKS_FILE" \
           | grep '^puppeteer-usher://')"
SHARE_B="$(awk '/BEGIN SHARE-LINK 2\/3/,/END SHARE-LINK 2\/3/' "$SHARE_LINKS_FILE" \
           | grep '^puppeteer-usher://')"
SHARE_C="$(awk '/BEGIN SHARE-LINK 3\/3/,/END SHARE-LINK 3\/3/' "$SHARE_LINKS_FILE" \
           | grep '^puppeteer-usher://')"

[ -n "$SHARE_A" ] || FAIL "Failed to parse share-link 1/3"
[ -n "$SHARE_B" ] || FAIL "Failed to parse share-link 2/3"
[ -n "$SHARE_C" ] || FAIL "Failed to parse share-link 3/3"

LOG "Captured share-link A (${#SHARE_A} chars)"
LOG "Captured share-link B (${#SHARE_B} chars)"
LOG "Captured share-link C (${#SHARE_C} chars)"

# Optionally render as QR codes for the video — if qrencode is installed.
if command -v qrencode >/dev/null 2>&1; then
    LOG "Rendering share-links as QR codes (qrencode found)"
    qrencode -o "$DOCKER_DIR/build/share-link-a.png" -s 6 -m 2 "$SHARE_A"
    qrencode -o "$DOCKER_DIR/build/share-link-b.png" -s 6 -m 2 "$SHARE_B"
    qrencode -o "$DOCKER_DIR/build/share-link-c.png" -s 6 -m 2 "$SHARE_C"
    LOG "QR codes: $DOCKER_DIR/build/share-link-{a,b,c}.png"
fi

# -----------------------------------------------------------------------------
# 3. Bring up the two Docker containers with the captured share-links.
# -----------------------------------------------------------------------------

LOG "Starting docker compose (ordering-a, ordering-b, ordering-c)"
cd "$DOCKER_DIR"
# Export the env vars so every later `docker compose ...` invocation
# (logs, ps, down) can resolve the variable references in the YAML.
export ORDERING_A_SHARE_LINK="$SHARE_A"
export ORDERING_B_SHARE_LINK="$SHARE_B"
export ORDERING_C_SHARE_LINK="$SHARE_C"
docker compose up --build -d

# -----------------------------------------------------------------------------
# 4. Tail logs until both containers report "convergence checkpoint reached".
# -----------------------------------------------------------------------------

LOG "Waiting for all three Stages to complete their rotation rounds..."
for try in $(seq 1 90); do
    A_OK=0; B_OK=0; C_OK=0
    docker compose logs ordering-a 2>&1 | grep -q "convergence checkpoint reached" && A_OK=1
    docker compose logs ordering-b 2>&1 | grep -q "convergence checkpoint reached" && B_OK=1
    docker compose logs ordering-c 2>&1 | grep -q "convergence checkpoint reached" && C_OK=1
    [ "$A_OK" = "1" ] && [ "$B_OK" = "1" ] && [ "$C_OK" = "1" ] && break
    sleep 2
done

if [ "$A_OK" != "1" ] || [ "$B_OK" != "1" ] || [ "$C_OK" != "1" ]; then
    LOG "Timed out waiting for convergence. Last 60 lines of each container:"
    docker compose logs --tail=60 ordering-a
    docker compose logs --tail=60 ordering-b
    docker compose logs --tail=60 ordering-c
    FAIL "Convergence not reached"
fi

LOG "====================================="
LOG "Paper 7 F2 demo (3-node mesh + rotation) — SUCCESS"
LOG "  ordering-a journal: convergence checkpoint reached"
LOG "  ordering-b journal: convergence checkpoint reached"
LOG "  ordering-c journal: convergence checkpoint reached"
LOG "====================================="

# -----------------------------------------------------------------------------
# 5. Kill the CLI (the analog Usher hop's "machine emitting QR" now powers off).
# -----------------------------------------------------------------------------

LOG "Stopping host-side CLI (the issuer machine 'powers off' now)"
kill "$CLI_PID" 2>/dev/null || true
wait "$CLI_PID" 2>/dev/null || true

# Containers stay up — verify they survive the issuer going away.
LOG "Issuer is gone. Checking that the containers still process commands..."
sleep 2
docker compose ps

LOG "Demo complete. To inspect:"
LOG "  docker compose logs ordering-a"
LOG "  docker compose logs ordering-b"
LOG "  docker compose down -v   # tear down"

# -----------------------------------------------------------------------------
# 6. (Optional, --rehydrate-demo) Stop one container, then restart it. The
#    restarted container's PuppeteerHost detects /data/identity.txt from the
#    previous run, skips OnboardAsync, and rehydrates its Stage from the
#    on-disk journal. Material for Paper 7 Phase 3 Video 3 — "the actor is
#    itself again, before any reconnection".
#
#    Note: the rehydration path in this commit does NOT re-establish the
#    coordination mesh or replication channels. The actor simply exists
#    again with its identity and its journal; mesh re-establishment +
#    replication catch-up are explicit future work (see
#    notes/paper7_phase3_shotlist.md §"Video 3 — what the paradigm already
#    guarantees").
# -----------------------------------------------------------------------------

if [ "$REHYDRATE_DEMO" = "1" ]; then
    LOG "====================================="
    LOG "Rehydration scenario (Paper 7 Phase 3 Video 3)"
    LOG "====================================="

    LOG "Stopping ordering-b (the node 'goes away'; journal stays on its volume)"
    docker compose stop ordering-b
    sleep 2
    LOG "Cluster state with one node down:"
    docker compose ps

    LOG "Starting ordering-b again (the node returns to its journal)"
    docker compose start ordering-b

    LOG "Waiting for ordering-b to log rehydration..."
    REHYDRATED=0
    for try in $(seq 1 30); do
        if docker compose logs --tail=30 ordering-b 2>&1 | \
           grep -q "Rehydration complete"; then
            REHYDRATED=1
            break
        fi
        sleep 1
    done

    if [ "$REHYDRATED" != "1" ]; then
        WARN "ordering-b did not log 'Rehydration complete' within 30s."
        WARN "Tail of ordering-b logs follows for diagnosis:"
        docker compose logs --tail=40 ordering-b
    else
        LOG "ordering-b rehydration log (the canonical lines):"
        docker compose logs --tail=30 ordering-b 2>&1 | \
            grep -E "(Pre-existing journal|Skipping Usher|Rehydrating Stage|Stage started|Rehydration complete)" || true

        LOG "====================================="
        LOG "Rehydration scenario — SUCCESS"
        LOG "  ordering-b returned and read its own journal."
        LOG "  No Usher contact. No new identity. No bootstrap."
        LOG "====================================="
    fi

    LOG "Final cluster state:"
    docker compose ps
fi

# Disable the trap — we want the containers to stay up so the operator can
# verify them post-issuer-shutdown (the retorical core of the analog Usher hop).
trap - EXIT
