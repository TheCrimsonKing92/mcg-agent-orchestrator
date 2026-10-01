# Case study: attribution partitions that silently ran serially

## Context

The orchestrator lands agent-written changes only after an acceptance gate runs the full test suite against the candidate merged with `main`. To save time, it can gate two ready goals together as a cohort, testing their combined tree once. When a cohort gate fails, the conductor runs attribution: it re-gates each member alone, called a partition, to decide which member caused the failure, or whether only the combination fails.

## Symptom

On 2026-09-29, goals c4abdb65 and 423c801b were gated as a cohort. The combined gate failed at 03:53:13Z after 1,011 seconds. Its lane phase took 739 seconds, running four test lanes at a time.

Attribution started immediately. The first partition, c4abdb65 alone, finished at 04:40:08Z after 2,807 seconds, about 2.8 times the combined gate. The two runs used the same acceptance runner and verifier but tested different candidate trees: the combined gate tested the cohort's `main` revision merged with both goals, and the partition tested that `main` revision merged with c4abdb65 alone. The second partition started at 04:40:17Z. While it ran, another goal landed and the conductor relaunched itself to pick up the new code. At 05:13:16Z the new conductor process refused to adopt the orphaned attribution, because `main` had moved since the attempt started, which is the correct response. About 80 minutes of attribution work ended without a verdict, and both goals had to be gated again.

## Diagnosis

Every gate writes a phase breakdown to the conductor's event log. The breakdowns of the two runs looked very different:

| | Combined gate | c4abdb65 partition |
|---|---|---|
| Total | 1,011 s | 2,807 s |
| Lane phase | 739 s | 0 s |
| Shard concurrency | 4 | unavailable |
| Serial check execution | 156 s | 2,667 s |

The partition never entered the lane phase. It ran every test project as an ordinary sequential check, with no shared prebuild and no parallel shards.

The source explains why. In `GoalAcceptanceVerifier` (`src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs`), the batch runner sends infrastructure test partitions down the parallel shard path only when the attempt holds a stable build slot. That means both a slot index and a slot lease, plus a shard budget above one. The shared test prebuild is created only under the same condition. Regular gates acquire a lease before they run. The attribution code in `ConductorDriver` called the same runner with `stableSlotIndex: null, stableSlotLease: null`. Nothing failed. The verifier took its valid fallback for an attempt without a slot, which is to run everything serially.

## Fix

Goal 397ed30f added `RunCohortPartitionAttempt` in `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.CohortAttributionStableSlot.cs`. Each partition now acquires a cohort stable-slot lease through the same coordinator the combined cohort gate uses, holds it for the partition's lifetime, and passes it to the verifier. The attribution loop in `ConductorDriver.cs` calls this method instead of passing nulls. The source change is one new 26-line file and a two-line call-site change. It landed on `main` in commit 6a2e31d09 at 06:07:51Z on 2026-09-29, as part of a three-goal merge train.

Two test classes guard it. `AcceptanceCohortWorkflowTestsAttributionStableSlotLease` asserts three things:

- each partition holds its own lease and every lease is released exactly once, including when a partition call throws;
- a cancelled attribution releases its lease and records an infrastructure failure;
- a failed lease acquisition produces an indeterminate verdict rather than a guess.

`AcceptanceCohortWorkflowTestsAttributionStableSlotSemantics` asserts that sharded partitions still produce the same attribution outcome and receipt fields for all five outcomes: first member failed, second failed, both failed, interaction only, and indeterminate.

## Result

The next cohort to fail was 423c801b with b9aa7464. Its combined gate took 679 seconds. Attribution started at 06:27:09Z:

- 423c801b alone failed after 685 seconds, with a lane phase of 551 seconds and a shard concurrency of 4.
- b9aa7464 alone passed after 838 seconds, also with a shard concurrency of 4.

Attribution reached its verdict at 06:52:42Z, 25.5 minutes after it started. It blamed 423c801b, and b9aa7464 joined a new cohort 24 seconds later. Before the fix, one partition alone took 46.8 minutes.

These are different goals, so the comparison is not a controlled experiment. What changed structurally is that a partition now costs about the same as a combined gate, instead of close to three times as much. The two partitions still run one after the other, so attribution costs roughly two gates. That is the next thing to reduce.

## What it taught

An optional dependency that defaults to "absent" can switch a system onto a slow path without any failure. The gate's own breakdown receipt showed the regression, as a lane phase of 0 and a concurrency of "unavailable". Recording how a run actually executed, not only whether it passed, is what made the cause findable.
