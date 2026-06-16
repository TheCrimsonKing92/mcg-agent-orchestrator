#!/usr/bin/env bash
# Cross-platform launcher mirroring mcg-orchestrator.cmd. Separates build from run so
# concurrent invocations never compile simultaneously (avoids CS2012 bin/obj contention).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
export MCG_ORCHESTRATOR_REPOSITORY_ROOT="$ROOT/"

APP_PROJECT="$ROOT/src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj"
APP_DLL="$ROOT/src/Mcg.AgentOrchestrator.App/bin/Debug/net10.0/Mcg.AgentOrchestrator.App.dll"
LOCK_DIR="$ROOT/.build-lock"

# Reclaim a dead-owner or age-stale lock left by a prior crashed invocation.
# If owner.pid exists and the owner process is alive, do NOT reclaim.
# If owner.pid is missing or unreadable, fall back to a generous 300-s age threshold.
if [ -d "$LOCK_DIR" ]; then
    PID_FILE="$LOCK_DIR/owner.pid"
    reclaim=0
    if [ -f "$PID_FILE" ] && owner_pid=$(cat "$PID_FILE" 2>/dev/null) && [ -n "$owner_pid" ]; then
        if ! kill -0 "$owner_pid" 2>/dev/null; then
            reclaim=1
        fi
    else
        lock_mtime=$(stat -c %Y "$LOCK_DIR" 2>/dev/null || stat -f %m "$LOCK_DIR" 2>/dev/null || echo 9999999999)
        now=$(date +%s)
        if [ $((now - lock_mtime)) -gt 300 ]; then
            reclaim=1
        fi
    fi
    if [ "$reclaim" -eq 1 ]; then
        rm -rf "$LOCK_DIR" 2>/dev/null || true
    fi
fi

# Acquire build lock -- mkdir is atomic on POSIX; spin up to 30 s
LOCK_TRIES=0
while ! mkdir "$LOCK_DIR" 2>/dev/null; do
    LOCK_TRIES=$((LOCK_TRIES + 1))
    if [ "$LOCK_TRIES" -ge 30 ]; then
        echo "ERROR: could not acquire build lock after 30 s" >&2
        exit 1
    fi
    sleep 1
done
# Record our PID so concurrent invocations can test our liveness before reclaiming.
echo $$ > "$LOCK_DIR/owner.pid"
trap 'rm -rf "$LOCK_DIR" 2>/dev/null || true' EXIT

dotnet build "$APP_PROJECT" --nologo -v q >/dev/null 2>&1

rm -rf "$LOCK_DIR" 2>/dev/null || true
trap - EXIT

exec dotnet "$APP_DLL" "$@"
