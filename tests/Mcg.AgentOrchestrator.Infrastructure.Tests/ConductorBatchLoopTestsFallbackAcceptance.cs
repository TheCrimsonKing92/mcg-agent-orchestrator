using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsFallbackAcceptance : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsFallbackAcceptance(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact]
    public void FallbackGate_TickScopedAdvance_LaunchesAndReturnsHeld()
    {
        var (kernel, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/FallbackGate.cs");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var attemptRoot = CreateTempDirectory("mcg-conductor-fallback-acceptance");
        var acceptanceRuns = 0;
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9100 + launches);
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceRuns++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/FallbackGate.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            driver.BeginTick();
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Equal(0, acceptanceRuns);
            Assert.True(result.IsHeld);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verifying, held.State);
            Assert.Equal(1, launches);
            var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, attempt.Outcome);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void FallbackGate_RunningAttempt_DoesNotRegate()
    {
        var (kernel, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/HeldGate.cs");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var attemptRoot = CreateTempDirectory("mcg-conductor-fallback-running");
        var acceptanceRuns = 0;
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9200 + launches);
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceRuns++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/HeldGate.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            driver.BeginTick(kernel, 1);
            var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            var second = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.True(first.IsHeld);
            Assert.True(second.IsHeld);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Equal(1, launches);
            Assert.Equal(0, acceptanceRuns);
            Assert.Single(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void FallbackGate_InFlight_AllowsNextTickReap()
    {
        var (kernel, seededGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/ReapGate.cs");
        PassVerification(kernel, seededGoal, seededGoal.Tasks.Single());
        kernel = WithGoalStatus(kernel, seededGoal.Id, GoalStatus.Verified);
        var goal = kernel.GetGoal(seededGoal.Id);
        var attemptRoot = CreateTempDirectory("mcg-conductor-fallback-reap");
        var dispatchRoot = CreateTempDirectory("mcg-conductor-dead-worker");
        var workerTask = new TaskSpec(
            TaskId.New(),
            "Review while acceptance remains in flight",
            AgentRole.Reviewer);
        var workerGoal = kernel.CreateGoal(
            "Worker dies while acceptance remains in flight",
            [workerTask]);
        kernel.ActivateGoal(workerGoal.Id, DefaultAgents());
        var startedAt = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        var stdoutPath = Path.Combine(dispatchRoot, "worker.out.log");
        var stderrPath = Path.Combine(dispatchRoot, "worker.err.log");
        var exitCodePath = Path.Combine(dispatchRoot, "worker.exit.txt");
        File.WriteAllText(stdoutPath, "worker started");
        File.WriteAllText(stderrPath, string.Empty);
        kernel.RecordTaskDispatch(
            workerGoal.Id,
            workerTask.Id,
            new TaskDispatchRecord("test-worker", "worker.exe", dispatchRoot, startedAt));
        kernel.RecordTaskProcessStarted(
            workerGoal.Id,
            workerTask.Id,
            new TaskProcessRecord(
                9701,
                "worker.exe",
                dispatchRoot,
                stdoutPath,
                stderrPath,
                exitCodePath,
                startedAt,
                null,
                null,
                OwnedProcessIds: [9701]));
        var launches = 0;
        var workerAlive = true;
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => workerAlive);
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9250 + launches);
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/ReapGate.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);
        var sweeps = 0;
        var loop = new ConductorBatchLoop(
            sweep: loopKernel =>
            {
                sweeps++;
                if (workerAlive)
                {
                    return;
                }

                runner.RefreshLatestProcess(loopKernel, workerGoal.Id, workerTask.Id);
            });

        try
        {
            var tickBoundaries = 0;
            var summary = loop.Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 2,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ =>
                {
                    tickBoundaries++;
                    if (workerAlive)
                    {
                        Assert.True(kernel.GetTask(workerGoal.Id, workerTask.Id).LastProcess!.IsRunning);
                        Assert.Equal(GoalStatus.Verifying, goal.Status);
                        workerAlive = false;
                    }

                    return false;
                });

            Assert.Equal(2, sweeps);
            Assert.True(tickBoundaries >= 1);
            Assert.True(summary.Held >= 2);
            Assert.False(kernel.GetTask(workerGoal.Id, workerTask.Id).LastProcess!.IsRunning);
            Assert.Equal(1, launches);
            var attempt = Assert.Single(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, attempt.Outcome);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(dispatchRoot);
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void FallbackGate_CompletedAttempt_LandsExactlyOnce()
    {
        var (kernel, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/CompletedGate.cs");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var attemptRoot = CreateTempDirectory("mcg-conductor-fallback-completed");
        ConductorParallelAcceptanceOwnedProcessLaunch? ownedLaunch = null;
        var acceptanceRuns = 0;
        var landingRuns = 0;
        var landed = false;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                ownedLaunch = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9301);
            });
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceRuns++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            land: landedGoal =>
            {
                landingRuns++;
                landed = true;
                return new LandingResult(
                    landedGoal.Id.Value,
                    landedGoal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "ok");
            },
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/CompletedGate.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            driver.BeginTick(kernel, 1);
            var started = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            Assert.True(started.IsHeld);
            Assert.NotNull(ownedLaunch);
            Assert.Equal(GoalStatus.Verifying, goal.Status);

            ownedLaunch!.ExecuteInCurrentProcess(9301);
            var completed = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            var later = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.True(completed.WasExecuted);
            Assert.True(later.WasExecuted);
            Assert.Equal(1, acceptanceRuns);
            Assert.Equal(1, landingRuns);
            Assert.Empty(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void FallbackGate_CompletedGoalHoldsBeforeLaunchingAcceptance()
    {
        var (kernel, seededGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/CompletedHold.cs");
        PassVerification(kernel, seededGoal, seededGoal.Tasks.Single());
        kernel = WithGoalStatus(kernel, seededGoal.Id, GoalStatus.Completed);
        var goal = kernel.GetGoal(seededGoal.Id);
        var attemptRoot = CreateTempDirectory("mcg-conductor-completed-hold");
        var launches = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: _ =>
            {
                launches++;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9400);
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/CompletedHold.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);
        try
        {
            driver.BeginTick(kernel, 1);
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            Assert.True(result.IsHeld);
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Equal(0, launches);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void FallbackGate_FailedAttempt_EscalatesWithoutLanding()
    {
        var (kernel, seededGoal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/FailedFallback.cs");
        PassVerification(kernel, seededGoal, seededGoal.Tasks.Single());
        kernel = WithGoalStatus(kernel, seededGoal.Id, GoalStatus.Verified);
        var goal = kernel.GetGoal(seededGoal.Id);
        var attemptRoot = CreateTempDirectory("mcg-conductor-fallback-failed");
        ConductorParallelAcceptanceOwnedProcessLaunch? ownedLaunch = null;
        var launches = 0;
        var landingRuns = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launches++;
                ownedLaunch = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9401);
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => new AcceptanceVerificationSummary(
                false,
                [],
                "Acceptance failed.",
                ["FailedFallbackTests.Fails"]),
            land: landedGoal =>
            {
                landingRuns++;
                return new LandingResult(
                    landedGoal.Id.Value,
                    landedGoal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "unexpected");
            },
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/FailedFallback.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            driver.BeginTick(kernel, 1);
            Assert.True(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).IsHeld);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            ownedLaunch!.ExecuteInCurrentProcess(9401);
            var completed = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            var later = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.True(completed.WasEscalated);
            Assert.True(later.WasEscalated);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.Equal(1, launches);
            Assert.Equal(0, landingRuns);
            var attempt = ReadAttempt(ownedLaunch.Attempt.MetadataPath);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, attempt.Outcome);
            Assert.NotNull(attempt.ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void FallbackGate_CancelledAttempt_HoldsWithoutVerdict()
    {
        var (kernel, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/CancelledFallback.cs");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var attemptRoot = CreateTempDirectory("mcg-conductor-fallback-cancelled");
        ConductorParallelAcceptanceOwnedProcessLaunch? ownedLaunch = null;
        var landingRuns = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                ownedLaunch = launch;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9501);
            });
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => throw new OperationCanceledException("goal parked"),
            land: landedGoal =>
            {
                landingRuns++;
                return new LandingResult(
                    landedGoal.Id.Value,
                    landedGoal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "unexpected");
            },
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/CancelledFallback.cs"],
            parallelAcceptanceAttemptCoordinator: coordinator);

        try
        {
            driver.BeginTick(kernel, 1);
            Assert.True(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).IsHeld);
            ownedLaunch!.ExecuteInCurrentProcess(9501);
            var completed = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.True(completed.IsHeld);
            Assert.Equal(0, landingRuns);
            Assert.Null(goal.LatestAcceptanceFailure);
            var attempt = ReadAttempt(ownedLaunch.Attempt.MetadataPath);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, attempt.Outcome);
            Assert.NotNull(attempt.ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void FallbackGate_OneShotAdvance_CreatesAndReconcilesSharedAttempt()
    {
        var (kernel, goal) = SimpleGoal("Update src/Mcg.AgentOrchestrator.App/Orchestration/OneShotGate.cs");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var attemptRoot = CreateTempDirectory("mcg-conductor-fallback-one-shot");
        var acceptanceRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                acceptanceRuns++;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Orchestration/OneShotGate.cs"],
            parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                runInline: true,
                acquireStableSlotLease: (_, _) => null));

        try
        {
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.True(result.WasExecuted);
            Assert.Equal(1, acceptanceRuns);
            var attempt = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(attemptRoot, goal.Id.Value),
                "*.attempt.json"));
            var recorded = ReadAttempt(attempt);
            Assert.StartsWith($"{goal.Id.Value[..8]}-0-", recorded.AttemptId, StringComparison.Ordinal);
            Assert.NotNull(recorded.ReconciledAt);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }
}
