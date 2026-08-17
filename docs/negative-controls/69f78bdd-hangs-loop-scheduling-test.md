# The acceptance timeout is a hang in one test, not slowness

Goal `69f78bdd` (defer spec refinement to a durable outbox) has failed acceptance twice with
`acceptance-check-timeout: infrastructure-tests-dotnet-build-slots elapsed=40m budget=40m`. The
lane is not slow. One test hangs.

## The hang

    ConductorBatchLoopTestsLoopSchedulingPolicy.ConductorBatchLoopContinuesTickingWhileGoalRefinementIsInFlight

Bisected at candidate `34aab425` by running each class in the lane filter directly against the
built apphost:

| Class | Result |
| --- | --- |
| `DotnetBuildEnvironmentManagerTests` | 105 tests, 1m32s |
| `LocalProcessVerifierDotnetBuildSlotTests` | 7 tests, 2.7s |
| `ConductorBatchLoopTestsGoalStallExit` | 10 tests, 0.8s |
| `ConductorBatchLoopTestsFaultIsolationEligibility` | 14 tests, 1.6s |
| `…Janitorial` + `…ReapingDetach` + `…WatchProgress` + `…PersistenceFailure` | 73 tests, 13s |
| `…DependencyCompletion` + `…RetryRecovery` + `…SetAsideReadmit` + `…ConductEvents` | 36 tests, 1.5s |
| `…SelfHandoff` | 24 tests, 19s, all pass |
| `…ParallelAcceptance` | 49 tests, 1m16s, **1 failed** |
| **`…LoopSchedulingPolicy`** | **hangs past 540s** |
| the single method above, run alone | **hangs past 180s** |

Every other class completes in seconds. The named test hangs on its own.

## Evidence it is a hang and not a slow lane

The gate heartbeat for the timed-out attempt records `lastProgressAt` equal to `startedAt` — no
progress at all for 29 minutes — with `stdoutBytes: 0` and `stderrBytes: 0`. The escalation's
resource line reports `cpu_ms=7593` for a 40-minute gate: roughly 7.5 seconds of CPU, so the
process is blocked, not spinning.

## Why this goal

The test is named for the behaviour this goal changes: it asserts the batch loop keeps ticking
*while goal refinement is in flight*. This goal removed the eager
`GoalRefinementWorkCoordinator.TryLaunchFirstPending` call from the top of every CLI command and
moved refinement to a durable outbox. If the fixture's in-flight refinement is now never launched,
anything the test waits on to observe "in flight" can never arrive, and the wait has no bound.

Stated as the leading hypothesis, not a conclusion: confirm by finding what the test waits on and
what was expected to satisfy it.

## An operator attribution to retract

An earlier operator note attributed this goal's first acceptance timeout to temp-reaper overhead —
up to 53s per test-host start with 1106 orphan roots — and the goal was mechanically reopened on
that basis. That defect was real and measured, and it is fixed on `main` in `cdbd6813` and
`1f669450`, but it is **not** the cause of this timeout. The roots are down to 72, the reaper cost
is now ~3s, and the same lane still hangs with zero output. The mechanical reopen was therefore
premature.

## Required

Bound or remove the wait, and make the test observe refinement through the durable outbox rather
than through whatever the eager launch used to provide. A test that can hang indefinitely is a
`test-design-discipline` rule (o) defect regardless of this goal's outcome — the bound is a hang
detector, and the absence of one is why a 40-minute gate budget was consumed twice.

Also note `…ParallelAcceptance` has 1 failing test at this candidate, separate from the hang and
not yet diagnosed.
