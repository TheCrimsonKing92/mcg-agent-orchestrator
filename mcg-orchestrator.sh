#!/usr/bin/env bash
# Cross-platform launcher mirroring mcg-orchestrator.cmd. Separates build from run so
# concurrent invocations never compile simultaneously (avoids CS2012 bin/obj contention).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
export MCG_ORCHESTRATOR_REPOSITORY_ROOT="$ROOT/"

# Intentionally inherited by the entire launcher process tree (CLI, builds, gates, and their children) to
# limit aggregate memory pressure on many-core hosts. The CLI's Workstation/non-concurrent GC mode is scoped
# to its runtimeconfig by Mcg.AgentOrchestrator.App.csproj, and MSBuild node reuse is disabled repo-wide by
# Directory.Build.rsp; do not export those settings here because unrelated descendants own their runtimes.
export DOTNET_GCConserveMemory=7

APP_PROJECT="$ROOT/src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj"
APP_DLL="$ROOT/src/Mcg.AgentOrchestrator.App/bin/Debug/net10.0/Mcg.AgentOrchestrator.App.dll"
LOCK_DIR="$ROOT/.build-lock"
LOCK_STALE_SECONDS=60

# Reclaim a dead-owner or age-stale lock left by a prior crashed invocation.
# A lock older than LOCK_STALE_SECONDS is stale even if owner.pid is present.
# A younger lock is also reclaimed when owner.pid is missing after a short recheck or its owner is gone.
if [ -d "$LOCK_DIR" ]; then
    PID_FILE="$LOCK_DIR/owner.pid"
    reclaim=0
    lock_mtime=$(stat -c %Y "$LOCK_DIR" 2>/dev/null || stat -f %m "$LOCK_DIR" 2>/dev/null || echo 0)
    now=$(date +%s)
    if [ $((now - lock_mtime)) -gt "$LOCK_STALE_SECONDS" ]; then
        reclaim=1
    fi
    if [ ! -f "$PID_FILE" ] && [ "$reclaim" -eq 0 ]; then
        sleep 0.5
    fi
    if [ -f "$PID_FILE" ] && owner_pid=$(cat "$PID_FILE" 2>/dev/null) && [ -n "$owner_pid" ]; then
        if [ "$reclaim" -eq 0 ] && ! kill -0 "$owner_pid" 2>/dev/null; then
            reclaim=1
        fi
    else
        reclaim=1
    fi
    if [ "$reclaim" -eq 1 ]; then
        rm -rf "$LOCK_DIR" 2>/dev/null || true
    fi
fi

# Up-to-date check -- if App.dll exists and is newer than all source files, skip build entirely.
if [ -f "$APP_DLL" ]; then
    stale=$(find "$ROOT/src" \( -name "*.cs" -o -name "*.csproj" -o -name "*.props" \) -newer "$APP_DLL" -print -quit 2>/dev/null)
    if [ -z "$stale" ]; then
        exec dotnet "$APP_DLL" "$@"
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

BUILD_LOG=$(mktemp)
dotnet build "$APP_PROJECT" --nologo -v quiet -clp:ErrorsOnly >"$BUILD_LOG" 2>&1 || {
    BUILD_EXIT=$?
    rm -rf "$LOCK_DIR" 2>/dev/null || true
    trap - EXIT
    if grep -qiE "(CS2012|MSB3021|cannot open.*for writing|process cannot access|being used by another process)" "$BUILD_LOG" 2>/dev/null; then
        echo "ERROR: build failed: App.dll is locked by a running orchestrator instance (serve-dashboard?); stop it and retry" >&2
    else
        cat "$BUILD_LOG" >&2
        echo "ERROR: dotnet build failed" >&2
    fi
    rm -f "$BUILD_LOG"
    exit $BUILD_EXIT
}
rm -f "$BUILD_LOG"

rm -rf "$LOCK_DIR" 2>/dev/null || true
trap - EXIT

exec dotnet "$APP_DLL" "$@"
