using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsGraphRepairIntent
{
    [Xunit.Fact]
    public async Task TypedRepair_SurvivesStaleHolderCheckpointAndRestart_WithUnrelatedProgress()
    {
        using var fixture = new GraphRepairTestFixture();
        var holder = await fixture.Repository.LoadAsync();
        var baselines = holder.ExportSnapshot().Goals.ToDictionary(goal => goal.Id);
        var checkpoint = CliPersistentStateRunner.CreateCommandCheckpoint(fixture.Repository, fixture.Workspace, holder, baselines);
        using var reached = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var stale = Task.Run(() =>
        {
            holder.RecordTaskNote(fixture.Goal.Id, fixture.Goal.Tasks[0].Id, "stale-holder-progress");
            reached.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(30)), "Repair did not release the stale checkpoint.");
            return Xunit.Record.Exception(() => checkpoint(holder));
        });
        try
        {
            Assert.True(reached.Wait(TimeSpan.FromSeconds(30)), "Old-graph holder did not reach checkpoint gate.");
            var intent = fixture.Enqueue();
            fixture.Apply();
            await fixture.Repository.TransactAsync((current, _) =>
            {
                current.RecordTaskNote(fixture.Other.Id, fixture.Other.Tasks[0].Id, "unrelated-durable-progress");
                return Task.FromResult((true, true));
            });
            resume.Set();
            var error = await stale.WaitAsync(TimeSpan.FromSeconds(30));
            var restarted = await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath).LoadAsync();
            Assert.True(restarted.GetGoal(fixture.Goal.Id).Tasks.Count == 5,
                "Committed typed repair must survive stale-holder checkpoint and restart.");
            Assert.Contains(restarted.GetGoal(fixture.Other.Id).Timeline, item => item.Message == "unrelated-durable-progress");
            Assert.NotNull(error);
            Assert.Contains("COMMAND_CHECKPOINT_REJECTED", error.Message);
            Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
        }
        finally { resume.Set(); await stale.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Submission_ChangesOnlyInbox_ThenAppliesOrderedPlan(bool bare)
    {
        using var fixture = new GraphRepairTestFixture();
        var before = GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync());
        var path = Path.Combine(fixture.Root, "plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(GraphRepairTestFixture.FiveRoles, OperatorIntentJson.Options));
        string[] args = bare ? ["add-task", "Researcher", "Research", "--before-role", "Developer"] :
            ["repair-task-graph", "--plan-file", path];
        Goal? goal = fixture.Goal;
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var output = InfrastructureTestSupport.CaptureConsole(() =>
        {
            Assert.False(CliPersistentStateRunner.ExecuteCommand(args, fixture.Repository, fixture.Workspace,
                ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref goal));
        });
        Assert.Equal(before, GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync()));
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Contains("status=Pending", output);
        Assert.Contains(intent.Id, output);
        Assert.Contains(intent.IdempotencyKey, output);
        Assert.Contains("fresh-key", output);
        var applied = fixture.Apply();
        Assert.Equal(bare ? new[] { AgentRole.Researcher, AgentRole.Developer, AgentRole.Reviewer } :
            new[] { AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer },
            applied.GetGoal(fixture.Goal.Id).Tasks.Select(task => task.RequiredRole));
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Single(applied.GetGoal(fixture.Goal.Id).Timeline.Where(item => item.OperatorIntentApplied?.IntentId == intent.Id));
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing-target")]
    [Xunit.InlineData("ambiguous-target")]
    [Xunit.InlineData("final-order-mismatch")]
    [Xunit.InlineData("invalid-insertion")]
    [Xunit.InlineData("empty-description")]
    [Xunit.InlineData("invalid-plan")]
    public async Task RejectedPlan_HasTypedReason_AndZeroPartialGraphOrProgress(string failure)
    {
        using var fixture = new GraphRepairTestFixture();
        var plan = failure switch
        {
            "missing-target" => new TaskGraphRepairOperatorIntentPayload([new("Planner", "Valid", "Developer"), new("Tester", "Bad target", "Researcher")]),
            "ambiguous-target" => new([new("Developer", "Second developer"), new("Tester", "Ambiguous", "Developer")]),
            "final-order-mismatch" => GraphRepairTestFixture.FiveRoles with { ExpectedFinalRoleOrder = ["Developer", "Reviewer"] },
            "invalid-insertion" => new([new("Planner", "Valid", "Developer"), new("Banana", "Invalid")]),
            "empty-description" => new([new("Planner", "Valid", "Developer"), new("Tester", " ")]),
            _ => new([], Version: 2)
        };
        var before = GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync());
        var beforeTarget = GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadGoalsAsync([fixture.Goal.Id]));
        var intent = fixture.Enqueue(plan);
        var rejectedKernel = fixture.Apply();
        var result = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, result.Status);
        Assert.Contains(failure == "empty-description" ? "invalid-insertion" : failure, result.Outcome);
        Assert.Equal(beforeTarget, GraphRepairTestFixture.Snapshot(rejectedKernel));
        Assert.Equal(before, GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync()));
    }

    [Xunit.Fact]
    public async Task MalformedPayload_RejectsWithoutMutation_AndIsNotBatched()
    {
        using var fixture = new GraphRepairTestFixture();
        var before = GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync());
        var intent = await fixture.Store.EnqueueAsync(new(Guid.NewGuid().ToString("N"), "malformed", OperatorIntentVerbs.TaskGraphRepair,
            fixture.Goal.Id.Value, null, "{", [], "operator", "cli", "local-process", DateTimeOffset.UtcNow));
        fixture.Apply();
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Equal(before, GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync()));
        Assert.False(OperatorIntentBatchPolicy.CanShareTick(OperatorIntentVerbs.TaskGraphRepair));
    }

    [Xunit.Fact]
    public async Task MalformedPlanFile_FailsBeforeEnqueue()
    {
        using var fixture = new GraphRepairTestFixture();
        var before = GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync());
        var path = Path.Combine(fixture.Root, "malformed.json");
        File.WriteAllText(path, "{\"insertions\":[],\"unexpectedField\":true}");
        var error = Assert.Throws<ArgumentException>(() => CliTaskGraphRepairCommand.Submit(
            ["repair-task-graph", "--plan-file", path], fixture.Workspace, fixture.Goal));
        Assert.Contains("invalid-plan-json", error.Message);
        Assert.Empty(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(before, GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync()));
    }
}
