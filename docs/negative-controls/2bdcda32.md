# Negative-control receipt: cohort gate faults and recycled-pid ancestry

Two facts are protected here:

- the **dead-pid fact** — `WorkerProcessJobs_ancestry_walk_refuses_recycled_dead_ancestor_pid`
  (`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerProcessJobsTests.cs`), which drives the
  ancestry walk through an injected parent-and-start-time lookup whose chain ends at a pid Windows has
  recycled, and asserts the candidate holding that pid is not an ancestor of the protected process.
- the **faulted-cohort fact**
  (`tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsParallelAcceptanceCohorts.cs`),
  which has two arrivals for the same `AcceptanceGateEngineException` and one guard per arrival:
  - the **synchronous arrival** — `CohortGateFault_HoldsBothMembersAndKeepsTheTickAlive`,
    `CohortGateFault_EscalatesBothMembersOnlyAtTheTransientFailureCap` and
    `CohortGateFault_NonTransientFaultEscalatesBothMembersWithoutEndingTheTick`, where the cohort run
    throws at the call site and the tick-level `catch` contains it.
  - the **background arrival** — `FaultedBackgroundCohortCompletion_IsResolvedInsideTheTickInsteadOfEndingIt`,
    `FaultedBackgroundCohortCompletion_EscalatesBothMembersOnlyAtTheTransientFailureCap` and
    `FaultedBackgroundCohortCompletion_ReturnsTypedFaultInsteadOfThrowing`, where a completed cohort gate
    run whose completion carries the exception is published on the driver first (exactly as the background
    gate thread's `SetException` leaves it) and the tick must reach it through
    `ConductorDriver.RunAcceptanceCohortForTick` returning a typed `ConductorAcceptanceCohortGateFault`.
    This is the arrival that killed the two daemons on 2026-09-15.

  Each arrival therefore has its own RED below: the tick-level `catch` protects only the synchronous
  arrival, and the driver's typed fault protects only the background one.

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

Invocation: `-Filter "DisplayName~AncestryWalk"` (2026-09-15T14:05Z; `WorkerProcessJobs.cs` and
`WorkerProcessJobsTests.cs` are byte-identical to that run, so this RED was not repeated).
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
wraps `driver.RunAcceptanceCohortForTick`. The clause is disabled by a filter it can never satisfy, which
is the compile-safe way to remove it (deleting the clause outright leaves `cohortRun` unassigned):

```diff
-                catch (Exception cohortGateException)
+                catch (Exception cohortGateException) when (cohortGateException is OutOfMemoryException)
                 {
```

Invocation: `-Filter "FullyQualifiedName~ConductorBatchLoopTestsParallelAcceptanceCohorts"`
(2026-09-15T15:09Z). Result: total=8 passed=5 failed=3, exit code 2.

```
FAIL  CohortGateFault_HoldsBothMembersAndKeepsTheTickAlive
FAIL  CohortGateFault_EscalatesBothMembersOnlyAtTheTransientFailureCap
FAIL  CohortGateFault_NonTransientFaultEscalatesBothMembersWithoutEndingTheTick
      Assert.Null() Failure: Value is not null
      Expected: null
      Actual:   Mcg.AgentOrchestrator.Infrastructure.AcceptanceGateEngineException: Acceptance gate
                engine fault (phase=check-execution; target=focused cohort tests):
                InvalidOperationException: worker-process-registration-failed pid=38220
                stage=protected-process-boundary cleanup=refused-protected-process
```

The non-null value is the exception the loop threw, and the tick recorded the production epitaph:
`ACCEPTANCE_COHORT_EXIT ... outcome=exception` followed by
`LOOP_STOP tick=1 reason=unintended-exit exception=AcceptanceGateEngineException`, which is how the two
daemons died.

The three `FaultedBackgroundCohortCompletion_*` tests stayed green under this mutation, because the
background arrival never throws at the call site: its protection is the driver returning a typed fault,
which RED 3 removes.

## RED 3 — driver's typed background fault removed (with the RED 2 catch still disabled)

Mutation in `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAcceptanceCohorts.cs`, inside
`ObserveCohortGateCompletion`, restoring the pre-fix rethrow of the background completion. The RED 2
mutation is left in place, so this is exactly the pre-fix production shape:

```diff
-        if (run.Completion.Task.Exception is { } aggregate)
-        {
-            _cohortGateFaults[memberPairKey] = new ConductorAcceptanceCohortGateFault(
-                memberPairKey,
-                run.PairFingerprint,
-                aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate);
-            return;
-        }
-
+        run.Completion.Task.GetAwaiter().GetResult();
         _cohortGateFaultCounts.TryRemove(memberPairKey, out _);
```

Invocation: `-Filter "FullyQualifiedName~ConductorBatchLoopTestsParallelAcceptanceCohorts"`
(2026-09-15T15:12Z). Result: total=8 passed=2 failed=6, exit code 2 — the three background-arrival tests
now fail as well, and the failure stack is the 2026-09-15 death path verbatim:

```
FAIL  FaultedBackgroundCohortCompletion_IsResolvedInsideTheTickInsteadOfEndingIt
      Assert.Null() Failure: Value is not null
      Actual:   AcceptanceGateEngineException: ... worker-process-registration-failed pid=38220 ...
        at ConductorDriver.ObserveCohortGateCompletion(String memberPairKey, CohortGateRun run)
        at ConductorDriver.TakeCohortGateFault(ConductorAcceptanceCohortSelection selection)
        at ConductorDriver.RunAcceptanceCohortForTick(...)
        at ConductorBatchLoop.RunParallelAcceptanceBatch(...)
```

## GREEN — after reverting the mutations

RED 2 and RED 3 reverted to the landed source (RED 1's file was already back at it), then re-run:

- `-Filter "FullyQualifiedName~ConductorBatchLoopTestsParallelAcceptanceCohorts"` (2026-09-15T15:15Z):
  total=8 passed=8 failed=0, ALL GREEN.
- `-Filter "FullyQualifiedName~WorkerProcessJobsTests|FullyQualifiedName~ConductorParallelAcceptanceAttemptsTests|FullyQualifiedName~RealWorkerProcessGuardTests"`
  (2026-09-15T15:19Z): total=77 passed=77 failed=0, ALL GREEN. That filter includes all four
  ancestry-walk tests, the existing live-ancestor kill refusal, the rest of the registration guard, the
  registration-fault classification, and the real spawned-process guard.

Each RED is paired with the GREEN above it: removing the start-time comparison must fail the dead-pid
fact, removing the tick-level catch must fail the synchronous arrival of the faulted-cohort fact, and
removing the driver's typed background fault must fail its background arrival. A successful build proves
compilation only and is not a behavioral receipt.
