# Negative control: refused retry admission reload

The focused command was:

`.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -NoBuild -Filter 'DisplayName~ConductorDriverRepeatedRefusedStartStaysSetAsideAfterFirstRetryAndDispatch'`

Before the reservation transaction used `AgentOrchestratorKernel.RetryTask`, the real SQLite reload fixture failed at the TaskRetried count with `Expected: 1` and `Actual: 0`. After the retry event was persisted but before the prepared dispatch was persisted in the same transaction, the fixture failed at the start-attempt count with `Expected: 1` and `Actual: 2`.

With both durable writes restored, the same command passed one test. This was the predecessor contract; the denied-path correction below supersedes persistence of the prepared dispatch when admission refuses the start.

## Durable-field set-aside control after denied admission

The control replaced `BuildEscalatedGoalStateFingerprint`'s durable-field result with an intentionally unstable value, rebuilt the worktree projects, and ran:

`.\scripts\Invoke-TestSummary.ps1 -Target tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj -NoBuild -RunnerPath tests/Mcg.AgentOrchestrator.Infrastructure.Tests/bin/Debug/net10.0/Mcg.AgentOrchestrator.Infrastructure.Tests.exe -Filter 'FullyQualifiedName~ConductorDriverTests' -TestHostTimeoutSeconds 120`

RED executed 309 tests (308 passed, 1 failed). `ConductorDriver_repeated_refused_start_stays_set_aside_after_first_retry_and_dispatch` re-admitted tick 2 and failed at the start-attempt assertion with `Assert.Equal() Failure: Values differ`, `Expected: 1`, `Actual: 2`. The durable fingerprint was then restored. The explicit worktree runner path is part of the receipt because the default no-build reusable artifact remained stale and passed even a temporary guaranteed-failure plumbing probe.

GREEN after restoring the durable fingerprint and marker-only denied path executed the same 309 tests: 309 passed, 0 failed.
