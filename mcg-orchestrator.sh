#!/usr/bin/env bash
# Cross-platform launcher mirroring mcg-orchestrator.cmd. Separates build from run so
# concurrent invocations never compile simultaneously (avoids CS2012 bin/obj contention).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
export MCG_ORCHESTRATOR_REPOSITORY_ROOT="$ROOT/"

APP_PROJECT="$ROOT/src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj"
APP_DLL="$ROOT/src/Mcg.AgentOrchestrator.App/bin/Debug/net10.0/Mcg.AgentOrchestrator.App.dll"
LOCK_DIR="$ROOT/.build-lock"

# Acquire build lock — mkdir is atomic on POSIX; spin up to 30 s
LOCK_TRIES=0
while ! mkdir "$LOCK_DIR" 2>/dev/null; do
    LOCK_TRIES=$((LOCK_TRIES + 1))
    if [ "$LOCK_TRIES" -ge 30 ]; then
        echo "ERROR: could not acquire build lock after 30 s" >&2
        exit 1
    fi
    sleep 1
done
trap 'rmdir "$LOCK_DIR" 2>/dev/null || true' EXIT

dotnet build "$APP_PROJECT" --nologo -v q

rmdir "$LOCK_DIR" 2>/dev/null || true
trap - EXIT

exec dotnet "$APP_DLL" "$@"