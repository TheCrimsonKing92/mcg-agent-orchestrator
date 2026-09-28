using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSupersededGateStop(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const string Scope = "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs";
    private const string LandingScope = "docs/architecture.md";
    private const string OldMain = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewMain = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Branch = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void LandingRequestsStopAndWaitsForObservedExit()
    {
        var root = CreateTempDirectory("mcg-superseded-gate");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var landing = CreateVerifiedSimpleGoal(kernel, "Land first goal");
            var waiting = CreateVerifiedSimpleGoal(kernel, "Run second gate");
            var policy = ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2 };
            var candidate = ConductorParallelAcceptanceCandidate.Create(waiting, 1, [Scope], Branch, OldMain);
            var starter = new ConductorParallelAcceptanceAttemptCoordinator(
                root, isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7105));
            var started = starter.Evaluate(candidate, policy, PassingRun);
            var alive = true;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, runInline: true, isProcessAlive: _ => alive);
            var main = OldMain;
            var landed = false;
            var stopCalls = new List<string>();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getLandingFileScopes: goal => goal.Id == landing.Id ? [LandingScope] : [Scope],
                getAcceptanceSlotCount: _ => 2,
                runAcceptanceWithSlot: (_, _) => new AcceptanceVerificationSummary(
                    true, [], BranchHeadSha: Branch, MainHeadSha: OldMain),
                resolveAcceptanceHeads: _ => (Branch, main),
                land: goal =>
                {
                    landed = true;
                    main = NewMain;
                    kernel.CompleteGoal(goal.Id, "Landed");
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed", Branch);
                },
                parallelAcceptanceAttemptCoordinator: coordinator);
            var loop = new ConductorBatchLoop
            {
                SupersededAttemptStop = (attempt, head) =>
                {
                    stopCalls.Add(attempt.AttemptId);
                    return coordinator.RequestSupersededMainStop(attempt, head);
                }
            };

            loop.Run(kernel, driver, policy, NoStopPath(), maxIterations: 1);

            Assert.True(landed);
            Assert.Equal([started.Attempt.AttemptId], stopCalls);
            Assert.Equal(NewMain, ReadAttempt(started.Attempt.MetadataPath).SupersedingMainHeadSha);
            Assert.True(coordinator.TryGetLiveInvalidatedAttempt(waiting.Id.Value, out _));
            Assert.Equal(GoalStatus.Verifying, kernel.GetGoal(waiting.Id).Status);
            Assert.Equal(0, kernel.GetGoal(waiting.Id).ConsecutiveAcceptanceIdentityStaleCount);
            Assert.Single(coordinator.GetCapacityReservingAttempts([waiting.Id.Value]));
            Assert.Contains(waiting.Id.Value, coordinator.GetLiveAttemptGoalIds([waiting.Id.Value]));

            alive = false;
            File.WriteAllText(started.Attempt.ExitCodePath, "1");
            loop.Run(kernel, driver, policy, NoStopPath(), maxIterations: 1);

            Assert.Equal(GoalStatus.Verified, kernel.GetGoal(waiting.Id).Status);
            Assert.Equal(1, kernel.GetGoal(waiting.Id).ConsecutiveAcceptanceIdentityStaleCount);
            Assert.Equal(started.Attempt.AttemptId, kernel.GetGoal(waiting.Id).LastAcceptanceIdentityStaleAttemptId);
            Assert.Empty(coordinator.GetCapacityReservingAttempts([waiting.Id.Value]));
            Assert.Equal([started.Attempt.AttemptId], stopCalls);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void LiveAttemptOnCurrentMainIsNotStoppedWithoutLanding()
    {
        var root = CreateTempDirectory("mcg-current-gate");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Run current gate");
            CreateVerifiedSimpleGoal(kernel, "Competing gate held by the live attempt");
            var policy = ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 2 };
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope], Branch, OldMain);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7106));
            var started = coordinator.Evaluate(candidate, policy, PassingRun);
            var stopCalls = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getLandingFileScopes: _ => [Scope],
                getAcceptanceSlotCount: _ => 2,
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                resolveAcceptanceHeads: _ => (Branch, OldMain),
                parallelAcceptanceAttemptCoordinator: coordinator);
            var loop = new ConductorBatchLoop
            {
                SupersededAttemptStop = (_, _) => { stopCalls++; return true; }
            };

            loop.Run(kernel, driver, policy, NoStopPath(), maxIterations: 1);

            Assert.Equal(0, stopCalls);
            Assert.Null(ReadAttempt(started.Attempt.MetadataPath).SupersedingMainHeadSha);
            Assert.Equal(GoalStatus.Verifying, kernel.GetGoal(goal.Id).Status);
            Assert.Single(coordinator.GetCapacityReservingAttempts([goal.Id.Value]));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void RecordedGreenLaneRemainsReusableAfterStop()
    {
        var root = CreateTempDirectory("mcg-superseded-cache");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Run gate with reusable lane");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope], Branch, OldMain);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7107));
            var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun);
            var lane = new GoalAcceptanceVerifier.AcceptanceManifestCheck
            {
                Name = "infrastructure tests: Superseded cache",
                Type = "dotnet-test",
                Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                Arguments = ["--filter", "FullyQualifiedName~ConductorBatchLoopTestsSupersededGateStop"]
            };
            AcceptancePartitionVerdictCache Cache(string attempt, string main) =>
                Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
                    new AcceptancePartitionVerdictCacheOptions(goal.Id, root, [lane], 5, true,
                        _ => "tree", _ => main, _ => "commit", () => attempt,
                        () => "manifest", () => false, ResolveClosureHash: _ => "closure-fixed")));
            var first = Cache(started.Attempt.AttemptId, OldMain);
            first.RecordExecution(lane, new AcceptanceCheckResult(lane.Name, true, 0, null, TestResultPaths: []));
            Assert.NotNull(first.CompleteAttempt());
            Assert.True(coordinator.RequestSupersededMainStop(started.Attempt, NewMain));
            Assert.Equal(NewMain, ReadAttempt(started.Attempt.MetadataPath).SupersedingMainHeadSha);

            var reused = Assert.IsType<AcceptanceCheckResult>(Cache("regate-attempt", NewMain).TryReuse(lane));
            Assert.Contains($"source_attempt_id={started.Attempt.AttemptId}", reused.ResultSummary, StringComparison.Ordinal);
            Assert.Contains("reuse_rule=closure", reused.ResultSummary, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void PublishedUnreconciledVerdictIsStaleAfterMainAdvances()
    {
        var root = CreateTempDirectory("mcg-published-superseded-gate");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Reconcile published gate");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope], Branch, OldMain);
            var policy = ConductorAutonomyPolicy.Conservative;
            var starter = new ConductorParallelAcceptanceAttemptCoordinator(
                root, isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7108));
            var started = starter.Evaluate(candidate, policy, PassingRun);
            var child = new ConductorParallelAcceptanceAttemptCoordinator(root, isProcessAlive: _ => false);
            child.RunAttemptForTests(started.Attempt, candidate, policy, PassingRun);
            var parent = new ConductorParallelAcceptanceAttemptCoordinator(root, isProcessAlive: _ => false);

            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed,
                ReadAttempt(started.Attempt.MetadataPath).Outcome);
            Assert.Single(parent.GetSupersedableGateAttempts([goal.Id.Value]));
            Assert.True(parent.RequestSupersededMainStop(started.Attempt, NewMain));

            var decision = parent.ObserveExistingAttempt(started.Attempt, candidate);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
            Assert.Equal(ConductorBatchLoop.IdentityStaleDisposition,
                ConductorBatchLoop.AcceptanceRunDisposition(Assert.IsType<ConductorParallelAcceptanceRunResult>(decision.Run)));
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Faulted,
                ReadAttempt(started.Attempt.MetadataPath).Outcome);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }
}
