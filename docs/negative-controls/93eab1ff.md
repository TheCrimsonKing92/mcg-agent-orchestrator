# Goal 93eab1ff negative controls

## RED: transfer-lease seam removed

The mutation replaced the two scoped defaults in `CliPersistentStateRunner.AcquireGoalReplacementTransferLease`:

```csharp
timeProvider ??= GoalReplacementTransferLeaseClock.Current;
wait ??= GoalReplacementTransferLeaseClock.CurrentWait;
```

with the production defaults directly:

```csharp
timeProvider ??= TimeProvider.System;
wait ??= Thread.Sleep;
```

Command: `.\scripts\Invoke-TestSummary.ps1 -Target tests\Mcg.AgentOrchestrator.Infrastructure.Tests\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -Filter 'FullyQualifiedName~CliCommandTestsPersistentRunnerCommandsGoalIntakeAndReplacement'`.

Result: valid RED, 49 executed, 48 passed, 1 failed. `GoalReplace_CompetingReplacementTimeout_IsRetryableWithoutAudit_ThenConflictsAfterWinnerCommits` failed after 19.391 seconds at its injected-wait assertion with `Assert.Equal() Failure: Values differ`, `Expected: 1`, `Actual: 0`. The real 15-second deadline produced the retryable exception without invoking the scoped clock or wait delegate. Thus the test-controlled first branch assertion is unreachable under the mutation, while `ConcurrentGoalReplacementsCommitExactlyOneSuccessor` again lets host speed decide whether its loser observes the committed owner before timing out.

Retained RED receipt: `C:\Users\miles\AppData\Local\Temp\Low\mcg-tests\summary-20260918T083455708-b7cfed8f\summary-Mcg.AgentOrchestrator.Infrastructure.Tests-1-r1-FullyQualifiedName-CliCommandTestsPersistentRunnerCommandsGoalIntakeAndReplacement.trx`.

## GREEN: scoped clock and wait delegate restored

After restoring the two `GoalReplacementTransferLeaseClock` defaults, the identical class-filter command passed: 49 executed, 49 passed, 0 failed or skipped. This is exactly one more executed fact than the 48-fact class before this change.

Terminal receipt: final-tree run `summary-20260918T090452548-af892e1f`, exit 0, 49/49 passed. The clean-run helper removed its temporary TRX directory after retaining the run-identity receipt.
