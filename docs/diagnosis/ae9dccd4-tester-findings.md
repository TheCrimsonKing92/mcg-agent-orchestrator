# Tester findings, preserved — the dispatch was discarded on output FORMAT, not on content

A Tester round produced ten findings against this candidate and its dispatch was rejected with
`reason=verification-pattern-unmatched` because it wrote `tests: fail`. The findings are correct and are
reproduced here so they are not lost. Backlog `0699a50d` tracks the rejection defect; it is not your
concern.

Evidence base: focused run at HEAD `6dedac949c5a`, `AcceptanceCohortWorkflowTests` total 27, failed 1,
succeeded 26.

## BLOCKING — fix these

**1. The train silently disables the existing pair-cohort path.**
`ConductorBatchLoop.RunParallelAcceptanceBatch`. Production `ConductorDriver` always constructs
`MergeTrainAcceptanceStore`, so `MergeTrainsEnabled` is on. The loop admits a train at `MinimumMembers=2`
and then skips the pair via `if (!trainAttempted)`. Proven by
`ProductionBatch_SelectsRunsPersistsLandsAndCleansOneSharedCohort` failing at line 1281,
`ReadOvertakeCount` Expected 0 Actual 1, because the cohort `onGateAdmitted` never ran.
The brief said build the train BESIDE the pair cohort, not replace it. This replaces it by default.

**2. Materialization faults escape and stop the conductor loop.**
`ConductorDriver.MergeTrains.RunMergeTrain`. `CreateMergeTrainWorkspace` throws
`InvalidOperationException` on stale main, stale member, or `worktree add` failure. `RunMergeTrain` does not
catch these and `ConductorBatchLoop.cs:2824` has no `try` around the train, so a fault stops the loop at
`reason=unintended-exit`. The pair cohort returns typed fallbacks at `ConductorDriver.cs:3211`, `:3216`,
`:3230` — match that shape. `MergeTrainEjectionReason.StaleBinding` is declared and never assigned, which is
the fallback this path is missing.

**3. Landing has no prepared-recovery path.**
`MergeTrainAcceptanceStore.FinalizeLanding` has no `RecoverPreparedLandings`. A crash between the main CAS
at `LandingExecutor.MergeTrains.cs:139` and the per-member Completed/backlog close at
`ConductorDriver.MergeTrains.cs:139` leaves members on main WITHOUT integrate records.
`CohortAcceptanceStore.RecoverPreparedLandings` at line 406 is the model to follow.
This is not hypothetical: that exact state occurred manually on 2026-08-20 and required `goal-mark-landed`
to reconcile a goal whose work was on main but whose record was orphaned.

**4. Criterion 1 has no test.** `ConductorMergeTrainTests` holds only three selector Facts. No test drives
`RunMergeTrain` and asserts one verifier invocation against three landings.
`ExactTestedMergeTrainCommit_LandsThreeGoalsWithPerGoalCoverage` passes but uses a hand-built `Passed`
receipt and never calls a verifier. No `*Tests*.cs` calls `RunMergeTrain` at all.

**5. Criterion 2 has no test.** The drop-newest loop at `ConductorDriver.MergeTrains.cs:169-185` is
untested. Nothing drives a last-member RED, asserts two attempt receipts, asserts remaining members landed,
or asserts the dropped goal is absent from `MemberResults` so its later solo gate names its own landing
paths.

**6. Dispose leaks without a cleanup record.**
`GoalWorktrees.MergeTrains.MergeTrainWorkspace.Dispose` throws when worktree removal fails and records no
cleanup marker, unlike `AcceptanceCohortWorkspace.Dispose` which calls `RecordAcceptanceCohortCleanupNeeded`
before rethrowing. Dispose runs on the return path after a SUCCESSFUL landing, so a Windows file lock can
stop the loop after the work succeeded.

## ADVISORY — record a decision, fix if cheap

**7.** `RunMergeTrain` is called at `ConductorBatchLoop.cs:2824` without `onGateAdmitted` and without
`ReadSuppressedCohortPairs`, so overtakes recorded before the tick are not reset. Same TRX failure at line
1281.

**8.** `MergeTrainAcceptanceStore.FinalizeLanding` fabricates `MergeTrainCoverage` rows in memory with
`Landed` hardcoded true; there is no persisted per-member coverage table.

**9.** `ConductorMergeTrainSelector.Select` skips only non-Ready and Overlaps. It omits
`DocsTreeOnlyCandidate`, `MainRevisionMismatch`, and the suppressed-pair guards used at
`ConductorAcceptanceCohorts.cs:150`.

**10.** The train lands without re-projecting members after the gate. The cohort re-projects and holds on
binding change at `ConductorDriver.cs:3406`. Branch-sha checks at `ExecuteMergeTrain:62` still hold content
identity, so the exposure is a risk or disposition change during the gate window.

## Note

Findings 1, 2, and 6 are all loop-availability risks — two stop the conductor outright. Prioritise those
over the test-coverage gaps, then close criteria 1 and 2 with real `RunMergeTrain` tests.
