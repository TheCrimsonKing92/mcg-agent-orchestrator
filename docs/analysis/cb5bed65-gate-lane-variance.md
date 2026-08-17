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

The scan found 1,223 attempt records with zero parse failures and 34 post-floor
attempts. All 34 attempt metadata records agreed with their goal-named receipt
directory (`goal_identity_mismatches=0`), so cross-goal identity is recoverable for
this window. Heartbeat parsing also reported zero JSON or timestamp failures. Sixteen
attempts contain parseable lane work; the remaining 18 are excluded from the active-
gate predictor rather than treating an attempt that never started a lane as equivalent
to a full gate. Of those 18, sixteen are `8ab0a29d` attempts lasting 5.0-9.1s; the
other two are `695e3554` attempts lasting 4.0s and 1,706.9s. Absence of a parseable
lane heartbeat, not attempt duration, is the exclusion rule.

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

There are two accounting populations. First, every post-floor attempt is accounted
for per lane. `No heartbeat` means the attempt produced no file for that lane; the
other outcome columns assign each produced heartbeat its first applicable result.
Thus each row below sums to 34 and distinguishes a gate that never reached the lane
from a dropped or rejected heartbeat.

| Lane | Eligible attempts | With heartbeat | No heartbeat | Qualified | Parse | State | Exit | Time |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Worker dispatch fixtures | 34 | 11 | 23 | 10 | 0 | 1 | 0 | 0 |
| Goal acceptance build slots | 34 | 11 | 23 | 8 | 0 | 1 | 2 | 0 |
| Goal worktree cleanup | 34 | 11 | 23 | 10 | 0 | 1 | 0 | 0 |
| Process spawning | 34 | 11 | 23 | 10 | 0 | 1 | 0 | 0 |
| Goal lifecycle commands | 34 | 10 | 24 | 9 | 0 | 0 | 1 | 0 |
| Dotnet build slots | 34 | 10 | 24 | 9 | 0 | 1 | 0 | 0 |

Second, the historical heartbeat-file scan explains why existing files are not in the
post-floor sample. `No joined attempt` and `Before floor / unknown commit` are
file-window exclusions and are intentionally not mixed into the 34-attempt totals.

| Lane | Candidate files | No joined attempt | Before floor / unknown commit |
|---|---:|---:|---:|
| Worker dispatch fixtures | 360 | 16 | 333 |
| Goal acceptance build slots | 362 | 16 | 335 |
| Goal worktree cleanup | 384 | 25 | 348 |
| Process spawning | 388 | 25 | 352 |
| Goal lifecycle commands | 358 | 16 | 332 |
| Dotnet build slots | 385 | 25 | 350 |

## Concurrency partition

For each lane span, `peak lanes` is the maximum number of parseable target-heartbeat
intervals from the same attempt active at one instant. `Peak gates` counts the
envelopes of parseable lane work for distinct post-floor attempts, including attempts
from other goals. An attempt with no parseable target heartbeat is not active gate
work. All intervals are half-open (`start <= t < end`), so an exact hand-off does not
count as overlap. Values in the partition columns are
`peak-count:run-count:mean-seconds`. `r` is the descriptive Pearson correlation; it is
not a causal estimate.

| Lane | Peak-lane partitions | Lane r | Peak-gate partitions | Gate r |
|---|---|---:|---|---:|
| Worker dispatch fixtures | 3:n1:335.9, 4:n9:399.1 | 0.326 | 1:n10:392.8 | not estimable |
| Goal acceptance build slots | 3:n1:289.9, 4:n7:341.9 | 0.390 | 1:n8:335.4 | not estimable |
| Goal worktree cleanup | 3:n1:237.5, 4:n9:341.3 | 0.439 | 1:n10:330.9 | not estimable |
| Process spawning | 3:n2:300.1, 4:n8:334.6 | 0.198 | 1:n10:327.7 | not estimable |
| Goal lifecycle commands | 3:n1:270.4, 4:n8:266.7 | -0.023 | 1:n9:267.1 | not estimable |
| Dotnet build slots | 4:n9:225.1 | not estimable | 1:n9:225.1 | not estimable |

The lane-count partitions are too imbalanced to establish a correlation: four lanes
have only one observation at peak 3, process spawning has two, and Dotnet build slots
has no predictor variation. Cross-goal gate identity and lane-work intervals are
recoverable, but no qualified run overlaps another work-bearing gate in this window.
The gate-count predictor therefore has no variation and its correlation is not
estimable. This is an observed-data gap, not evidence that concurrent gates have no
effect.

The retained `conduct-events.log` has no lines for the four goal prefixes in this
window. Attempt metadata is sufficient for gate intervals, but worker-dispatch and
operator-activity intervals are not recoverable. A future measurement-instrumentation
slice would need a retained, UTC-normalized machine-activity interval record with
stable activity and goal identities; this report does not estimate that missing
dimension.

## Cleanup-lane variable cost

Criterion 3 is amended for this goal: either identify the dominant step or establish
that recorded timing is too coarse, naming the present and required granularity. The
dominant fixture step is **not identifiable from current receipts**. The gate log
emits `PHASE_PROGRESS` for the cleanup test process at roughly 30-second observations
and one `shard-complete` total. `CliPhaseTimingRecorder` emits outer CLI phases such as
`workspace-rebase-check`, `workspace-rebase`, and `verification-suite`. Neither source
times directory creation, `git init`, either `git config`, `git add`, `git commit`, or
the SQLite migration inside `CreateSeededRepository`.

The measurement script now reads the qualified TRX files directly, so the following
figures are rerunnable rather than an ad-hoc calculation. They narrow the gap without
filling it:

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
242.6s. Therefore peak lane count cannot by itself explain both floor and tail. Every
qualified cleanup run has peak gates 1, so gate concurrency cannot be tested in this
window and is not claimed as an explanation.

No causal mechanism is established, and no variance remedy is proposed. Consequently
there is no predicted post-remedy spread in this slice. The falsifiable result is the
measurement itself: more post-floor receipts can overturn the reported partitions and
correlations; per-step fixture timings can settle the presently undecidable dominant
step.
