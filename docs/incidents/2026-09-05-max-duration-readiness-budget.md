# 2026-09-05 Max-Duration Readiness Budget

## Symptom

Two staged successors were killed by the continuity supervisor's 120-second readiness budget even though
they had acquired the loop lease or were completing startup work. The 2026-09-04 21:30:31Z generation was
killed near 21:32:31Z without reaching `LOOP_START`. The 2026-09-05 02:27:37Z generation was killed at
02:29:37Z and likewise emitted no `LOOP_START`.

## Root Cause

Readiness meant observing `LOOP_START`, which is emitted only after the pre-loop terminal sweep. The
2026-09-04 21:30:31Z generation emitted a stale-terminal exclusion count of 1,042 and repair lines for three
merged worktrees (goals 6f9ddf54, 89a066b9, and a108e1c7) before it was killed. The log has no per-repair
timestamps, so individual merged-branch-cleanup durations are not recoverable; collectively the startup
sweep and subsequent pre-loop work did not reach `LOOP_START` within 120 seconds.

The 2026-09-05 02:27:37Z generation's stdout log was zero bytes. At 02:29:37Z its fallback reported a stale
loop lock written two minutes earlier by the killed pid. That places the budget consumption after lock
acquisition and before sweep output. Because repair lines are printed only after the sweep returns, that log
cannot distinguish initial state load from an in-progress sweep and does not support assigning a duration to
either step. In comparison, the 04:03:47Z successor removed two merged worktrees and reached `LOOP_START` at
04:04:29Z (42 seconds); the 00:48:11Z cold start removed one and reached it at 00:48:41Z (30 seconds); the
02:32:10Z cold start removed none and reached it at 02:32:50Z (40 seconds).

Merged worktrees landed during a generation can reach the next generation because the per-tick sweep runs
at the start of a tick. A landing in the final tick has no following in-process sweep. Additionally,
`src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSweep.cs` sets `skipMergedCleanupThisPass` when it
repairs a missing durable landing intent, and the `GoalWorktrees.RemoveTerminal` guard requires that flag to
be false. Cleanup therefore becomes eligible on a later pass, which can be the successor's startup sweep.

## Falsified Alternatives

The failures do not show that the staged binaries could not start. The 02:27:37Z lock receipt proves that
successor acquired the lease, and the successful comparison generations reached `LOOP_START` with the same
state-load and cleanup sequence inside the existing budget.

## Fix

The conduct child now emits `LOOP_READY ` after acquiring the loop lease and loading state, before the
terminal sweep. `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorContinuitySupervisor.cs` accepts that
line while retaining `LOOP_START ` as a compatibility readiness signal and as the operator's loop-start
marker. The readiness timeout and incumbent fallback remain unchanged for a child that emits neither line.

## Residuals

The in-process `ConductorLoopHandoff` verifier still keys on `LOOP_START`; supervised max-duration renewal
does not use that path. Moving last-tick worktree cleanup into an incumbent drain pass remains separate work
because it needs its own destructive-worktree safety evidence.

## Lessons

A readiness budget should cover only the prerequisites represented by readiness. Janitorial work that can
scale with repository history must not make an already-authoritative successor appear dead.
