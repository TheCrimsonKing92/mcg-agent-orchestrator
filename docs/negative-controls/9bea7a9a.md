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
