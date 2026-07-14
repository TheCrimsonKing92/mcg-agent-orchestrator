# 2026-07-14 Loop Handoff False Failure

## Symptom

The 2026-07-14 16:40Z batch45 to batch46 loop handoff reported `LOOP_HANDOFF_FAILED` even though the successor later started. The conduct event stream shows `LOOP_STOP` at `2026-07-14T16:40:26Z`, `LOOP_HANDOFF_FAILED` at `2026-07-14T16:40:48Z`, and `LOOP_START` at `2026-07-14T16:41:42Z`.

Source: backlog item `61ca9c5dab8c4221b5a6e74871ae6b3f` and `.orchestrator/logs/conduct-events.log`.

## Root Cause

Backlog item `61ca9c5dab8c4221b5a6e74871ae6b3f` records three residual defects:

- The verification window was 10 seconds while the successor boot took about 75 seconds under load, so `loopStartJournaled=false` was reported for a healthy process.
- The handoff retry spawned a second successor while the first successor was still alive, causing a single-instance collision and misleading evidence from the failed retry.
- Detached launch used `bInheritHandles:false` while redirecting stdout/stderr through inheritable handles, so successor console output was lost even though the process ran.

The receipt that proved the earlier survival fix worked was the same conduct sequence: the successor emitted `LOOP_START` after the failure report. Git history also records `81365c6c Integrate goal/59c23d66`, the landed predecessor fix for surviving handoff successors.

## Falsified Alternatives

The incident falsified "successor does not survive" for this case: the conduct stream later journaled `LOOP_START`. The remaining failure was reporting and observability around a slow but live successor, not the absence of a successor.

## Fix

No fix for the three residual defects is recorded as landed in this incident entry. The predecessor survival work landed as `81365c6c Integrate goal/59c23d66`.

## Residuals

- Backlog `61ca9c5dab8c4221b5a6e74871ae6b3f`: widen live-successor verification, avoid retry while an attempt is still alive, and preserve redirected successor output.

## Lessons

Do not treat a missing loop-start journal inside a short fixed window as failure while the successor process is still alive. Handoff diagnostics must report per-attempt evidence so a later failed retry cannot overwrite the evidence for a still-running first attempt.

