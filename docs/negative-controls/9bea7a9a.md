# 9bea7a9a blocked cohort head yield

## Decision seam

`ConductorAcceptanceCohortSelector.Select` receives a typed snapshot of resources held by live
acceptance attempts. When the durable forced head conflicts with that snapshot, it may select the
oldest younger candidate that is disjoint from every holder and has a compatible disjoint peer.
Unknown holder scope reserves `ownership:unknown-acceptance-scope` and fails closed.

## Controlled RED

Disable only the held-resource fairness branch in `ConductorAcceptanceCohortSelector.Select`, leaving
candidate projection, pair conflict checks, and durable store behavior unchanged. Run:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~ConductorAcceptanceCohortTests|FullyQualifiedName~ConductorCrossTickTests'
```

The negative control must execute a positive count and fail
`Selector_BlockedForcedHeadYieldsToOldestRunnableDisjointCandidate`: the old selector admits the
forced head instead of the expected younger pair. A compile failure, zero tests, or any unrelated
failure is inconclusive.

## Restored GREEN

Restore the held-resource fairness branch and rerun the same command. The selector class proves the
yield, same-file conflict evidence, and unknown-resource fail-closed behavior. The cross-tick class
proves the persisted overtake transition and that the head is selected immediately after its holder
releases the conflict, without sleeps or elapsed-time assertions.

## Background transition emission correction

Change only the production batch callback so it records the admission transition without emitting
`ACCEPTANCE_COHORT_FAIRNESS_TRANSITION`, then run:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~AcceptanceCohortWorkflowTestsBackgroundAndCapacity.ProductionBatch_LongCohortGateDoesNotBlockTicksOrOperatorIntents_AndReconcilesLater'
```

The RED arm must execute one test and fail `Assert.Single`: the background gate records the durable
transition after the tick has already returned `CohortInFlight`, so no typed fairness-transition event
is present. Restoring emission inside the gate-admitted callback must execute one test and pass with
exactly one event naming the oldest goal and the `previous=1 resulting=0 oldest_admitted=True`
transition. Acceptance owns both executable receipts.

## Fairness decision durable classification

Remove only the `ACCEPTANCE_COHORT_FAIRNESS` arm from `TryClassifyConductEvent`, then run:

```powershell
.\scripts\Invoke-RepoScript.ps1 scripts\Invoke-TestSummary.ps1 -Target tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'Name~ConductLog_FairnessYield_PersistsTypedDecision'
```

The RED arm must execute one test and fail `Assert.Single` with an empty collection because the
yield decision was emitted but dropped before the durable conduct-event write. Restoring the arm
must execute one test and persist one `acceptance-cohort` record containing the blocked head,
selected younger goal, typed conflict holder/key, and overtake count. Acceptance owns the final
candidate-SHA RED/GREEN receipts.
