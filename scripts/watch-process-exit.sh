#!/usr/bin/env bash
# Waits for a Windows process to exit, then prints the tail of a log file.
# Single completion notification for detached commands (acceptance runs etc).
# Usage: ./scripts/watch-process-exit.sh <pid> <logfile> [tailBytes]
PID="$1"
LOG="$2"
BYTES="${3:-300}"
until [ -z "$(ps -W 2>/dev/null | grep "$PID")" ]; do sleep 30; done
echo "PROCESS $PID EXITED"
tail -c "$BYTES" "$LOG"
