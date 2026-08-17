# Gate-lane variance characterization (`cb5bed65`)

Measurement date: 2026-08-17. This is a characterization, not a proposed fix.

## Method and receipt qualification

The measurement uses `*.gate-heartbeat.json` process spans under the canonical
acceptance-attempt store. A row qualifies only when its attempt id joins to an
`*.attempt.json`, the attempt's `branchHeadSha` contains commit `1f669450`, the
heartbeat has `state == "completed"` and `exitCode == 0`, and both timestamps parse
with a non-negative interval. Commit ancestry, not wall-clock time, is the receipt
floor. The floor commit timestamp is used only to avoid unnecessary ancestry checks.

Run:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Measure-GateLaneVariance.ps1 `
  -ReceiptRoot <repository-common-root>\.orchestrator\acceptance-gate-attempts `
  -RepositoryRoot .
```

The script reports every qualified run after the aggregate tables. Percentiles use
R-7 linear interpolation (the same interpolation as Excel `PERCENTILE.INC`). All
timestamps are normalized through `DateTimeOffset`; no local/UTC comparison is made
on unnormalized values.

The scan found 1,223 attempt records with zero parse failures. It reconstructed 34
post-floor gate intervals. All 34 attempt metadata records agreed with their
goal-named receipt directory (`goal_identity_mismatches=0`), so cross-goal gate
identity is recoverable from the attempt store for this window.

## Distribution

Durations are seconds.

| Lane | n | Short of 20 | Mean | Min | p50 | p90 | p99 | Max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Worker dispatch fixtures | 10 | 10 | 392.8 | 311.9 | 405.9 | 450.9 | 503.1 | 508.9 |
| Goal acceptance build slots | 8 | 12 | 335.4 | 289.9 | 323.8 | 383.7 | 434.3 | 439.9 |
| Goal worktree cleanup | 10 | 10 | 330.9 | 237.5 | 343.9 | 410.7 | 454.0 | 458.8 |
| Process spawning | 10 | 10 | 327.7 | 250.4 | 298.5 | 413.7 | 464.2 | 469.8 |
| Goal lifecycle commands | 9 | 11 | 267.1 | 193.2 | 270.4 | 335.1 | 355.3 | 357.5 |
| Dotnet build slots | 9 | 11 | 225.1 | 196.9 | 224.7 | 257.2 | 258.9 | 259.1 |

No lane has the requested 20 post-floor receipts. The shortfalls above are reported
instead of admitting pre-`1f669450` data. With only 8-10 observations, p99 is an
interpolation immediately below the maximum, not a statistically distinct tail
estimate. Process spawning has the clearest mean/p50 divergence (327.7s versus
298.5s), consistent with the observed right tail; the sample is too small to infer a
distribution family.

### Exclusions

Each candidate is assigned the first applicable exclusion reason.

| Lane | Candidates | Qualified | No joined attempt | Before floor / unknown commit | Parse | State | Exit | Time |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Worker dispatch fixtures | 360 | 10 | 16 | 333 | 0 | 1 | 0 | 0 |
| Goal acceptance build slots | 362 | 8 | 16 | 335 | 0 | 1 | 2 | 0 |
| Goal worktree cleanup | 384 | 10 | 25 | 348 | 0 | 1 | 0 | 0 |
| Process spawning | 388 | 10 | 25 | 352 | 0 | 1 | 0 | 0 |
| Goal lifecycle commands | 358 | 9 | 16 | 332 | 0 | 0 | 1 | 0 |
| Dotnet build slots | 385 | 9 | 25 | 350 | 0 | 1 | 0 | 0 |

## Concurrency partition

For each lane span, `peak lanes` is the maximum number of parseable target-heartbeat
intervals from the same attempt active at one instant. `Peak gates` is the maximum
number of post-floor attempt intervals active at one instant, including attempts from
other goals. Endpoints are taken from heartbeat `startedAt`/`lastObservedAt` and
attempt `startedAt`/`completedAt` (falling back to `lastHeartbeatAt`). Values in the
partition columns are `peak-count:run-count:mean-seconds`. `r` is the descriptive
Pearson correlation; it is not a causal estimate.

| Lane | Peak-lane partitions | Lane r | Peak-gate partitions | Gate r |
|---|---|---:|---|---:|
| Worker dispatch fixtures | 3:n1:335.9, 4:n9:399.1 | 0.326 | 1:n8:406.7, 2:n2:337.4 | -0.475 |
| Goal acceptance build slots | 3:n1:289.9, 4:n7:341.9 | 0.390 | 1:n6:343.3, 2:n2:311.6 | -0.312 |
| Goal worktree cleanup | 3:n1:237.5, 4:n9:341.3 | 0.439 | 1:n8:346.9, 2:n2:267.0 | -0.450 |
| Process spawning | 3:n2:300.1, 4:n8:334.6 | 0.198 | 1:n8:344.4, 2:n2:261.2 | -0.479 |
| Goal lifecycle commands | 3:n1:270.4, 4:n8:266.7 | -0.023 | 1:n7:282.3, 2:n2:214.2 | -0.554 |
| Dotnet build slots | 4:n9:225.1 | not estimable | 1:n7:231.2, 2:n2:203.7 | -0.536 |

The lane-count partitions are too imbalanced to establish a correlation: four lanes
have only one observation at peak 3, process spawning has two, and Dotnet build slots
has no predictor variation. Cross-goal gate activity is recoverable, but only two
qualified observations per lane saw peak 2. Those observations were faster on
average in every lane, so this window contains no positive association between gate
count and duration. This is sparse observational evidence, not proof that concurrent
gates have no effect.

The retained `conduct-events.log` has no lines for the four goal prefixes in this
window. Attempt metadata is sufficient for gate intervals, but worker-dispatch and
operator-activity intervals are not recoverable. A future measurement-instrumentation
slice would need a retained, UTC-normalized machine-activity interval record with
stable activity and goal identities; this report does not estimate that missing
dimension.

## Cleanup-lane variable cost

The dominant fixture step is **not identifiable from current receipts**. The gate log
emits `PHASE_PROGRESS` for the cleanup test process at roughly 30-second observations
and one `shard-complete` total. `CliPhaseTimingRecorder` emits outer CLI phases such as
`workspace-rebase-check`, `workspace-rebase`, and `verification-suite`. Neither source
times directory creation, `git init`, either `git config`, `git add`, `git commit`, or
the SQLite migration inside `CreateSeededRepository`.

The available TRX files narrow the gap without filling it:

- All ten qualified cleanup TRX files contain the same 156 test results.
- The sum of test-result durations is 230.4s for the 237.5s fastest lane and 446.3s
  for the 458.8s slowest lane. The runner residual is 7.1s and 12.5s respectively;
  across all runs it ranges from 7.1s to 25.1s. Thus the slow tail is inside reported
  test execution, not primarily unreported lane-launch overhead.
- The largest single-test range is 21.23s
  (`Reconcile_sweep_retries_acceptance_after_untracked_rebase_blocker_is_removed`,
  2.93-24.16s), below one tenth of the cleanup lane's 221.3s range. The next ranges
  are 17.06s and 11.42s. No single test, much less a fixture sub-step, dominates the
  observed lane spread.

The evidence therefore localizes variability only to many test executions in the
cleanup process. Naming git, SQLite, directory I/O, CPU, or contention as the dominant
physical mechanism would be speculation. Per-step spans around those fixture
operations are the missing evidence, but adding them is outside this characterization
slice.

## Negative controls and disposition

The fastest cleanup run (237.5s) had peak lanes 3 and peak gates 1. The slowest
(458.8s) had peak lanes 4 and peak gates 1, but another peak-4/peak-1 run completed in
242.6s. Therefore peak lane count cannot by itself explain both floor and tail. The
two peak-gate-2 runs completed in 265.6s and 268.4s, so concurrent-gate count also
does not explain the 458.8s tail in this sample.

No causal mechanism is established, and no variance remedy is proposed. Consequently
there is no predicted post-remedy spread in this slice. The falsifiable result is the
measurement itself: more post-floor receipts can overturn the reported partitions and
correlations; per-step fixture timings can settle the presently undecidable dominant
step.
