using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AdjudicationPreconditionTests
{
    [Xunit.Fact]
    public void Captured_facts_ignore_version_drift_and_detect_each_semantic_change()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, AgentCatalog.Default().Agents, "Adjudication precondition");
        var task = goal.Tasks.Single();
        var captured = AdjudicationPrecondition.Capture(goal, task);
        var payload = new AdjudicateOperatorIntentPayload(
            "close", "reason", ["receipt-1"], -1, "C:\\tmp", Precondition: captured);

        Assert.False(AdjudicationPrecondition.IsStale(payload, goal, task));
        Assert.True(AdjudicationPrecondition.IsStale(payload with
            { Precondition = captured with { TaskStatus = nameof(WorkTaskStatus.Failed) } }, goal, task));
        Assert.True(AdjudicationPrecondition.IsStale(payload with
            { Precondition = captured with { TaskDispatchedAtUtcTicks = 1 } }, goal, task));
        Assert.True(AdjudicationPrecondition.IsStale(payload with
            { Precondition = captured with { TaskLatestRetryAtUtcTicks = 1 } }, goal, task));
        Assert.True(AdjudicationPrecondition.IsStale(payload with
            { Precondition = captured with { GoalStatus = nameof(GoalStatus.Verified) } }, goal, task));

        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow,
            ResultCommit: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.True(AdjudicationPrecondition.IsStale(payload, goal, task));
    }

    [Xunit.Fact]
    public void Legacy_payload_remains_parseable_and_unconstrained()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, AgentCatalog.Default().Agents, "Legacy adjudication");
        var task = goal.Tasks.Single();
        var payload = JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(
            """{"shape":"close","text":"reason","evidenceReferences":[],"expectedGoalStateVersion":-1,"workingDirectory":"C:\\tmp"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Null(payload.Precondition);
        Assert.False(AdjudicationPrecondition.IsStale(payload, goal, task));
    }
}
