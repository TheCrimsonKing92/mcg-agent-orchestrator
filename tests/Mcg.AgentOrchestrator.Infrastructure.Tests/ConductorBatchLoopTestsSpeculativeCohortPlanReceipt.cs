using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsSpeculativeCohortPlanReceipt : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsSpeculativeCohortPlanReceipt(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void ReceiptNamesDependencyHoldAndCohortGateMemberExclusions()
    {
        var kernel = new AgentOrchestratorKernel();
        var dependency = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, DefaultAgents(), "Unfinished dependency");
        var held = CreateVerifiedSimpleGoal(kernel, "Verified dependent goal");
        kernel.SetGoalDependency(held.Id, dependency.Id);
        var first = CreateVerifiedSimpleGoal(kernel, "First in-flight cohort member");
        var second = CreateVerifiedSimpleGoal(kernel, "Second in-flight cohort member");
        var goals = new[] { dependency, held, first, second };
        var paths = goals.ToDictionary(goal => goal.Id,
            goal => (IReadOnlyList<string>)[ $"src/App/{goal.Id.Value[..8]}.cs" ]);
        var driver = ReadyDriver(paths);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selection = new ConductorAcceptanceCohortSelection(
            [ReadyProjection(first.Id, paths[first.Id][0]), ReadyProjection(second.Id, paths[second.Id][0])], []);
        Assert.True(driver.TryRegisterCohortGateRunForTests(selection, completion));

        var receipt = TickReceipt(kernel, driver, ConductorAutonomyPolicy.Conservative);

        Assert.Contains($"{held.Id.Value[..8]}:DependencyHold", receipt, StringComparison.Ordinal);
        Assert.Contains($"{first.Id.Value[..8]}:CohortGateMember", receipt, StringComparison.Ordinal);
        Assert.Contains($"{second.Id.Value[..8]}:CohortGateMember", receipt, StringComparison.Ordinal);
        Assert.Equal(1, receipt.Split($"{held.Id.Value[..8]}:", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, receipt.Split($"{first.Id.Value[..8]}:", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, receipt.Split($"{second.Id.Value[..8]}:", StringSplitOptions.None).Length - 1);
        Assert.Contains("members=none", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptStatesAcceptanceWidthOccupiedWhenLiveGateFillsWidthOne()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "First ready goal");
        var second = CreateVerifiedSimpleGoal(kernel, "Second ready goal");
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [first.Id] = ["tests/Mcg.AgentOrchestrator.Core.Tests/First.cs"],
            [second.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Second.cs"]
        };
        var driver = ReadyDriver(paths);
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new GateLoadContextProbe.LiveGateOccupant(4101, "cccccccccccccccccccccccccccccccc", 0, TimeSpan.Zero)]);

        var receipt = TickReceipt(kernel, driver,
            ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 });

        Assert.Contains("ready=2", receipt, StringComparison.Ordinal);
        Assert.Contains("capacity=AcceptanceWidthOccupied(width=1,occupants=goal:cccccccc)",
            receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("CohortCapacity", receipt, StringComparison.Ordinal);
    }

    private static ConductorDriver ReadyDriver(IReadOnlyDictionary<GoalId, IReadOnlyList<string>> paths)
    {
        const string mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(goalId.Value.PadRight(40, 'b')[..40], mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        return MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: goal => paths[goal.Id],
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector);
    }

    [Fact]
    public void ConflictPaths_DoNotChangePlanReceiptAndResolvableConflictStaysExcluded()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Resolvable planner conflict");
        var writes = 0;
        var projector = new GateReadyCandidateProjector(
            _ => new(new string('b', 40), new string('a', 40)),
            _ => new(true, ["src/Mcg.AgentOrchestrator.Core/Feature.cs"]),
            (_, _, _) => new(false, ["src/ExactConflict.cs"]));
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly, isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector, writeEscalation: (_, _, _) => writes++);
        Assert.True(driver.ParallelAcceptanceEnabled);
        var probes = 0;
        driver.PreLandingMergeConflictProbe = _ =>
        {
            probes++;
            return new(new string('b', 40), new string('a', 40), AdditiveConflictProbeOutcome.Resolvable, "none");
        };
        using var occupancy = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new GateLoadContextProbe.LiveGateOccupant(4101, "cccccccccccccccccccccccccccccccc", 0, TimeSpan.Zero)]);

        var receipt = TickReceipt(kernel, driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 });

        Assert.Contains($"{goal.Id.Value[..8]}:MergeConflict", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("src/ExactConflict.cs", receipt, StringComparison.Ordinal);
        Assert.Equal(1, probes);
        Assert.Equal(0, writes);
    }

    private static string TickReceipt(
        AgentOrchestratorKernel kernel, ConductorDriver driver, ConductorAutonomyPolicy policy)
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"speculative-cohort-{Guid.NewGuid():N}.jsonl");
        try
        {
            new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(logPath))
                .Run(kernel, driver, policy, NoStopPath(), maxIterations: 1);
            var receipts = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "speculative-cohort-plan")
                .ToArray();
            return Assert.Single(receipts).Detail;
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    private static GateReadyCandidateProjection ReadyProjection(GoalId goalId, string path) =>
        new(goalId, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied,
            ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto,
            [path], [$"production:{goalId.Value[..8]}"],
            new GateReadyMergeEvidence(goalId.Value.PadRight(40, 'b')[..40],
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
}
