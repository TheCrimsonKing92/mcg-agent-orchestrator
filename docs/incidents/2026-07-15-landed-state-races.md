# 2026-07-15 Landed-State Races and Shared-Git Collisions

**Current status (2026-10-01):** Remediated by later goals, with residual backlog items still open; the original record below is unchanged. Goal `81f85740` (landed as `cd5b6d28e` on 2026-07-27) made the conductor tick the only writer of goal state: recovery commands such as `retry`, `progress` and `verify-manual` now submit typed intents that the tick applies; see the [state model](../state-model.md) and the [operator runbook](../operator-runbook.md). Goal `38ae793c` (landed as `8d70e552f` on 2026-08-13) made branch integration conductor-owned, so workers no longer rebase; see Branch integration ownership in the [role capability matrix](../role-capability-matrix.md). Backlog items `b940ad5c`, `79ab3324` and `bb6f496a` remain open.

## Symptom

Two related incidents during the 2026-07-14/15 landing waves:

1. Goal `194fc64a` was marked landed by the operator at 19:24Z (content merged as `fba3c00d`), yet at ~19:56Z the loop re-dispatched a full Developer round onto the landed goal, producing a 29-minute stale-base round (commit `d8c6db6b`, discarded) that edited files owned by other landed goals. The landed disposition was later overwritten a second time by a failed Tester exit reconciliation (goal flipped back to Verified at ~20:35Z).
2. Goal `c0624af9`'s Developer hit `git-rebase-blocked - shared .git packed-refs.lock permission denied` at ~18:35Z, leaving its worktree mid-cherry-pick; the next dispatch walked into the in-progress state and failed sandbox preflight. Operator completed the sequence by hand (final commit `52cc3da8`).

## Root Cause

1. The goal-mark-landed status flip does not survive concurrent state writers: cleanup-budget exhaustion can cancel the flip's commit, and task-exit reconciliation writes goal state last-writer-wins over the disposition. Two distinct overwrite paths were observed in one evening.
2. Worker rebases run against the SHARED repository `.git`; concurrent operator git operations (a landing wave's merges and pushes at ~19:15-19:30Z) contended `packed-refs.lock`, killing the worker's rebase mid-operation. Workers resolve refs from live repo state, which also enabled a second contamination class: two goal branches captured the operator's transient scratch-merge commit (`7ab3270e`) from the repo root's checked-out state.

## Falsified Alternatives

The 194fc64a re-dispatch was not a record-only ghost (the sweep's normalization path) - a real dispatch ran with a real commit. The packed-refs failure was not a permanent permission problem - it was transient lock contention, proven by the operator completing the same operations minutes later.

## Fix

Operator recoveries only (branch reset to accepted tip; cherry-pick completion; status-tool repairs). No structural fix landed yet.

## Residuals

- Backlog `b940ad5c`: cleanup never on the landing critical path; disposition commits first in a short transaction (owns overwrite path 1).
- Backlog `79ab3324`: workers rebase against a pinned dispatch-time main sha, never live repo-root state (owns the contamination class).
- Backlog `bb6f496a`: orchestrator-side round-boundary rebasing (would subsume worker-vs-operator git contention).
- Operator rule adopted: verification merges in temporary worktrees, never the repo root, while workers run.

## Lessons

A durable disposition that can be silently overwritten by any later state writer is not durable. Operator git activity on the shared repository is an unmodeled concurrent actor - until workers pin their base refs, landing waves and worker rebases must not overlap.
