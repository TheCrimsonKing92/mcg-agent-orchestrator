using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalPriorityOperatorIntentTests
{
    [Xunit.Fact]
    public async Task SubmissionQueuesThenTickAppliesAndRestoresPriorityWithAudit()
    {
        var (kernel, goal, context, store) = Fixture();
        const string rationale = "Release the waiting dependency";
        InfrastructureTestSupport.CaptureConsole(() => CliGoalPriorityCommand.Execute(
            ["goal-priority", goal.Id.Value[..8], "CRITICAL", "--reason", rationale, "--operator-actor", "priority-operator"], context, store));
        var intent = Assert.Single(await store.ListForGoalAsync(goal.Id.Value));
        Assert.Equal(OperatorIntentVerbs.GoalPriority, intent.Verb);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Null(intent.TaskId);
        Assert.Equal(GoalPriority.Normal, goal.Priority);
        var payload = JsonSerializer.Deserialize<GoalPriorityOperatorIntentPayload>(intent.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("critical", payload.Level);
        Assert.Equal(rationale, payload.Rationale);
        var coordinator = new OperatorIntentCoordinator(store);
        coordinator.ExecutePending(kernel, goal);
        Assert.Equal(GoalPriority.Critical, goal.Priority);
        var change = Assert.Single(goal.Timeline.Where(evt => evt.Message.StartsWith("Goal priority set:")));
        Assert.Contains("previous=normal", change.Message);
        Assert.Contains("new=critical", change.Message);
        Assert.Contains(intent.Actor, change.Message);
        Assert.Contains(rationale, change.Message);
        coordinator.CompletePersisted([goal.Id]);
        InfrastructureTestSupport.CaptureConsole(() => CliGoalPriorityCommand.Execute(
            ["goal-priority", goal.Id.Value[..8], "normal"], context, store));
        coordinator.ExecutePending(kernel, goal);
        Assert.Equal(GoalPriority.Normal, goal.Priority);
        var changes = goal.Timeline.Where(evt => evt.Message.StartsWith("Goal priority set:")).ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Contains("previous=critical new=normal", changes[1].Message);
    }

    [Xunit.Theory]
    [Xunit.InlineData("urgent-ish")]
    [Xunit.InlineData("2")]
    [Xunit.InlineData("")]
    public async Task InvalidSubmissionWritesNoIntent(string level)
    {
        var (_, goal, context, store) = Fixture();
        var exception = Assert.Throws<ArgumentException>(() => CliGoalPriorityCommand.Execute(
            ["goal-priority", goal.Id.Value[..8], level], context, store));
        Assert.Contains("low, normal, high, critical", exception.Message);
        Assert.Empty(await store.ListForGoalAsync(goal.Id.Value));
        Assert.Empty(await store.ListActionableGoalIdsAsync());
    }

    [Xunit.Fact]
    public async Task InvalidPersistedPayloadIsRejectedWithoutPriorityMutation()
    {
        var (kernel, goal, _, store) = Fixture();
        var id = Guid.NewGuid().ToString("N");
        await store.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.GoalPriority,
            goal.Id.Value, null, "{\"level\":\"urgent-ish\",\"rationale\":\"bad row\"}", [],
            "test", "cli", "local", DateTimeOffset.UtcNow));
        new OperatorIntentCoordinator(store).ExecutePending(kernel, goal);
        Assert.Equal(OperatorIntentStatus.Rejected, (await store.GetAsync(id))!.Status);
        Assert.Equal(GoalPriority.Normal, goal.Priority);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Message.StartsWith("Goal priority set:"));
    }

    [Xunit.Fact]
    public async Task JsonSubmissionIncludesPayloadAndIdempotencyKeyDeduplicates()
    {
        var (_, goal, context, store) = Fixture();
        var args = new[] { "goal-priority", goal.Id.Value[..8], "high", "--reason", "Expedite", "--idempotency-key", "priority-request", "--json" };
        var output = InfrastructureTestSupport.CaptureConsole(() => CliGoalPriorityCommand.Execute(args, context, store));
        using var document = JsonDocument.Parse(output);
        Assert.Equal("mcg.operator-intent", document.RootElement.GetProperty("schema").GetString());
        var receipt = document.RootElement.GetProperty("payload");
        Assert.Equal("goal-priority", receipt.GetProperty("verb").GetString());
        Assert.Equal(goal.Id.Value, receipt.GetProperty("goalId").GetString());
        Assert.Equal("Pending", receipt.GetProperty("status").GetString());
        Assert.Equal("high", receipt.GetProperty("payload").GetProperty("level").GetString());
        Assert.Equal("Expedite", receipt.GetProperty("payload").GetProperty("rationale").GetString());
        InfrastructureTestSupport.CaptureConsole(() => CliGoalPriorityCommand.Execute(args, context, store));
        var intent = Assert.Single(await store.ListForGoalAsync(goal.Id.Value));
        Assert.Equal("priority-request", intent.IdempotencyKey);
        Assert.Equal(receipt.GetProperty("id").GetString(), intent.Id);
        Assert.Equal(GoalPriority.Normal, goal.Priority);
    }

    [Xunit.Fact]
    public async Task UnknownGoalWritesNoIntent()
    {
        var (_, _, context, store) = Fixture();
        Assert.Throws<KeyNotFoundException>(() => CliGoalPriorityCommand.Execute(
            ["goal-priority", "unknown-goal", "critical"], context, store));
        Assert.Empty(await store.ListActionableGoalIdsAsync());
    }

    private static (AgentOrchestratorKernel, Goal, CliExecutionContext, SqliteOperatorIntentStore) Fixture()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Priority intent");
        var root = Path.Combine(Path.GetTempPath(), $"mcg-priority-intent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var context = new CliExecutionContext(kernel, workspace, new InMemoryModelProviderRegistry([]),
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), goal);
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        return (kernel, goal, context, store);
    }
}
