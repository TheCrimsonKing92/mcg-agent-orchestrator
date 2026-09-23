using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsMaxDurationDeferral(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact(Timeout = 30_000)]
    public async Task MaxDurationBoundary_WithInFlightAttempt_DefersUntilOutcomeAndSuppressesNewWork()
    {
        var time = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(time);
        var root = CreateTempDirectory("mcg-max-duration-deferral");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var running = CreateVerifiedSimpleGoal(kernel, "Update src/RunningAtMaxDuration.cs");
            var paidWorkerStarts = 0;
            var sleepCount = 0;
            string? attemptId = null;
            Goal? waitingAcceptance = null;
            var driver = MakeAcceptanceDriver(fixture, () => paidWorkerStarts++);
            var eventPath = Path.Combine(root, "conduct-events.log");
            BatchLoopSummary? summary = null;

            var outputText = CaptureConsole(() => summary = new ConductorBatchLoop(
                handoffOnMaxDuration: _ => ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log"),
                conductEventLogWriter: new ConductEventLogWriter(eventPath),
                utcNow: time.GetUtcNow).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                Path.Combine(root, ConductorBatchLoop.StopFileName),
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _interval =>
                {
                    sleepCount++;
                    if (sleepCount == 1)
                    {
                        attemptId = fixture.RequiredHandleForTests(running).Attempt.AttemptId;
                        waitingAcceptance = CreateVerifiedSimpleGoal(kernel, "Update src/WaitingAtMaxDuration.cs");
                        _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                            kernel,
                            DefaultAgents(),
                            "Dispatch paid work after max duration");
                        time.AdvanceForTests(TimeSpan.FromSeconds(2));
                    }
                    else if (sleepCount == 2)
                    {
                        fixture.RequiredHandleForTests(running).CompleteForTests();
                        time.AdvanceForTests(TimeSpan.FromSeconds(1));
                    }
                    else
                    {
                        throw new InvalidOperationException("Max-duration deferral exceeded its deterministic tick budget.");
                    }

                    return false;
                },
                maxDuration: TimeSpan.FromSeconds(1),
                keepAliveWhenIdle: true,
                maxDurationDeferralCeiling: TimeSpan.FromSeconds(30)));

            Assert.NotNull(attemptId);
            Assert.True(summary!.Handoff?.Started);
            Assert.Equal(GoalStatus.Verified, running.Status);
            Assert.Equal(0, paidWorkerStarts);
            Assert.Empty(fixture.AttemptCoordinator.GetUnreconciledAttempts([waitingAcceptance!.Id.Value]));
            var lines = outputText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(lines, line =>
                line.StartsWith($"ACCEPTANCE goal={running.Id.Value[..8]} ", StringComparison.Ordinal) &&
                line.Contains("result=passed", StringComparison.Ordinal) &&
                line.Contains($"attempt={attemptId}", StringComparison.Ordinal));
            var deferred = Assert.Single(lines, line => line.StartsWith("LOOP_STOP_DEFERRED ", StringComparison.Ordinal));
            Assert.Contains(attemptId, deferred, StringComparison.Ordinal);
            Assert.Contains("attempt_elapsed_ms=", deferred, StringComparison.Ordinal);
            var stopped = Assert.Single(lines, line => line.StartsWith("LOOP_STOP ", StringComparison.Ordinal));
            Assert.Contains("reason=max-duration", stopped, StringComparison.Ordinal);
            Assert.DoesNotContain("deferralExpired=true", stopped, StringComparison.Ordinal);
            Assert.Contains(attemptId, File.ReadAllText(eventPath), StringComparison.Ordinal);
            Assert.Contains("loop-stop-deferred", File.ReadAllText(eventPath), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    public async Task MaxDurationDeferral_FailedTerminalAttempt_DoesNotStartSerialAcceptanceRetry()
    {
        var time = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(
            time,
            AcceptanceVerificationSummary.Failed);
        var root = CreateTempDirectory("mcg-max-duration-failed-drain");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var running = CreateVerifiedSimpleGoal(kernel, "Update src/FailedAtMaxDuration.cs");
            var sleepCount = 0;
            var driver = MakeAcceptanceDriver(fixture, () => { });
            BatchLoopSummary? summary = null;

            var outputText = CaptureConsole(() => summary = new ConductorBatchLoop(
                handoffOnMaxDuration: _ => ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log"),
                utcNow: time.GetUtcNow).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                Path.Combine(root, ConductorBatchLoop.StopFileName),
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ =>
                {
                    sleepCount++;
                    if (sleepCount == 1)
                    {
                        time.AdvanceForTests(TimeSpan.FromSeconds(2));
                    }
                    else if (sleepCount == 2)
                    {
                        fixture.RequiredHandleForTests(running).CompleteForTests();
                        time.AdvanceForTests(TimeSpan.FromSeconds(1));
                    }
                    else if (sleepCount <= 4)
                    {
                        time.AdvanceForTests(TimeSpan.FromSeconds(1));
                    }
                    else
                    {
                        throw new InvalidOperationException("Failed-attempt deferral exceeded its deterministic tick budget.");
                    }

                    return false;
                },
                maxIterations: 4,
                maxDuration: TimeSpan.FromSeconds(1),
                keepAliveWhenIdle: true,
                maxDurationDeferralCeiling: TimeSpan.FromSeconds(30)));

            Assert.Equal(0, summary!.Retried);
            Assert.Equal(0, fixture.HeldAttemptCount);
            Assert.DoesNotContain("result=retry", outputText, StringComparison.Ordinal);
            Assert.Contains(kernel.GetGoal(running.Id).Timeline, item =>
                item.Message.Contains("Acceptance verification failed", StringComparison.Ordinal));
            var stopped = Assert.Single(
                outputText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
                line => line.StartsWith("LOOP_STOP ", StringComparison.Ordinal));
            Assert.Contains("reason=max-duration", stopped, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    public async Task MaxDurationBoundary_WhenAcceptanceSnapshotIsUnreadable_DefersInsteadOfStopping()
    {
        var time = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(time);
        var root = CreateTempDirectory("mcg-max-duration-unreadable-snapshot");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var running = CreateVerifiedSimpleGoal(kernel, "Update src/UnreadableAtMaxDuration.cs");
            var sleepCount = 0;
            var metadataRestored = false;
            string? metadataPath = null;
            string? validMetadata = null;

            var outputText = CaptureConsole(() => new ConductorBatchLoop(
                handoffOnMaxDuration: _ => ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log"),
                utcNow: time.GetUtcNow).Run(
                kernel,
                MakeAcceptanceDriver(fixture, () => { }),
                ConductorAutonomyPolicy.Conservative,
                Path.Combine(root, ConductorBatchLoop.StopFileName),
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ =>
                {
                    sleepCount++;
                    if (sleepCount == 1)
                    {
                        var handle = fixture.RequiredHandleForTests(running);
                        metadataPath = handle.Attempt.MetadataPath;
                        validMetadata = File.ReadAllText(metadataPath);
                        File.WriteAllText(metadataPath, "not-json");
                        time.AdvanceForTests(TimeSpan.FromSeconds(2));
                    }
                    else if (sleepCount == 2)
                    {
                        time.AdvanceForTests(TimeSpan.FromSeconds(1));
                    }
                    else
                    {
                        throw new InvalidOperationException("Unreadable-snapshot deferral exceeded its deterministic tick budget.");
                    }

                    return false;
                },
                maxDuration: TimeSpan.FromSeconds(1),
                keepAliveWhenIdle: true,
                maxDurationDeferralCeiling: TimeSpan.FromSeconds(30),
                onMaxDurationDeferralStateChanged: isDeferred =>
                {
                    if (isDeferred && !metadataRestored)
                    {
                        File.WriteAllText(metadataPath!, validMetadata!);
                        fixture.RequiredHandleForTests(running).CompleteForTests();
                        metadataRestored = true;
                    }
                }));

            Assert.True(metadataRestored);
            Assert.Contains("LOOP_STOP_DEFERRED ", outputText, StringComparison.Ordinal);
            Assert.Contains("inflightStateUnavailable=true", outputText, StringComparison.Ordinal);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, ReadAttempt(metadataPath!).Outcome);
            Assert.Contains("LOOP_STOP ", outputText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    public void MaxDurationBoundary_WithoutInFlightAttempt_StopsWithoutDeferral()
    {
        var (kernel, _) = SimpleGoal();
        BatchLoopSummary? summary = null;

        var outputText = CaptureConsole(() => summary = new ConductorBatchLoop(
            handoffOnMaxDuration: _ => ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log")).Run(
            kernel,
            MakeDriver(),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxDuration: TimeSpan.Zero,
            maxDurationDeferralCeiling: TimeSpan.FromSeconds(1)));

        Assert.True(summary!.Handoff?.Started);
        Assert.DoesNotContain("LOOP_STOP_DEFERRED ", outputText, StringComparison.Ordinal);
        Assert.Contains("LOOP_STOP ", outputText, StringComparison.Ordinal);
    }

    [Xunit.Fact(Timeout = 30_000)]
    public async Task MaxDurationDeferral_CeilingExpires_StopsAndNamesAttempt()
    {
        var time = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(time);
        var root = CreateTempDirectory("mcg-max-duration-expiry");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var running = CreateVerifiedSimpleGoal(kernel, "Update src/ExpiryAtMaxDuration.cs");
            var sleepCount = 0;
            string? attemptId = null;
            BatchLoopSummary? summary = null;

            var outputText = CaptureConsole(() => summary = new ConductorBatchLoop(
                handoffOnMaxDuration: _ => ConductorLoopHandoffResult.StartedProcess(1234, "out.log", "err.log"),
                utcNow: time.GetUtcNow).Run(
                kernel,
                MakeAcceptanceDriver(fixture, () => { }),
                ConductorAutonomyPolicy.Conservative,
                Path.Combine(root, ConductorBatchLoop.StopFileName),
                watchInterval: TimeSpan.FromSeconds(1),
                sleepFunc: _ =>
                {
                    sleepCount++;
                    if (sleepCount == 1)
                    {
                        attemptId = fixture.RequiredHandleForTests(running).Attempt.AttemptId;
                        time.AdvanceForTests(TimeSpan.FromSeconds(2));
                    }
                    else if (sleepCount == 2)
                    {
                        time.AdvanceForTests(TimeSpan.FromSeconds(4));
                    }
                    else
                    {
                        throw new InvalidOperationException("Max-duration expiry exceeded its deterministic tick budget.");
                    }

                    return false;
                },
                maxDuration: TimeSpan.FromSeconds(1),
                keepAliveWhenIdle: true,
                maxDurationDeferralCeiling: TimeSpan.FromSeconds(3)));

            Assert.NotNull(attemptId);
            Assert.True(summary!.Handoff?.Started);
            var lines = outputText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines, line => line.StartsWith("LOOP_STOP_DEFERRED ", StringComparison.Ordinal));
            var stopped = Assert.Single(lines, line => line.StartsWith("LOOP_STOP ", StringComparison.Ordinal));
            Assert.Contains("reason=max-duration", stopped, StringComparison.Ordinal);
            Assert.Contains("deferralExpired=true", stopped, StringComparison.Ordinal);
            Assert.Contains(attemptId, stopped, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static ConductorDriver MakeAcceptanceDriver(
        HoldingAcceptanceAttemptsAcrossTicksFixture fixture,
        Action paidWorkerStart) =>
        MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                paidWorkerStart();
                return DispatchStartOutcome.Started();
            },
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"],
            parallelAcceptanceAttemptCoordinator: fixture.AttemptCoordinator,
            getAcceptanceSlotCount: _ => 1);
}
