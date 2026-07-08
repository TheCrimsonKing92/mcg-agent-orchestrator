#!/usr/bin/env bash
# Tails the newest operator conduct-loop log (or a given one) and emits
# terminal events for the given goal prefixes plus LOOP_STOP / errors.
# Usage: ./scripts/watch-loop-events.sh <goalPrefix> [goalPrefix...]
# Env: WATCH_LOOP_LOG overrides log selection.
LOGS="$(dirname "$0")/../.orchestrator/logs"
LOG="${WATCH_LOOP_LOG:-$(ls -t "$LOGS"/operator-conduct-loop-*.out.log 2>/dev/null | head -1)}"
if [ -z "$LOG" ]; then echo "no conduct-loop log found"; exit 1; fi
goals=$(IFS='|'; echo "$*")
echo "WATCHING $(basename "$LOG") goals=$goals"
tail -f "$LOG" | grep -E --line-buffered "($goals).*(Landed|escalated)|LOOP_STOP|^Error:"
