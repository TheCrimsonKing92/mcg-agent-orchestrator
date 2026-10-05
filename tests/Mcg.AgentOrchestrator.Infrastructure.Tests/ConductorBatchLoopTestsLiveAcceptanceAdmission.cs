using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLiveAcceptanceAdmission : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLiveAcceptanceAdmission(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact]
    public void LiveAttemptReservesCapacityBeforeOlderNewlyEligibleGoals()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var now = DateTimeOffset.Parse("2026-09-03T09:00:00Z");
        var clock = new ParallelAcceptanceTestClock(now.AddMinutes(-10));
        var kernel = new AgentOrchestratorKernel(clock);
        var goals = CreateGoals(kernel, 3, "ReservedAcrossTicks");
        for (var attempt = 1;
             attempt < 128 && BuildPermitIndex(goals[0]) == BuildPermitIndex(goals[1]);
             attempt++)
        {
            kernel = new AgentOrchestratorKernel(clock);
            goals = CreateGoals(kernel, 3, "ReservedAcrossTicks");
        }
        Assert.NotEqual(BuildPermitIndex(goals[0]), BuildPermitIndex(goals[1]));

        var running = goals[0];
        var waiters = goals[1..];
        for (var index = 0; index < waiters.Length; index++)
        {
            clock.UtcNow = now.AddMinutes(index);
            PassVerificationAt(kernel, waiters[index], waiters[index].Tasks.Single(), clock.UtcNow);
        }
        clock.UtcNow = now.AddMinutes(2);
        PassVerificationAt(kernel, running, running.Tasks.Single(), clock.UtcNow);

        using var releaseGates = new ManualResetEventSlim(false);
        using var runningEntered = new ManualResetEventSlim(false);
        var waiterEntered = waiters.ToDictionary(goal => goal.Id, _ => new ManualResetEventSlim(false));
        var attemptRoot = CreateTempDirectory("mcg-conductor-live-attempt-reserves-capacity");
        var holdWaiters = true;
        Action waitForAttempts = () => { };

        try
        {
            var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    if (goal.Id == running.Id)
                    {
                        runningEntered.Set();
                    }
                    else
                    {
                        waiterEntered[goal.Id].Set();
                    }

                    Assert.True(releaseGates.Wait(TimeSpan.FromSeconds(30)));
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: goal =>
                {
                    if (holdWaiters && goal.Id != running.Id)
                    {
                        throw new IOException("waiter scope intentionally unavailable during primer tick");
                    }

                    return [$"src/Mcg.AgentOrchestrator.App/Orchestration/{goal.Id.Value}.cs"];
                },
                parallelAcceptanceAttemptCoordinator: coordinator);

            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            Assert.True(runningEntered.Wait(TimeSpan.FromSeconds(5)));

            holdWaiters = false;
            BatchTickSummary? admissionTick = null;
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => admissionTick = current);

            Assert.True(SpinWait.SpinUntil(
                () => waiterEntered.Values.Any(signal => signal.IsSet),
                TimeSpan.FromSeconds(5)));
            Assert.Contains(admissionTick!.ProgressLines!, line =>
                line.Contains($"ACCEPTANCE goal={running.Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("result=running", StringComparison.Ordinal));
            Assert.Contains(admissionTick.ProgressLines!, line =>
                waiters.Any(goal => line.Contains($"ACCEPTANCE goal={goal.Id.Value[..8]}", StringComparison.Ordinal)) &&
                line.Contains("slot=slot-1", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));
            Assert.Contains(admissionTick.ProgressLines!, line =>
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
            var admittedWaiter = Assert.Single(waiters.Where(goal =>
                Directory.Exists(Path.Combine(attemptRoot, goal.Id.Value))));
            Assert.True(coordinator.HasLiveAttempt(running.Id.Value));
            Assert.True(coordinator.HasLiveAttempt(admittedWaiter.Id.Value));
        }
        finally
        {
            releaseGates.Set();
            waitForAttempts();
            foreach (var signal in waiterEntered.Values)
            {
                signal.Dispose();
            }
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public void LiveInvalidatedAttemptsAndCohortGatesShareTheAcceptanceCapacity()
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var now = DateTimeOffset.Parse("2026-09-03T10:00:00Z");
        var clock = new ParallelAcceptanceTestClock(now);
        var kernel = new AgentOrchestratorKernel(clock);
        var goals = CreateGoals(kernel, 5, "SharedCapacity");
        for (var attempt = 1;
             attempt < 128 && BuildPermitIndex(goals[0]) == BuildPermitIndex(goals[1]);
             attempt++)
        {
            kernel = new AgentOrchestratorKernel(clock);
            goals = CreateGoals(kernel, 5, "SharedCapacity");
        }
        Assert.NotEqual(BuildPermitIndex(goals[0]), BuildPermitIndex(goals[1]));

        for (var index = 0; index < goals.Length; index++)
        {
            clock.UtcNow = now.AddMinutes(index);
            PassVerificationAt(kernel, goals[index], goals[index].Tasks.Single(), clock.UtcNow);
        }

        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [goals[0].Id] = ["src/Mcg.AgentOrchestrator.App/Orchestration/LiveFirst.cs"],
            [goals[1].Id] = ["src/Mcg.AgentOrchestrator.App/Orchestration/LiveSecond.cs"],
            [goals[2].Id] = ["tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs"],
            [goals[3].Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs"],
            [goals[4].Id] = ["tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs"]
        };
        var projectionEnabled = false;
        var projector = new GateReadyCandidateProjector(
            goalId => projectionEnabled
                ? new GateReadyCandidateRevisionPair(goalId.Value.PadRight(40, 'b')[..40], new string('a', 40))
                : throw new IOException("projection intentionally unavailable during primer tick"),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        using var releaseGates = new ManualResetEventSlim(false);
        using var firstWaveEntered = new CountdownEvent(2);
        var attemptRoot = CreateTempDirectory("mcg-conductor-shared-acceptance-capacity");
        var trainCalls = 0;
        var cohortCalls = 0;
        Action waitForAttempts = () => { };

        try
        {
            var coordinator = ThreadedAcceptanceAttemptCoordinator(attemptRoot, out waitForAttempts);
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    if (goal.Id == goals[0].Id || goal.Id == goals[1].Id)
                    {
                        firstWaveEntered.Signal();
                    }

                    Assert.True(releaseGates.Wait(TimeSpan.FromSeconds(30)));
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                runMergeTrain: (selection, _, policy) =>
                {
                    trainCalls++;
                    return new ConductorMergeTrainRunResult(
                        null,
                        BuildSelectionResults(selection.Members, policy, "unexpected train admission"),
                        [],
                        "outcome=passed");
                },
                runAcceptanceCohort: (selection, _, policy) =>
                {
                    cohortCalls++;
                    return new ConductorAcceptanceCohortRunResult(
                        null,
                        BuildSelectionResults(selection.Members, policy, "unexpected cohort admission"),
                        "outcome=passed");
                },
                parallelAcceptanceAttemptCoordinator: coordinator);

            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                NoStopPath(),
                maxIterations: 1);
            Assert.True(firstWaveEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(coordinator.InvalidateCurrent(goals[0].Id.Value, "test invalidation while root remains alive"));
            Assert.True(coordinator.TryGetLiveInvalidatedAttempt(goals[0].Id.Value, out _));

            projectionEnabled = true;
            BatchTickSummary? capacityTick = null;
            new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => capacityTick = current);

            Assert.Equal(0, trainCalls);
            Assert.Equal(0, cohortCalls);
            Assert.All(goals[2..], goal =>
                Assert.False(Directory.Exists(Path.Combine(attemptRoot, goal.Id.Value))));
            Assert.Contains(capacityTick!.ProgressLines!, line =>
                line.Contains($"ACCEPTANCE goal={goals[0].Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("result=running", StringComparison.Ordinal));
            Assert.Contains(capacityTick.ProgressLines!, line =>
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
        }
        finally
        {
            releaseGates.Set();
            waitForAttempts();
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Xunit.Fact]
    public async Task RetryInvalidatedActiveAttemptReservesCapacityBeforeOtherGoalsStart()
    {
        var root = CreateTempDirectory("mcg-conductor-retry-invalidated-capacity");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = CreateGoals(kernel, 3, "RetryInvalidatedCapacity");
            foreach (var goal in goals)
            {
                PassVerificationAt(kernel, goal, goal.Tasks.Single(), DateTimeOffset.UtcNow);
            }

            kernel.BeginGoalAcceptanceVerification(goals[0].Id, "Acceptance attempt launched.");
            var nextPid = 9000;
            var attemptRoot = Path.Combine(root, "attempts");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(
                    Interlocked.Increment(ref nextPid)));
            var invalidatedCandidate = ConductorParallelAcceptanceCandidate.Create(
                goals[0],
                0,
                ["src/RetryInvalidated.cs"],
                "branch-sha",
                "main-sha");
            var started = coordinator.Evaluate(
                invalidatedCandidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);

            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var task = goals[0].Tasks.Single();
            await store.EnqueueAsync(new OperatorIntentRecord(
                "retry-invalidated-capacity",
                "retry-invalidated-capacity-key",
                OperatorIntentVerbs.Retry,
                goals[0].Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload("Retry while acceptance remains alive.", null, RetryCause: RetryCause.ContractClarification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [],
                "operator",
                "test",
                "local-process",
                DateTimeOffset.UtcNow));

            BatchTickSummary? capacityTick = null;
            new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(store)).Run(
                kernel,
                MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                    getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"],
                    parallelAcceptanceAttemptCoordinator: coordinator),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => capacityTick = current,
                persistGoalTick: (_, _) => { });

            Assert.Equal(GoalStatus.Active, goals[0].Status);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.True(coordinator.TryGetLiveInvalidatedAttempt(goals[0].Id.Value, out _));
            Assert.Single(goals[1..].Where(goal => Directory.Exists(Path.Combine(attemptRoot, goal.Id.Value))));
            Assert.Contains(capacityTick!.ProgressLines!, line =>
                line.Contains("result=deferred", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void PersistedSlotOneLeavesSlotZeroForTheNextAttempt()
    {
        var root = CreateTempDirectory("mcg-conductor-live-slot-selection");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = CreateGoals(kernel, 2, "PersistedSlotSelection");
            foreach (var goal in goals)
            {
                PassVerificationAt(kernel, goal, goal.Tasks.Single(), DateTimeOffset.UtcNow);
            }

            var nextPid = 9200;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(
                    Interlocked.Increment(ref nextPid)));
            var runningCandidate = ConductorParallelAcceptanceCandidate.Create(
                goals[0],
                1,
                ["src/Running.cs"],
                "branch-sha",
                "main-sha");
            Assert.Equal(
                ConductorParallelAcceptanceAttemptDecisionKind.Started,
                coordinator.Evaluate(
                    runningCandidate,
                    ConductorAutonomyPolicy.Conservative,
                    PassingRun).Kind);
            kernel.BeginGoalAcceptanceVerification(goals[0].Id, "Acceptance attempt launched in persisted slot one.");

            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                    getLandingFileScopes: goal => goal.Id == goals[0].Id ? ["src/Running.cs"] : ["src/Waiting.cs"],
                    parallelAcceptanceAttemptCoordinator: coordinator),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => tick = current);

            Assert.Contains(tick!.ProgressLines!, line =>
                line.Contains($"ACCEPTANCE goal={goals[1].Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("slot=slot-0", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void InvalidatedAttemptExitRaceReturnsTerminalDecision()
    {
        var root = CreateTempDirectory("mcg-conductor-invalidated-exit-race");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateGoals(kernel, 1, "InvalidatedExitRace").Single();
            PassVerificationAt(kernel, goal, goal.Tasks.Single(), DateTimeOffset.UtcNow);
            var now = DateTimeOffset.Parse("2026-09-03T11:00:00Z");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                utcNow: () => now,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(9401),
                recentHeartbeatGrace: TimeSpan.FromMinutes(1));
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                0,
                ["src/ExitRace.cs"],
                "branch-sha",
                "main-sha");
            var started = coordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                PassingRun);
            Assert.True(coordinator.InvalidateCurrent(goal.Id.Value, "retry invalidated live attempt"));
            now = now.AddMinutes(2);
            Assert.False(coordinator.TryGetLiveInvalidatedAttempt(goal.Id.Value, out _));
            var selected = Assert.Single(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
            Assert.Equal(
                ConductorParallelAcceptanceAttemptDecisionKind.Running,
                coordinator.ObserveExistingAttempt(selected, candidate).Kind);

            File.WriteAllText(started.Attempt.ExitCodePath, "0");
            var observed = coordinator.ObserveExistingAttempt(selected, candidate);

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, observed.Kind);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, observed.Attempt.Outcome);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void CorruptAttemptMetadataHoldsAllAcceptanceAdmissionWithoutKillingTheTick()
    {
        var root = CreateTempDirectory("mcg-conductor-corrupt-capacity-state");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goals = CreateGoals(kernel, 2, "CorruptCapacityState");
            foreach (var goal in goals)
            {
                PassVerificationAt(kernel, goal, goal.Tasks.Single(), DateTimeOffset.UtcNow);
            }

            var corruptGoalDirectory = Path.Combine(root, goals[0].Id.Value);
            Directory.CreateDirectory(corruptGoalDirectory);
            File.WriteAllText(Path.Combine(corruptGoalDirectory, "corrupt.attempt.json"), "{");
            var launches = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(9501);
                });

            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                    getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"],
                    parallelAcceptanceAttemptCoordinator: coordinator),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => tick = current);

            Assert.Equal(0, launches);
            Assert.False(Directory.Exists(Path.Combine(root, goals[1].Id.Value)));
            Assert.All(goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            Assert.Contains(tick!.ProgressLines!, line =>
                line.Contains("reason=acceptance-capacity-state-unavailable", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void AliveAttemptsWithStaleHeartbeatsStillReserveCapacityBeforeFairnessSelection()
    {
        var root = CreateTempDirectory("mcg-conductor-stale-heartbeat-capacity");
        try
        {
            var now = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
            var clock = new ParallelAcceptanceTestClock(now);
            var kernel = new AgentOrchestratorKernel(clock);
            var goals = CreateGoals(kernel, 4, "StaleHeartbeatCapacity");
            foreach (var goal in goals)
            {
                PassVerificationAt(kernel, goal, goal.Tasks.Single(), clock.UtcNow);
                clock.UtcNow = clock.UtcNow.AddSeconds(1);
            }

            var nextPid = 9600;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                utcNow: () => clock.UtcNow,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(
                    Interlocked.Increment(ref nextPid)),
                recentHeartbeatGrace: TimeSpan.FromMinutes(1));
            for (var slot = 0; slot < 2; slot++)
            {
                var candidate = ConductorParallelAcceptanceCandidate.Create(
                    goals[slot],
                    slot,
                    [$"src/Running{slot}.cs"],
                    "branch-sha",
                    "main-sha");
                Assert.Equal(
                    ConductorParallelAcceptanceAttemptDecisionKind.Started,
                    coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun).Kind);
                kernel.BeginGoalAcceptanceVerification(goals[slot].Id, "Acceptance attempt launched.");
            }

            clock.UtcNow = clock.UtcNow.AddMinutes(2);
            Assert.False(coordinator.HasLiveAttempt(goals[0].Id.Value));
            Assert.False(coordinator.HasLiveAttempt(goals[1].Id.Value));

            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                    getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"],
                    parallelAcceptanceAttemptCoordinator: coordinator),
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => tick = current);

            Assert.All(goals[2..], goal =>
                Assert.False(Directory.Exists(Path.Combine(root, goal.Id.Value))));
            Assert.DoesNotContain(goals, goal => goal.Status == GoalStatus.Failed);
            Assert.Contains(tick!.ProgressLines!, line =>
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal));
            Assert.DoesNotContain(tick.ProgressLines!, line =>
                line.Contains("result=escalated", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void ManualPolicyVerifiedGoalRecordsCandidateNoneProgressLine()
    {
        var (goal, tick) = RunVerifiedGoalAdmissionTick(ConductorAutonomyPolicy.Manual);

        var line = Assert.Single(tick.ProgressLines!, progress =>
            progress.Contains("reason=parallel-acceptance-candidate-none", StringComparison.Ordinal));
        Assert.Contains($"goal={goal.Id.Value[..8]}", line);
        Assert.Contains("detail=verified-transition-requires-operator", line);
        Assert.Contains("ADMISSION tick=1 result=held", line);
    }

    [Xunit.Fact]
    public void ConservativePolicyVerifiedGoalStartsAcceptanceWithoutCandidateNoneLine()
    {
        var (manualGoal, manualTick) = RunVerifiedGoalAdmissionTick(ConductorAutonomyPolicy.Manual);
        Assert.Contains(manualTick.ProgressLines!, line =>
            line.Contains($"reason=parallel-acceptance-candidate-none goal={manualGoal.Id.Value[..8]}", StringComparison.Ordinal) &&
            line.Contains("detail=verified-transition-requires-operator", StringComparison.Ordinal));
        var (goal, tick) = RunVerifiedGoalAdmissionTick(ConductorAutonomyPolicy.Conservative);

        Assert.DoesNotContain(tick.ProgressLines!, line =>
            line.Contains("parallel-acceptance-candidate-none", StringComparison.Ordinal));
        // Main 0009e75aa admits this single verified goal into slot-0 with result=started.
        Assert.Contains(tick.ProgressLines!, line =>
            line.Contains($"ACCEPTANCE goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
            line.Contains("slot=slot-0", StringComparison.Ordinal) &&
            line.Contains("result=started", StringComparison.Ordinal));
    }

    private static (Goal Goal, BatchTickSummary Tick) RunVerifiedGoalAdmissionTick(ConductorAutonomyPolicy policy)
    {
        using var isolatedRoot = IsolatedDotnetRootScope();
        var root = CreateTempDirectory("mcg-conductor-candidate-none");
        try
        {
            var now = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
            var kernel = new AgentOrchestratorKernel(new ParallelAcceptanceTestClock(now));
            var goal = CreateGoals(kernel, 1, "CandidateNone").Single();
            PassVerificationAt(kernel, goal, goal.Tasks.Single(), now);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single().Status);
            Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root,
                utcNow: () => now,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(9701));

            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(
                kernel,
                MakeDriver(
                    getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                    runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                    getLandingFileScopes: _ => ["src/CandidateNone.cs"],
                    parallelAcceptanceAttemptCoordinator: coordinator,
                    utcNow: () => now,
                    executionDirectory: root),
                policy,
                NoStopPath(),
                maxIterations: 1,
                onTick: current => tick = current);

            Assert.NotNull(tick);
            return (goal, tick);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static Goal[] CreateGoals(AgentOrchestratorKernel kernel, int count, string suffix) =>
        Enumerable.Range(0, count)
            .Select(index => GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                $"Update src/Mcg.AgentOrchestrator.App/Orchestration/{suffix}{index}.cs"))
            .ToArray();

    private static IReadOnlyDictionary<string, ConductorAdvanceResult> BuildSelectionResults(
        IReadOnlyList<GateReadyCandidateProjection> members,
        ConductorAutonomyPolicy policy,
        string detail) =>
        members.ToDictionary(
            member => member.GoalId.Value,
            member => new ConductorAdvanceResult(
                member.GoalId.Value,
                member.GoalId.Value[..8],
                policy.Name,
                new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, detail)),
            StringComparer.Ordinal);

    private static int BuildPermitIndex(Goal goal) =>
        goal.Id.Value[..8]
            .ToLowerInvariant()
            .Sum(ch => (int)ch) %
        DotnetBuildEnvironmentManager.BuildConcurrencySlotCount;

    private static ConductorParallelAcceptanceAttemptCoordinator ThreadedAcceptanceAttemptCoordinator(
        string attemptRoot,
        out Action waitForAttempts)
    {
        var nextPid = 8000;
        var alive = new ConcurrentDictionary<int, byte>();
        var threads = new ConcurrentBag<Thread>();
        waitForAttempts = () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            foreach (var thread in threads)
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(
                    remaining > TimeSpan.Zero && thread.Join(remaining),
                    $"acceptance attempt thread {thread.Name} did not finish before cleanup");
            }
        };

        return new ConductorParallelAcceptanceAttemptCoordinator(
            attemptRoot,
            isProcessAlive: pid => alive.ContainsKey(pid),
            launchOwnedProcess: launch =>
            {
                var pid = Interlocked.Increment(ref nextPid);
                var thread = new Thread(() =>
                {
                    alive[pid] = 0;
                    try
                    {
                        launch.ExecuteInCurrentProcess(pid);
                    }
                    finally
                    {
                        alive.TryRemove(pid, out _);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"acceptance-attempt-test-{pid}"
                };
                threads.Add(thread);
                thread.Start();
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(pid);
            });
    }

    private static EnvVarScope IsolatedDotnetRootScope() =>
        new(
            DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable,
            Path.Combine(Path.GetTempPath(), $"{DotnetBuildEnvironmentManager.RootDirectoryName}-batch-loop-{Guid.NewGuid():N}"));

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;
        private readonly string _value;

        public EnvVarScope(string name, string value)
        {
            _name = name;
            _value = value;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
            if (!string.IsNullOrWhiteSpace(_value) && Directory.Exists(_value))
            {
                try { Directory.Delete(_value, recursive: true); }
                catch { }
            }
        }
    }

    private sealed class ParallelAcceptanceTestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
