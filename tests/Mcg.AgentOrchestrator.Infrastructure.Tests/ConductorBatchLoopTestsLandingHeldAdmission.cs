using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The collection isolates the process-wide occupied-width probe.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLandingHeldAdmission : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsLandingHeldAdmission(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void Tick_OperatorWait_ExcludesHeldGoalFromPlan()
    {
        var (goals, wait, receipt) = TickReceipt("operator");

        Assert.Contains("ready=3", receipt, StringComparison.Ordinal);
        Assert.Contains($"{goals[0].Id.Value[..8]}:LandingHeld(request={wait.Id.Value[..8]})", receipt);
        Assert.Equal(goals.Skip(1).Select(goal => goal.Id.Value[..8]).Order(StringComparer.Ordinal),
            Members(receipt).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Tick_GateSatisfiableWait_KeepsAllFourGoalsInPlan()
    {
        var (goals, _, receipt) = TickReceipt("acceptance-gate");

        Assert.Contains("ready=4", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("LandingHeld", receipt, StringComparison.Ordinal);
        Assert.Equal(goals.Select(goal => goal.Id.Value[..8]).Order(StringComparer.Ordinal),
            Members(receipt).Order(StringComparer.Ordinal));
    }

    private static string[] Members(string receipt) =>
        receipt.Split("members=", StringSplitOptions.None)[1].Split(' ')[0].Split(',');

    private static (Goal[] Goals, HumanInputRequest Wait, string Receipt) TickReceipt(string owner)
    {
        var kernel = new AgentOrchestratorKernel();
        var goals = Enumerable.Range(0, 4).Select(index =>
            CreateVerifiedSimpleGoal(kernel, $"Landing hold admission {index}")).ToArray();
        var wait = kernel.RequestHumanInputDeduplicated(goals[0].Id, null, "Supply prospective evidence",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
            testNames: ["GateTests"]).Request;
        var paths = goals.ToDictionary(goal => goal.Id,
            goal => (IReadOnlyList<string>)[$"tests/Mcg.AgentOrchestrator.Core.Tests/{goal.Id.Value[..8]}.cs"]);
        var projector = new GateReadyCandidateProjector(
            id => new GateReadyCandidateRevisionPair(id.Value.PadRight(40, 'b')[..40], new string('a', 40)),
            id => new GateReadyLandingScopeObservation(true, paths[id]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: goal => paths[goal.Id], isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: projector);
        // Prove each input is ready before the tick adds the pending-wait exclusion.
        Assert.All(goals, goal => Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative)));
        using var probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            [new GateLoadContextProbe.LiveGateOccupant(4101, "cccccccccccccccccccccccccccccccc", 0, TimeSpan.Zero)]);
        var logPath = Path.Combine(Path.GetTempPath(), $"landing-held-{Guid.NewGuid():N}.jsonl");
        try
        {
            new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(logPath))
                .Run(kernel, driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 },
                    NoStopPath(), maxIterations: 1);
            var receipts = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "speculative-cohort-plan").ToArray();
            return (goals, wait, Assert.Single(receipts).Detail);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }
}
