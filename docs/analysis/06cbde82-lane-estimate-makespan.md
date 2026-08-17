# Lane-estimate refresh makespan comparison (`06cbde82`)

Measurement date: 2026-08-17. The estimate refresh landed as `952f3a47` at
2026-08-17T15:18:06Z. This report measures the result; it does not propose another
scheduler change.

## Qualification and cohorts

The source is `*.gate-heartbeat.json` under the canonical acceptance-attempt store.
A heartbeat qualifies when its filename attempt id joins to the sibling
`*.attempt.json`, `state == "completed"`, `exitCode == 0`, and `startedAt` plus
`lastObservedAt` parse to a non-negative interval. Process duration is
`lastObservedAt - startedAt`; summed TRX durations are not used.

The packaged before-window accounting remains authoritative: 274 qualified and 101
excluded receipts (84 missing attempt id, 7 exit 2, 4 running, 4 exit 1, and 2 timed
out) at or after `1f669450`. Applying an additional symmetric complete-target-set
rule leaves six attempts with exactly one qualified heartbeat for each of the 17
infrastructure lanes. The before cohort ends before the next acceptance attempt at
2026-08-17T12:52:39.135Z.

The after window starts at `952f3a47` and ends before the in-progress attempt that
started at 2026-08-17T16:58:01.513Z. It contains 120 receipts: 96 qualified and 24
excluded for missing attempt ids, with no other exclusion category. Four attempts
have the complete 17-lane target set. Each attempt's recorded `mainHeadSha` contains
`952f3a47`.

Lane-phase makespan is the interval from the earliest lane `startedAt` to the latest
lane `lastObservedAt` in a complete attempt. Whole-gate makespan is the attempt's
`completedAt - startedAt`. Both windows use the same definitions.

## Observed makespan

Durations are seconds.

| Measure | Before n | Before mean | Before range | After n | After mean | After range | Mean delta |
|---|---:|---:|---:|---:|---:|---:|---:|
| Infrastructure lane phase | 6 | 670.2 | 546.0-790.0 | 4 | 938.2 | 817.0-1040.0 | +268.0 |
| Whole acceptance gate | 6 | 874.0 | 777.0-998.0 | 4 | 1272.5 | 1125.0-1393.0 | +398.5 |

Makespan did **not** improve. Both after ranges are entirely above their before
ranges. The correct refreshed estimates remain useful calibration, but this sample
contains no scheduling win.

## Lane-cost movement

The complete-attempt cohorts show broad process-cost increases, so the raw makespan
delta cannot be attributed to scheduling order.

| Lane | Before mean (range) | After mean (range) | Mean delta |
|---|---:|---:|---:|
| Cli | 118.2 (84-143) | 182.0 (146-205) | +63.8 |
| Worker shell | 9.0 (6-12) | 39.5 (27-48) | +30.5 |
| Worker sandbox planner | 8.8 (5-12) | 40.5 (29-50) | +31.7 |
| Conduct watch sweep scoping | 11.0 (7-16) | 46.2 (31-71) | +35.2 |
| Goal lifecycle commands | 277.2 (210-358) | 367.2 (335-395) | +90.0 |
| Goal worktree cleanup | 341.2 (265-459) | 479.2 (438-514) | +138.0 |
| Goal worktree parallel | 67.3 (57-80) | 112.2 (91-139) | +44.9 |
| Worker profiles | 61.2 (48-79) | 106.8 (75-132) | +45.6 |
| Worker dispatch fixtures | 402.7 (335-509) | 477.5 (335-553) | +74.8 |
| Process spawning | 300.0 (250-400) | 429.2 (397-469) | +129.2 |
| Chaos gate | 73.7 (52-89) | 134.2 (114-161) | +60.5 |
| Dotnet build slots | 229.8 (197-259) | 291.5 (267-307) | +61.7 |
| Goal acceptance verifier | 41.3 (36-51) | 70.2 (59-80) | +28.9 |
| Goal acceptance build slots | 345.0 (299-440) | 473.0 (441-494) | +128.0 |
| Remainder balance A | 134.5 (133-136) | 166.8 (155-179) | +32.3 |
| Remainder balance B | 31.0 (27-35) | 58.8 (47-66) | +27.8 |
| Remainder | 63.8 (55-70) | 121.0 (93-154) | +57.2 |

This table establishes cost movement but not its cause. No contention, load, or
resource-pressure mechanism is inferred from these observations.

## Fixed-duration scheduling replay

`GoalAcceptanceVerifier` orders lanes by descending `estimatedSerialSeconds`, then
uses manifest order as the tie-breaker. It starts the first runnable lane while fewer
than four lanes are active and honors each lane's exclusive resource keys.

The old estimates come from the refresh merge's first parent, `c17f5c15`; the
refreshed estimates come from landed merge `952f3a47`. Their rank orders differ. A
replay preserving each attempt's observed lane durations, four-lane concurrency,
manifest order, and resource keys gives the following refreshed-order minus old-order
deltas: before mean +3.2 seconds, range 0.0 to +10.6; after mean -12.0 seconds, range
-14.7 to -10.3. Negative values favor the refreshed order.

The scheduling contribution is therefore small and cost-vector-sensitive within the
observed cohorts: the refreshed order is up to 10.6 seconds slower on the before
vectors and 10.3-14.7 seconds faster on the after vectors. Those effects are much
smaller than the observed lane-phase spread, and the sign changes between cohorts, so
they do not prove an observed scheduling win. The raw +268.0-second lane-phase delta
and +398.5-second whole-gate delta remain dominated by lane-cost movement; the
receipts do not identify its physical cause.

## Disposition

Negative control: **no improvement**. The refreshed values changed calibration and
ordering, but the measured after makespan is slower. The replay's small counterfactual
benefit on the after vectors does not establish a scheduling win in observed wall-clock
time, so one must not be claimed from this refresh.
