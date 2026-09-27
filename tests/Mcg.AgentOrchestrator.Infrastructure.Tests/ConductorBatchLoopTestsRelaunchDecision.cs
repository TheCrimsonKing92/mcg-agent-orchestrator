using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsRelaunchDecision : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsRelaunchDecision(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void EachSuccessfulLandingEmitsItsRelaunchDecision()
    {
        var docs = RunLanding("docs/operator-runbook.md");
        Assert.Contains("LOOP_RELAUNCH_NOT_REQUIRED", docs.Output, StringComparison.Ordinal);
        Assert.Contains($"goal={docs.GoalId}", docs.Output, StringComparison.Ordinal);
        Assert.Contains("classification=documentation", docs.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_RELAUNCH_SCHEDULED", docs.Output, StringComparison.Ordinal);
        Assert.Equal(0, docs.RelaunchCalls);
        var docsDecision = Assert.Single(docs.Events.Where(record =>
            record.Detail.StartsWith("LOOP_RELAUNCH_NOT_REQUIRED", StringComparison.Ordinal)));
        Assert.Equal("loop-relaunch", docsDecision.EventKind);
        Assert.Equal(docs.GoalId, docsDecision.GoalId);

        var kernel = RunLanding("src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.GoalLifecycle.cs");
        Assert.Contains("LOOP_RELAUNCH_SCHEDULED", kernel.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_RELAUNCH_NOT_REQUIRED", kernel.Output, StringComparison.Ordinal);
        Assert.True(kernel.RelaunchCalls > 0);
        var kernelDecision = Assert.Single(kernel.Events.Where(record =>
            record.Detail.StartsWith("LOOP_RELAUNCH_SCHEDULED", StringComparison.Ordinal)));
        Assert.Equal("loop-relaunch", kernelDecision.EventKind);
        Assert.Equal(kernel.GoalId, kernelDecision.GoalId);
    }

    [Xunit.Fact]
    public void EachSuccessfulLandingEmitsDecisionWhenSelfRelaunchDisabled()
    {
        var kernel = RunLanding(
            "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.GoalLifecycle.cs",
            selfRelaunchEnabled: false);

        Assert.Contains("LOOP_RELAUNCH_NOT_REQUIRED", kernel.Output, StringComparison.Ordinal);
        Assert.Contains($"goal={kernel.GoalId}", kernel.Output, StringComparison.Ordinal);
        Assert.Contains("classification=self-relaunch-disabled", kernel.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_RELAUNCH_SCHEDULED", kernel.Output, StringComparison.Ordinal);
        Assert.Equal(0, kernel.RelaunchCalls);
        var decision = Assert.Single(kernel.Events.Where(record =>
            record.Detail.StartsWith("LOOP_RELAUNCH_NOT_REQUIRED", StringComparison.Ordinal)));
        Assert.Equal("loop-relaunch", decision.EventKind);
        Assert.Equal(kernel.GoalId, decision.GoalId);
    }

    private static LandingObservation RunLanding(string changedPath, bool selfRelaunchEnabled = true)
    {
        var root = CreateTempDirectory("mcg-relaunch-decision");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, $"Land {changedPath}");
        var landed = false;
        var relaunchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => landed
                ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                : new GoalLifecycleFacts(WorkspaceExists: true),
            land: candidate =>
            {
                landed = true;
                return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8],
                    new LandingDecision.Promote(), "integration", true, "Landed");
            },
            getLandingFileScopes: _ => [changedPath]);

        var output = AsyncLocalConsoleRouter.Capture(() =>
            new ConductorBatchLoop(
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    return new ConductorSelfRelaunchResult(false, "build", "test stop");
                },
                selfRelaunchEnabled: selfRelaunchEnabled,
                conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 3));

        var events = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        return new LandingObservation(goal.Id.Value, output, relaunchCalls, events);
    }

    private sealed record LandingObservation(
        string GoalId,
        string Output,
        int RelaunchCalls,
        ConductEventRecord[] Events);
}
