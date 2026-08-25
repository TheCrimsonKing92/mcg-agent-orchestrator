using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed partial class ConductorDriverTestsAcceptanceCoordination
{
    [Xunit.Fact]
    public void InlineLanding_ViolatingAuthority_SkipsVerifier()
    {
        var executionDirectory = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SimpleGoal("Inline landing source-size preflight");
            PassVerification(kernel, goal, goal.Tasks.Single());
            var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
            Directory.CreateDirectory(worktreePath);
            WriteSourceSizeAuthority(worktreePath, maximumLineCount: 2, actualLineCount: 3);
            var verifierRan = false;
            var driver = MakeAcceptanceDriver(
                runAcceptanceVerification: _ =>
                {
                    verifierRan = true;
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
                executionDirectory: executionDirectory);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.False(verifierRan);
            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    [Xunit.Fact]
    public void InlineLanding_CompliantAuthority_ProceedsToVerifier()
    {
        var executionDirectory = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SimpleGoal("Inline landing compliant source-size preflight");
            PassVerification(kernel, goal, goal.Tasks.Single());
            var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
            Directory.CreateDirectory(worktreePath);
            WriteSourceSizeAuthority(worktreePath, maximumLineCount: 3, actualLineCount: 3);
            var verifierRan = false;
            var driver = MakeAcceptanceDriver(
                runAcceptanceVerification: _ =>
                {
                    verifierRan = true;
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                executionDirectory: executionDirectory);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.True(verifierRan);
            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ConductorDriverNoTickLandingUsesLeasedSharedAttempt()
    {
        var attemptRoot = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("No-tick landing stable slot");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var stableSlotLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
        int? capturedSlotIndex = null;
        DotnetBuildEnvironmentLease? capturedLease = null;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            runInline: true,
            acquireStableSlotLease: (_, _) => stableSlotLease);
        var driver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, slotIndex, lease, _) =>
            {
                capturedSlotIndex = slotIndex;
                capturedLease = lease;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(0, capturedSlotIndex);
        Assert.Same(stableSlotLease, capturedLease);
        var attempt = ReadOnlyAttempt(attemptRoot, goal);
        Assert.StartsWith($"{goal.Id.Value[..8]}-0-", attempt.AttemptId, StringComparison.Ordinal);
        Assert.Equal(AcceptanceStableSlotExhaustionPolicy.DegradeToSerial, attempt.StableSlotExhaustionPolicy);
        Assert.NotNull(attempt.ReconciledAt);
        using var reacquiredLease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireStableSlotExecutionLock(0, TimeSpan.Zero)).Lease;
    }

    [Xunit.Fact]
    public void ConductorDriverNoTickLandingSelfReconcilesAndEmitsLifecycleEvents()
    {
        var attemptRoot = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("No-tick landing lifecycle");
        PassVerification(kernel, goal, goal.Tasks.Single());
        ConductorParallelAcceptanceOwnedProcessLaunch? ownedLaunch = null;
        var launched = false;
        var events = new List<(string GoalId, string Detail)>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                ownedLaunch = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9701);
            },
            acquireStableSlotLease: (_, _) => null);
        var driver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) =>
                AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator,
            acceptanceEventSink: (goalId, detail) => events.Add((goalId, detail)),
            noTickAcceptancePollDelay: _ =>
            {
                if (!launched)
                {
                    launched = true;
                    ownedLaunch!.ExecuteInCurrentProcess(9701);
                }
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var attempt = ReadOnlyAttempt(attemptRoot, goal);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, attempt.Outcome);
        Assert.NotNull(attempt.ReconciledAt);
        Assert.Collection(
            events,
            startedEvent =>
            {
                var (goalId, started) = startedEvent;
                Assert.Equal(goal.Id.Value[..8], goalId);
                Assert.StartsWith("ACCEPTANCE ", started, StringComparison.Ordinal);
                Assert.Contains($"result=started attempt={attempt.AttemptId} tick=0", started, StringComparison.Ordinal);
            },
            terminalEvent =>
            {
                var (goalId, terminal) = terminalEvent;
                Assert.Equal(goal.Id.Value[..8], goalId);
                Assert.StartsWith("ACCEPTANCE ", terminal, StringComparison.Ordinal);
                Assert.Contains($"result=passed attempt={attempt.AttemptId} tick=0", terminal, StringComparison.Ordinal);
            });
    }

    [Xunit.Fact]
    public void ConductorDriverNoTickLandingDegradesAfterInjectedSlotExhaustion()
    {
        var attemptRoot = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("No-tick landing bounded degradation");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var verifierRan = false;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            runInline: true,
            acquireStableSlotLease: (_, _) => throw new DotnetBuildSlotsBusyException(
                new DotnetBuildLeaseAcquisition.SlotsBusy(
                    "injected-bounded-exhaustion",
                    [new DotnetBuildStableSlotWait(0, 4242)])));
        var driver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, slotIndex, lease, _) =>
            {
                verifierRan = true;
                Assert.Null(slotIndex);
                Assert.Null(lease);
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(verifierRan);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var attempt = ReadOnlyAttempt(attemptRoot, goal);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, attempt.Outcome);
        Assert.Contains(
            attempt.LeaseReceipts ?? [],
            receipt => receipt.StartsWith("ACCEPTANCE_LEASE_DEGRADE ", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ConductorDriverNoTickLandingReturnsFailedRecordedVerdict()
    {
        var attemptRoot = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("No-tick landing failed verdict");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            runInline: true,
            acquireStableSlotLease: (_, _) => null);
        var driver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) => AcceptanceVerificationSummary.Failed,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        var attempt = ReadOnlyAttempt(attemptRoot, goal);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, attempt.Outcome);
        Assert.NotNull(attempt.ReconciledAt);
    }

    [Xunit.Fact]
    public void ConductorDriverNoTickLandingStopsPollingAtInjectedDeadline()
    {
        var attemptRoot = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("No-tick landing bounded poll");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var now = DateTimeOffset.Parse("2026-08-24T00:00:00Z");
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(9801));
        var driver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) =>
                throw new InvalidOperationException("The held owned process must not run in this test."),
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator,
            noTickAcceptancePollDelay: delay => now += delay,
            noTickAcceptancePollTimeout: TimeSpan.FromMilliseconds(200),
            utcNow: () => now);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("bounded no-tick wait", held.Reason, StringComparison.Ordinal);
        var attempt = ReadOnlyAttempt(attemptRoot, goal);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, attempt.Outcome);
        Assert.Null(attempt.ReconciledAt);
    }

    [Xunit.Fact]
    public void ConductorDriverNoTickLandingDegradesAfterInjectedBuildLockBlock()
    {
        var attemptRoot = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("No-tick landing build-lock degradation");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var verifierRan = false;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            runInline: true,
            acquireStableSlotLease: (_, _) => throw new BuildLockBlockedException(
                new BuildLockAttribution("locked.dll", [], "injected-build-lock")));
        var driver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, slotIndex, lease, _) =>
            {
                verifierRan = true;
                Assert.Null(slotIndex);
                Assert.Null(lease);
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(verifierRan);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var attempt = ReadOnlyAttempt(attemptRoot, goal);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, attempt.Outcome);
        Assert.Contains(
            attempt.LeaseReceipts ?? [],
            receipt => receipt.StartsWith("ACCEPTANCE_LEASE_DEGRADE ", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ConductorDriverNoTickLandingRetriesTransientUnreadableAttemptMetadata()
    {
        var attemptRoot = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("No-tick landing transient metadata read");
        PassVerification(kernel, goal, goal.Tasks.Single());
        ConductorParallelAcceptanceOwnedProcessLaunch? ownedLaunch = null;
        string? metadataPath = null;
        string? backupPath = null;
        var delayCount = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                ownedLaunch = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9802);
            },
            acquireStableSlotLease: (_, _) => null);
        var driver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) =>
                AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator,
            noTickAcceptancePollDelay: _ =>
            {
                delayCount++;
                if (delayCount == 1)
                {
                    metadataPath = Assert.Single(Directory.EnumerateFiles(
                        Path.Combine(attemptRoot, goal.Id.Value),
                        "*.attempt.json"));
                    backupPath = metadataPath + ".transient";
                    File.Move(metadataPath, backupPath);
                }
                else if (delayCount == 2)
                {
                    File.Move(backupPath!, metadataPath!);
                    ownedLaunch!.ExecuteInCurrentProcess(9802);
                }
            });
        ConductorAdvanceResult? result = null;

        var exception = Record.Exception(() =>
            result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result!.Outcome);
        Assert.Equal(2, delayCount);
        Assert.NotNull(ReadOnlyAttempt(attemptRoot, goal).ReconciledAt);
    }

    [Xunit.Fact]
    public void ConductorDriverNoTickLandingReturnsVerdictWhenLifecycleSinkFails()
    {
        var attemptRoot = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("No-tick landing event sink failure");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            runInline: true,
            acquireStableSlotLease: (_, _) => null);
        var driver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) =>
                AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator,
            acceptanceEventSink: (_, detail) =>
            {
                if (detail.Contains("result=passed", StringComparison.Ordinal))
                {
                    throw new IOException("Injected acceptance event sink failure.");
                }
            });
        ConductorAdvanceResult? result = null;

        var exception = Record.Exception(() =>
            result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));

        Assert.Null(exception);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result!.Outcome);
        Assert.NotNull(ReadOnlyAttempt(attemptRoot, goal).ReconciledAt);
    }

    [Xunit.Fact]
    public void ConductorDriverNoTickAdvanceDoesNotAdoptLoopOwnedAttempt()
    {
        var attemptRoot = CreateTempDirectory();
        var (kernel, goal) = SimpleGoal("No-tick caller does not adopt loop acceptance");
        PassVerification(kernel, goal, goal.Tasks.Single());
        ConductorParallelAcceptanceOwnedProcessLaunch? ownedLaunch = null;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                ownedLaunch = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9803);
            },
            acquireStableSlotLease: (_, _) => null);
        var tickDriver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) =>
                AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator);
        tickDriver.BeginTick(kernel, 1);
        var started = tickDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
        var noTickDriver = MakeAcceptanceDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceVerificationWithLease: (_, _, _, _) =>
                AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            parallelAcceptanceAttemptCoordinator: coordinator,
            noTickAcceptancePollDelay: _ => ownedLaunch!.ExecuteInCurrentProcess(9803));

        var result = noTickDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Held>(started.Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalStatus.Verifying, goal.Status);
        Assert.Null(ReadOnlyAttempt(attemptRoot, goal).ReconciledAt);
    }

    private static ConductorDriver MakeAcceptanceDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<Goal, int?, DotnetBuildEnvironmentLease?, CancellationToken, AcceptanceVerificationSummary>?
            runAcceptanceVerificationWithLease = null,
        Func<Goal, ChangeRiskTier?>? classifyRisk = null,
        ConductorParallelAcceptanceAttemptCoordinator? parallelAcceptanceAttemptCoordinator = null,
        Action<string, string>? acceptanceEventSink = null,
        Action<TimeSpan>? noTickAcceptancePollDelay = null,
        TimeSpan? noTickAcceptancePollTimeout = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<Goal, AcceptanceVerificationSummary>? runAcceptanceVerification = null,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask = null,
        string? executionDirectory = null) =>
        new(
            getFacts ?? (_ => GoalLifecycleFacts.None),
            () => 0,
            _ => "/tmp/workspace",
            _ => DispatchStartOutcome.Started(),
            null,
            null,
            runAcceptanceVerification ?? (_ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria),
            null,
            retryTask,
            null,
            (_, _, _) => 0,
            null,
            _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                "goal/test",
                "Already fast-forwardable",
                [],
                null),
            (goal, _) => new LandingResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed"),
            null,
            _ => { },
            _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
            (_, _, _) => { },
            classifyRisk ?? (_ => null),
            runAcceptanceVerificationWithLease: runAcceptanceVerificationWithLease,
            parallelAcceptanceAttemptCoordinator: parallelAcceptanceAttemptCoordinator,
            acceptanceEventSink: acceptanceEventSink,
            noTickAcceptancePollDelay: noTickAcceptancePollDelay,
            noTickAcceptancePollTimeout: noTickAcceptancePollTimeout,
            utcNow: utcNow,
            executionDirectory: executionDirectory);

    private static void WriteSourceSizeAuthority(
        string root,
        int maximumLineCount,
        int actualLineCount)
    {
        var authorityPath = Path.Combine(
            root,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
        File.WriteAllText(
            authorityPath,
            $"new SourceSizeCeiling(\"guarded.cs\", {maximumLineCount})");
        File.WriteAllLines(
            Path.Combine(root, "guarded.cs"),
            Enumerable.Repeat("line", actualLineCount));
    }

    private static ConductorParallelAcceptanceAttempt ReadOnlyAttempt(string attemptRoot, Goal goal)
    {
        var path = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(attemptRoot, goal.Id.Value),
            "*.attempt.json"));
        return JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
