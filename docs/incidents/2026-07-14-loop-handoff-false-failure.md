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

All three residual defects were fixed by goal `b9bbe713` (landed `c060ec08 Integrate goal/b9bbe713`, 2026-07-14): verification waits up to 120s while the successor process is alive, no retry spawns while an attempt's process lives (per-attempt evidence journaled), and stdio handles are inherited via a selective handle list (the naive `bInheritHandles:true` first attempt re-tethered the successor to the parent job and was caught at operator landing verification before merge). The predecessor survival work landed earlier as `81365c6c Integrate goal/59c23d66`.

LIVE VALIDATION 2026-07-15 08:32Z: the batch51 -> batch52 max-duration handoff journaled `LOOP_HANDOFF` (success) for the first time - `LOOP_STOP` 08:32:10, successor verified, `LOOP_START` 08:32:57, no duplicate spawn, and the successor's console log captured output (4,561 bytes and growing, vs 0 bytes for every previous handoff-spawned generation).

## Residuals

- None for this incident. Related improvement (not a defect): backlog `8856062b` - relaunch-on-landing, so conductor-scoped landings trigger a fresh-staged handoff instead of waiting for max-duration.

## Lessons

Do not treat a missing loop-start journal inside a short fixed window as failure while the successor process is still alive. Handoff diagnostics must report per-attempt evidence so a later failed retry cannot overwrite the evidence for a still-running first attempt.

