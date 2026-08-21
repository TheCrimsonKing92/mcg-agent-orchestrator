using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorCrossTickTests
{
    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task ParallelAcceptanceFairness_LiveOldest_AllowsDeclaredCapacityAcrossTicks()
    {
        await RunFairnessScenario(0);
        await RunFairnessScenario(1);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task ParallelAcceptanceFairness_StalledOldest_BypassesAtThreshold()
    {
        var scenario = await RunStalledOldestScenario();

        Assert.Contains(scenario.Tick3.ProgressLines!, line =>
            line.Contains($"ACCEPTANCE goal={scenario.BypassedGoalId[..8]}", StringComparison.Ordinal) &&
            line.Contains("result=started", StringComparison.Ordinal));
        Assert.Equal(2, scenario.HeldAttemptCount);
        Assert.Equal(0, scenario.ProcessSpawnCount);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task ParallelAcceptanceFairness_BelowStallThreshold_ExplicitlyDefers()
    {
        var scenario = await RunStalledOldestScenario();

        Assert.Contains(scenario.Tick2.ProgressLines!, line =>
            line.Contains($"goal={scenario.BypassedGoalId[..8]}", StringComparison.Ordinal) &&
            line.Contains("result=deferred", StringComparison.Ordinal) &&
            line.Contains("reason=parallel-acceptance-fairness", StringComparison.Ordinal) &&
            line.Contains($"oldest={scenario.StalledGoalId[..8]}", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Tick2.ProgressLines!, line =>
            line.Contains("reason=parallel-acceptance-stalled-oldest-bypass", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Tick2.ProgressLines!, line =>
            line.Contains($"ACCEPTANCE goal={scenario.BypassedGoalId[..8]}", StringComparison.Ordinal) &&
            line.Contains("result=started", StringComparison.Ordinal));
        Assert.Equal(0, scenario.ProcessSpawnCount);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task ParallelAcceptanceFairness_StalledOldestBypass_EmitsDistinctAdmissionRecord()
    {
        var scenario = await RunStalledOldestScenario();

        var capReached = Assert.Single(scenario.Tick1.ProgressLines!, line =>
            line.Contains("result=cap-reached", StringComparison.Ordinal) &&
            line.Contains("reason=parallel-acceptance-fairness", StringComparison.Ordinal) &&
            line.Contains($"goal={scenario.OvertakingGoalId[..8]}", StringComparison.Ordinal));
        var bypass = Assert.Single(scenario.Tick3.ProgressLines!, line =>
            line.Contains("result=admitted", StringComparison.Ordinal) &&
            line.Contains("reason=parallel-acceptance-stalled-oldest-bypass", StringComparison.Ordinal) &&
            line.Contains($"goal={scenario.BypassedGoalId[..8]}", StringComparison.Ordinal));

        Assert.Contains($"bypassedOldest={scenario.StalledGoalId[..8]}", bypass, StringComparison.Ordinal);
        Assert.Contains("stalledTicks=3", bypass, StringComparison.Ordinal);
        Assert.Contains("threshold=3", bypass, StringComparison.Ordinal);
        Assert.DoesNotContain("bypassedOldest=", capReached, StringComparison.Ordinal);
        Assert.NotEqual(capReached, bypass);
        Assert.Equal(0, scenario.ProcessSpawnCount);
    }

    private static async Task<StalledOldestScenarioResult> RunStalledOldestScenario()
    {
        var time = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(time);
        var kernel = new AgentOrchestratorKernel();
        var stalled = CreateGoal(kernel, "Update src/StalledOldestAcrossTicks.cs");
        var overtaking = CreateGoal(kernel, "Update src/OvertakingAcrossTicks.cs");
        var bypassed = CreateGoal(kernel, "Update src/BypassedAcrossTicks.cs");
        var paidWorkerStartCount = 0;
        var driver = CreateDriver(
            fixture.AttemptCoordinator,
            goal => goal.Id == stalled.Id
                ? throw new IOException("oldest scope temporarily unavailable")
                : [$"src/{goal.Id.Value}.cs"],
            () => paidWorkerStartCount++);

        PassVerification(kernel, stalled, time.GetUtcNow());
        PassVerification(kernel, overtaking, time.GetUtcNow().AddMinutes(1));
        PassVerification(kernel, bypassed, time.GetUtcNow().AddMinutes(2));
        BatchTickSummary? tick1 = null;
        fixture.RunTickForTests(kernel, driver, tick => tick1 = tick);
        Assert.Equal(
            ConductorParallelAcceptanceAttemptOutcome.Running,
            fixture.RequiredHandleForTests(overtaking).Attempt.Outcome);

        fixture.AdvanceTimeForTests(TimeSpan.FromSeconds(1));
        BatchTickSummary? tick2 = null;
        fixture.RunTickForTests(kernel, driver, tick => tick2 = tick);

        fixture.AdvanceTimeForTests(TimeSpan.FromSeconds(1));
        BatchTickSummary? tick3 = null;
        fixture.RunTickForTests(kernel, driver, tick => tick3 = tick);

        Assert.Equal(0, paidWorkerStartCount);
        return new StalledOldestScenarioResult(
            stalled.Id.Value,
            overtaking.Id.Value,
            bypassed.Id.Value,
            tick1!,
            tick2!,
            tick3!,
            fixture.HeldAttemptCount,
            fixture.ProcessSpawnCount);
    }

    private static async Task RunFairnessScenario(int iteration)
    {
        var time = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 8, 2 + iteration, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(time);
        var kernel = new AgentOrchestratorKernel();
        var running = CreateGoal(kernel, "Update src/RunningAcrossTicks.cs");
        // This injected rejection is scenario control, not failure behavior under test. After
        // tick 1 it keeps the live goal out of the rebuilt active-candidate list. Under the
        // pre-fix waiter selection that goal remains the oldest waiter; recording it as served
        // would reset the bounded-overtake counter and mask the cross-tick fairness regression.
        var rejectRunningCandidateRebuild = false;
        var rejectedRunningCandidateRebuildCount = 0;
        var paidWorkerStartCount = 0;
        var driver = CreateDriver(
            fixture.AttemptCoordinator,
            goal =>
            {
                if (goal.Id == running.Id && rejectRunningCandidateRebuild)
                {
                    rejectedRunningCandidateRebuildCount++;
                    throw new IOException("running candidate scope temporarily unavailable");
                }

                return [$"src/{goal.Id.Value}.cs"];
            },
            () => paidWorkerStartCount++);

        PassVerification(kernel, running, time.GetUtcNow());
        fixture.RunTickForTests(kernel, driver);

        var runningHandle = fixture.RequiredHandleForTests(running);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, runningHandle.Attempt.Outcome);
        using (var heartbeat = JsonDocument.Parse(File.ReadAllText(runningHandle.Attempt.HeartbeatPath)))
        {
            Assert.Equal("running", heartbeat.RootElement.GetProperty("state").GetString());
        }
        Assert.True(fixture.AttemptCoordinator.HasLiveAttempt(running.Id.Value));

        fixture.AdvanceTimeForTests(TimeSpan.FromSeconds(1));
        rejectRunningCandidateRebuild = true;
        var primer = CreateGoal(kernel, "Update src/PrimerAcrossTicks.cs");
        PassVerification(kernel, primer, time.GetUtcNow());
        fixture.RunTickForTests(kernel, driver);

        var primerHandle = fixture.RequiredHandleForTests(primer);
        Assert.Equal(1, rejectedRunningCandidateRebuildCount);
        Assert.Equal(ConductorBatchLoop.DefaultParallelAcceptanceCapacity, fixture.HeldAttemptCapacity);
        Assert.Equal(2, fixture.HeldAttemptCapacity);
        Assert.Equal(2, fixture.HeldAttemptCount);
        Assert.True(fixture.AttemptCoordinator.HasLiveAttempt(running.Id.Value));
        Assert.True(fixture.AttemptCoordinator.HasLiveAttempt(primer.Id.Value));

        primerHandle.CompleteForTests();
        Assert.Equal(1, fixture.HeldAttemptCount);
        Assert.True(fixture.AttemptCoordinator.HasLiveAttempt(running.Id.Value));
        Assert.False(fixture.AttemptCoordinator.HasLiveAttempt(primer.Id.Value));

        fixture.AdvanceTimeForTests(TimeSpan.FromSeconds(1));
        var waiting = CreateGoal(kernel, "Update src/WaitingAcrossTicks.cs");
        PassVerification(kernel, waiting, time.GetUtcNow());
        BatchTickSummary? admissionTick = null;
        fixture.RunTickForTests(kernel, driver, tick => admissionTick = tick);

        Assert.Equal(2, rejectedRunningCandidateRebuildCount);
        Assert.Contains(admissionTick!.ProgressLines!, line =>
            line.Contains($"ACCEPTANCE goal={waiting.Id.Value[..8]}", StringComparison.Ordinal) &&
            line.Contains("result=started", StringComparison.Ordinal));
        Assert.DoesNotContain(admissionTick.ProgressLines!, line =>
            line.Contains($"goal={waiting.Id.Value[..8]}", StringComparison.Ordinal) &&
            line.Contains("result=deferred", StringComparison.Ordinal) &&
            line.Contains("reason=parallel-acceptance-fairness", StringComparison.Ordinal));
        Assert.Equal(2, fixture.HeldAttemptCount);
        Assert.Equal(0, fixture.ProcessSpawnCount);
        Assert.Equal(0, paidWorkerStartCount);

        var waitingHandle = fixture.RequiredHandleForTests(waiting);
        var excess = CreateGoal(kernel, "Update src/ExcessAcrossTicks.cs");
        var excessCandidate = ConductorParallelAcceptanceCandidate.Create(
            excess,
            slotIndex: 1,
            [$"src/{excess.Id.Value}.cs"]);
        var gateViolation = Assert.Throws<ConductorParallelAcceptanceAttemptCompletionGateViolationException>(() =>
            fixture.AttemptCoordinator.Evaluate(
                excessCandidate,
                ConductorAutonomyPolicy.Conservative,
                (candidate, _) => ConductorParallelAcceptanceRunResult.Accepted(
                    candidate,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria)));
        Assert.Contains("capacity 2 is exhausted", gateViolation.Message, StringComparison.Ordinal);
        Assert.Equal(2, fixture.HeldAttemptCount);
        Assert.False(fixture.AttemptCoordinator.HasLiveAttempt(excess.Id.Value));

        waitingHandle.CompleteForTests();
        Assert.True(fixture.AttemptCoordinator.HasLiveAttempt(running.Id.Value));
        Assert.False(fixture.AttemptCoordinator.HasLiveAttempt(waiting.Id.Value));
        runningHandle.CompleteForTests();
        Assert.Equal(0, fixture.HeldAttemptCount);
    }

    private static Goal CreateGoal(AgentOrchestratorKernel kernel, string objective) =>
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, objective);

    private static void PassVerification(
        AgentOrchestratorKernel kernel,
        Goal goal,
        DateTimeOffset completedAt)
    {
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", completedAt));
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", string.Empty, completedAt));
    }

    private static ConductorDriver CreateDriver(
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        Func<Goal, IReadOnlyList<string>> getLandingFileScopes,
        Action paidWorkerStart)
    {
        return new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => "/tmp/workspace",
            dispatchAndStart: _ =>
            {
                paidWorkerStart();
                return DispatchStartOutcome.Started();
            },
            startRecordedDispatches: null,
            buildServerShutdown: null,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                "goal/test",
                "OK",
                [],
                null),
            land: (goal, _) => new LandingResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "ok"),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null,
            getLandingFileScopes: getLandingFileScopes,
            runAcceptanceVerificationWithSlot: (_, _) =>
                AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            parallelAcceptanceAttemptCoordinator: coordinator);
    }

    private sealed record StalledOldestScenarioResult(
        string StalledGoalId,
        string OvertakingGoalId,
        string BypassedGoalId,
        BatchTickSummary Tick1,
        BatchTickSummary Tick2,
        BatchTickSummary Tick3,
        int HeldAttemptCount,
        int ProcessSpawnCount);
}
