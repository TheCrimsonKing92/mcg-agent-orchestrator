# Negative-control receipt: cohort gate faults and recycled-pid ancestry

Two facts are protected here:

- the **dead-pid fact** — `WorkerProcessJobs_ancestry_walk_refuses_recycled_dead_ancestor_pid`
  (`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs`), which drives the
  ancestry walk through an injected parent-and-start-time lookup whose chain ends at a pid Windows has
  recycled, and asserts the candidate holding that pid is not an ancestor of the protected process.
- the **faulted-cohort fact** — `CohortGateFault_HoldsBothMembersAndKeepsTheTickAlive`,
  `CohortGateFault_EscalatesBothMembersOnlyAtTheTransientFailureCap` and
  `CohortGateFault_NonTransientFaultEscalatesBothMembersWithoutEndingTheTick`
  (`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsParallelAcceptanceCohorts.cs`),
  which drive a cohort gate run whose completion faults with `AcceptanceGateEngineException` and assert
  the tick completes with both members held or escalated.

Every run below used the repository managed runner, one invocation per line:

```
scripts\Invoke-TestSummary.ps1 -Target tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter "<filter>"
```

## RED 1 — start-time check removed

Mutation in `src/Mcg.AgentOrchestrator.Infrastructure/Processes/WorkerProcessJobs.cs`, inside
`IsDescendantOf(int, int, ProcessAncestryLookup)`; the parent hop keeps its liveness check and loses
only the start-time ordering check:

```diff
-            if (!readAncestryFacts(parentProcessId, out var parent) ||
-                parent.StartTimeUtc > child.StartTimeUtc)
+            if (!readAncestryFacts(parentProcessId, out var parent))
             {
                 return false;
             }
```

Invocation: `-Filter "DisplayName~AncestryWalk"` (2026-09-15T14:05Z).
Result: total=4 passed=3 failed=1, exit code 2.

```
FAIL  WorkerProcessJobs_ancestry_walk_refuses_recycled_dead_ancestor_pid
      Assert.False() Failure
      Expected: False
      Actual:   True
      at WorkerProcessJobsTests.WorkerProcessJobsAncestryWalkRefusesRecycledDeadAncestorPid()
```

Liveness alone does not settle the hop: the recycled pid names a live process, so without the
start-time comparison the walk credits it as the protected process's ancestor and the registration
guard refuses the freshly spawned child. This is the exact 2026-09-15 production false positive.

## RED 2 — tick-level catch removed

Mutation in `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs`, on the `catch` that
wraps `driver.RunAcceptanceCohort`. The clause is disabled by a filter it can never satisfy, which is
the compile-safe way to remove it (deleting the clause outright leaves `cohortRun` unassigned):

```diff
-                catch (Exception cohortGateException)
+                catch (Exception cohortGateException) when (cohortGateException is OutOfMemoryException)
                 {
```

Invocation: `-Filter "FullyQualifiedName~ConductorBatchLoopTestsParallelAcceptanceCohorts"`
(2026-09-15T14:07Z). Result: total=6 passed=3 failed=3, exit code 2.

```
FAIL  CohortGateFault_HoldsBothMembersAndKeepsTheTickAlive
      Assert.Null() Failure: Value is not null
FAIL  CohortGateFault_EscalatesBothMembersOnlyAtTheTransientFailureCap
      Assert.Null() Failure: Value is not null
FAIL  CohortGateFault_NonTransientFaultEscalatesBothMembersWithoutEndingTheTick
      Assert.Null() Failure: Value is not null
```

The non-null value is the exception the loop threw: the stack ran out of
`ConductorBatchLoop.cs:line 982` — the tick boundary — which is how the two daemons died.

## GREEN — after reverting both mutations

Both mutations reverted to the landed source, then re-run:

- `-Filter "FullyQualifiedName~ConductorBatchLoopTestsParallelAcceptanceCohorts"` (2026-09-15T14:08Z):
  total=6 passed=6 failed=0, ALL GREEN.
- `-Filter "FullyQualifiedName~WorkerProcessJobsTests"` (2026-09-15T14:09Z): total=67 passed=67
  failed=0, ALL GREEN. This class filter includes all four ancestry-walk tests, the existing
  live-ancestor kill refusal, and the rest of the registration guard.

Each RED is paired with the GREEN above it: removing the start-time comparison must fail the dead-pid
fact, and removing the tick-level catch must fail the faulted-cohort facts. A successful build proves
compilation only and is not a behavioral receipt.
