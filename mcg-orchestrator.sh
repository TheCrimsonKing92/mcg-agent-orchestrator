#!/usr/bin/env bash
# Cross-platform launcher mirroring mcg-orchestrator.cmd. Runs the orchestrator CLI with the
# repository root set so worktrees and git operations target this repo.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
export MCG_ORCHESTRATOR_REPOSITORY_ROOT="$ROOT/"
exec dotnet run --project "$ROOT/src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj" -- "$@"
