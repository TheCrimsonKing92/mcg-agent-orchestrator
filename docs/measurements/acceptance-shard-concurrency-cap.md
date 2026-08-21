# Acceptance shard concurrency cap measurement

Date: 2026-08-21

## Decision

The candidate cap is **6**, raised from 4. The current 18-lane topology has about 4,727 seconds of
uncontended-equivalent lane work and an approximately 845-second binding `Process spawning` lane. Cap 5
remains throughput-bound (`4727 / 5 = 945.4s`), while cap 6 reaches the serial floor
(`max(845, 4727 / 6) = 845s`). Cap 6 is therefore the smallest useful measured step. Cap 8 is not attempted
in this goal because its 590.9-second throughput floor is already below the same 845-second serial floor.

These projections are arithmetic on the supplied receipts, not success criteria. The declared
`estimatedSerialSeconds` values are stale—for example, `Process spawning` declares 327.7 seconds—and are
intentionally unchanged so the cap remains the only measurement variable.

## Timeout headroom

The uncontended cap-4 binding lane was 850.8 seconds before the lane split, and the split did not materially
shorten the retained process-spawning work. A conservative proportional cap-6 stress estimate is
`850.8 * 6 / 4 = 1,276.2s` (21.27 minutes), leaving 18.73 minutes against the unchanged 40-minute timeout.
The supplied post-split evidence also estimates a 2.2x slowdown when two gates contend; that estimate comes
from one pair of runs and is not treated as an uncontended safety guarantee. The cap-6 measurement must be
uncontended. Any concurrent gate, lane over 20 minutes, timeout, missing receipt, or new intermittent failure
stops the increase.

## Method

For each run, retain every successful manifest-declared `shard-complete` receipt and the aggregate
`shards-complete elapsed_ms` receipt. Sum lane elapsed values for total work; use the aggregate elapsed value
for shard-phase wall clock; record lane names/count, longest lane, implied average concurrency, commit, and
whether another gate or heavy worker batch shared the host. Missing or failed lanes invalidate the run.

For the negative control, reconstruct each lane interval as `[completion - elapsed, completion]`. For every
exclusive resource key, including `xunit:GoalWorktreeCleanupHooks`, `xunit:EnvMutation`,
`xunit:JobAccounting`, `xunit:ProcessSpawning`, and `xunit:DotnetBuildSlots`, keyed intervals must not overlap.
This real receipt check complements the deterministic cap-6 scheduler test, which requires total peak 6 while
two case-variant same-key lanes remain at peak 1.

## Caps attempted

| Topology / cap | Contention | Lane work | Shard wall | Longest lane | Result |
| --- | --- | ---: | ---: | ---: | --- |
| Pre-split, 4 (2026-08-21 04:04:53Z) | Uncontended | 4,446s (16 receipts) | 1,301s | 850.8s | Baseline; topology differs from candidate |
| Post-split, 4 | Two concurrent gates | Not supplied | Not supplied | 1,691.0s | Context only; not comparable to an uncontended candidate |
| Post-split, 6 | Must be uncontended | Pending acceptance receipt | Pending acceptance receipt | Pending acceptance receipt | Pending first cap-6 gate |

The pre-split baseline reported 16 receipts while the current manifest declares 18 lanes; its raw wall-clock
delta cannot establish a like-for-like improvement. The cap-6 acceptance receipt must be added here together
with a comparable post-split cap-4 receipt before the performance criterion is complete. If cap 6 does not
improve a comparable wall clock, the recorded result stands and the configured cap returns to 4; cap 8 is not
used as a rescue attempt.
