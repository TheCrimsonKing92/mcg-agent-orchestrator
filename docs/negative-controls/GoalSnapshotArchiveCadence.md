# Negative control: daily goal snapshot archive

Acceptance lane executes this procedure in a disposable checkout of the candidate. Workers only author it.
Use `scripts/Invoke-WorkerBuildCheck.ps1` for builds and the managed runner below for tests; preserve the candidate cadence file before the mutation.

| Step | Change / command | Expected evidence |
| --- | --- | --- |
| RED mutation | Restore `src/Mcg.AgentOrchestrator.App/Orchestration/RunEventMaintenanceCadence.cs` from `main` (`git restore --source=main --worktree -- <path>`). Reapply **only** the trailing optional `Func<DateTimeOffset, int>? goalSnapshotArchiveOperation = null` parameter, leaving it unused, so the new tests compile against the reverted behavior. | No production archive call or archive receipt remains. Keep the new helper and tests. |
| RED run | Build `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj`, then run `scripts/Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -NoBuild -Filter 'FullyQualifiedName~GoalSnapshotArchiveCadenceTests'`. | `DueWorkspace_Run_ArchivesOnceAfterSweepReceipt`, `ArchiveThrows_PreservesCadenceSweepReceiptAndDailySchedule`, and `FailedSweep_DueWorkspace_StillInvokesArchive` fail with an expected callback count of 1 and actual 0. `RealStore_InjectedPastClock_ArchivesOnlyOldCompletedTerminalGoal` fails because the archive table is empty and no hot row is stubbed. Record these assertion messages; compilation failure is not a valid control. Unit, no-workspace and fresh-window cases can remain green. |
| GREEN restore | Restore the preserved candidate cadence file, rebuild the same test project, and rerun the same `FullyQualifiedName~GoalSnapshotArchiveCadenceTests` filter. | Every selected test passes; record pass/fail counts and the candidate identity. |
