#!/usr/bin/env bash
# Emits a STATUS line every INTERVAL seconds for the given goal prefixes:
# generation count (dispatch.json files) + the NEWEST dispatch's own state
# (its heartbeat state, "done" if exited, or "DOA" if it has neither).
# Usage: ./scripts/watch-goal-pulse.sh <goalPrefix> [goalPrefix...]
INTERVAL="${WATCH_INTERVAL:-420}"
LOGS="$(dirname "$0")/../.orchestrator/logs"
while true; do
  line=""
  for g in "$@"; do
    n=$(ls "$LOGS"/"$g"-*.dispatch.json 2>/dev/null | wc -l)
    newest=$(ls -t "$LOGS"/"$g"-*.dispatch.json 2>/dev/null | head -1)
    st=""
    if [ -n "$newest" ]; then
      base="${newest%.dispatch.json}"
      if [ -f "$base.heartbeat.json" ]; then
        st=$(grep -oE '"state":"[a-z-]+"' "$base.heartbeat.json" 2>/dev/null | cut -d'"' -f4)
      elif [ -f "$base.exit.txt" ]; then
        st="done=$(cat "$base.exit.txt" 2>/dev/null)"
      else
        st="DOA"
      fi
    fi
    line="$line$g:g$n/$st "
  done
  echo "STATUS $line"
  sleep "$INTERVAL"
done
