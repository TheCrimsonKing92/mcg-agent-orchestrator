# Negative control: refused retry admission reload

The focused command was:

`.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -NoBuild -Filter 'DisplayName~ConductorDriverRepeatedRefusedStartStaysSetAsideAfterFirstRetryAndDispatch'`

Before the reservation transaction used `AgentOrchestratorKernel.RetryTask`, the real SQLite reload fixture failed at the TaskRetried count with `Expected: 1` and `Actual: 0`. After the retry event was persisted but before the prepared dispatch was persisted in the same transaction, the fixture failed at the start-attempt count with `Expected: 1` and `Actual: 2`.

With both durable writes restored, the same command passes one test. This demonstrates that the regression test depends on preserving the failed-branch retry and prepared dispatch across the admission snapshot reload.
