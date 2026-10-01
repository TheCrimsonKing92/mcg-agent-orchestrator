# 2026-07-15 Dormant Red Tests Monopolize the Gate and Throttle the Board

**Current status (2026-10-01):** Remediated by later goals, with residual backlog items still open; the original record below is unchanged. The two red tests were fixed by goal `e42c2c9b` (landed as `087191daf`), as the Fix section records. Goal `4bc184e2` (landed as `70269c3a2` on 2026-07-23) moved acceptance gates out of the tick into background attempts, closing backlog item `fdb75163`, so one goal's gate no longer holds the tick. Goal `6251e612` (landed as `42db8d9b4` on 2026-07-30) added the clean-test baseline from backlog item `cca13692`, and goal `5d57fe95` (landed as `d910eaed3` on 2026-09-17) requires executed evidence before a candidate failure is called inherited from `main`; see [baseline attribution](../baseline-attribution-replay.md). Backlog items `76d770ac` and `4d63661a` remain open.

## Symptom

For ~3 hours (2026-07-15 09:00-12:20Z) the conduct loop throttled to ~3 ticks/hour and landed nothing autonomously, despite the auto-review-retry goal (`e73887c2`) having landed. The event stream showed only `6e08860f` gate-progress for the entire window. `PHASE_TIMING tick=11 per-goal-walk elapsed_ms=1122841` with `slowest=6e08860f:1122566ms` - one goal's gate consumed 18.7 of a 19-minute tick; the other ten goals got ~40ms each.

## Root Cause

Two pre-existing red tests sat on main (`Cli_failure_triage_classifies_provider_connectivity_with_failover` - expected the old provider-connectivity triage output that `eb1ee9e6` intentionally changed; `Cli_subscription_dispatch_confirm_limit_review_accepts_text_file` - a preflight now requires a goal workspace the test did not create). Every acceptance gate runs the `infrastructure tests: Cli` partition, so every gate failed on these two. The no-give-up retry (landed `cee1d2da`) then re-ran the full suite every tick without ever yielding, and because gates run inline in the tick (`fdb75163`, unfixed), one goal's unpassable gate throttled the entire board.

The operator seeded it: at the `e73887c2` hand-landing (~08:20Z) the two reds were observed (`289/291`) and merged past as "pre-existing." Under the no-give-up gate, a dormant red is not benign - it becomes an active board poison.

## Falsified Alternatives

Not a hung process (child pids advanced, output grew). Not the phantom slot-lock class (that was `5f4bb7de`'s subject, already fixed) - here the gate ran tests to completion and they genuinely failed. Not a full freeze - ticks completed every ~19 minutes; the board was throttled and landing-blocked, not stopped.

## Fix

Immediate unwedge: parked the three gate-blocked Verified goals (`6e08860f`, `a5340f2b`, `a9857a02`) so ticks excluded them and dispatch prep flowed again. Root fix: goal `e42c2c9b` corrected both reds test-side (the new triage output and preflight behavior are the intended landed behavior); operator hand-landed it as `087191da` (its own gate would have been equally blocked). Full `CliCommandTests` `291/291` on the merged candidate was the landing gate. A `batch52 -> batch53` max-duration handoff (clean, second consecutive successful `LOOP_HANDOFF`) accelerated the unwedge.

## Residuals

- Backlog `76d770ac`: the structural bug - no-give-up must distinguish a transient lock (retry) from a terminal test failure (escalate after bounded attempts, do not retry forever). Pairs with `fdb75163` (gates off the tick critical path) and `cca13692` (clean-test-baseline to tell inherited-red from goal-introduced).
- Backlog `4d63661a`: the handoff left `e42c2c9b`'s Reviewer dispatch stale (dead batch52 pid), which the preflight refused and escalated; the reconcile should auto-clear a dead-pid stale-running record.

## Lessons

Never hand-land past a test called "pre-existing red" without either fixing it or filing it as blocking. Under the no-give-up gate, a dormant red on main is a live board-poison that monopolizes every subsequent gate. When unblocking after such an incident, un-park gate-blocked goals ONE canary at a time and confirm its gate passes before releasing the rest - the same red that caused the wedge, if not fully cleared, will re-wedge instantly.
