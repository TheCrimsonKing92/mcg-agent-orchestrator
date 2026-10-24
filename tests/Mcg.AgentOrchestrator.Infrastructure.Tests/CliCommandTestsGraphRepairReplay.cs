using Mcg.AgentOrchestrator.App.Cli;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsGraphRepairReplay
{
    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    public async Task Replay_BeforeAndAfterCommitAndRestart_ReturnsOriginalOperation(bool explicitKey, bool planForm)
    {
        using var fixture = new GraphRepairTestFixture();
        var planPath = Path.Combine(fixture.Root, "original-plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(GraphRepairTestFixture.FiveRoles, OperatorIntentJson.Options));
        var args = planForm ? new List<string> { "repair-task-graph", "--plan-file", planPath } :
            new List<string> { "add-task", "Tester", "Verify", "--before-role", "Reviewer" };
        if (explicitKey) args.AddRange(["--idempotency-key", "fixed-operation"]);
        Submit(fixture, args);
        var original = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        if (planForm)
        {
            var movedPath = Path.Combine(fixture.Root, "same-contents-another-path.json");
            File.Copy(planPath, movedPath);
            args[2] = movedPath;
        }
        Submit(fixture, args);
        Assert.Equal(original.Id, Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value)).Id);
        fixture.Apply(complete: false); // graph + typed outcome committed, inbox completion lost
        var afterCommit = await fixture.Repository.LoadAsync();
        Assert.Equal(planForm ? 5 : 3, afterCommit.GetGoal(fixture.Goal.Id).Tasks.Count);
        Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Store.GetAsync(original.Id))!.Status);
        Submit(fixture, args);
        fixture.Apply(); // a new coordinator recovers the durable marker before expiry/replay
        Submit(fixture, args);
        var restarted = new SqliteOperatorIntentStore(Path.Combine(fixture.Workspace.OrchestratorDirectory,
            SqliteOperatorIntentStore.DatabaseFileName), fixture.Workspace.LogDirectory);
        var applied = Assert.Single(await restarted.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(original.Id, applied.Id);
        Assert.Equal(original.IdempotencyKey, applied.IdempotencyKey);
        Assert.Equal(OperatorIntentStatus.Applied, applied.Status);
        var final = await fixture.Repository.LoadAsync();
        Assert.Equal(planForm ? 5 : 3, final.GetGoal(fixture.Goal.Id).Tasks.Count);
        Assert.Single(final.GetGoal(fixture.Goal.Id).Timeline.Where(item => item.OperatorIntentApplied?.IntentId == original.Id));
        string[] distinct = ["add-task", "Tester", "Verify", "--before-role", "Reviewer", "--idempotency-key", "distinct-later-operation"];
        Submit(fixture, distinct);
        fixture.Apply();
        Assert.Equal(planForm ? 6 : 4, (await fixture.Repository.LoadAsync()).GetGoal(fixture.Goal.Id).Tasks.Count);
        Assert.Equal(2, (await restarted.ListForGoalAsync(fixture.Goal.Id.Value)).Count);
    }

    [Xunit.Fact]
    public async Task ConflictingExplicitKey_RejectsCompleteChangedRequest()
    {
        using var fixture = new GraphRepairTestFixture();
        Submit(fixture, ["add-task", "Tester", "First", "--idempotency-key", "same"]);
        Assert.Throws<InvalidOperationException>(() => Submit(fixture, ["add-task", "Tester", "Changed", "--idempotency-key", "same"]));
        Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(2, (await fixture.Repository.LoadAsync()).GetGoal(fixture.Goal.Id).Tasks.Count);
    }

    [Xunit.Fact]
    public async Task CrashBeforeGraphCommit_ReloadReappliesExactlyOnce()
    {
        using var fixture = new GraphRepairTestFixture();
        var intent = fixture.Enqueue();
        var lost = await fixture.Repository.LoadGoalsAsync([fixture.Goal.Id]);
        new OperatorIntentCoordinator(fixture.Store).ExecutePending(lost, lost.GetGoal(fixture.Goal.Id));
        Assert.Equal(2, (await fixture.Repository.LoadAsync()).GetGoal(fixture.Goal.Id).Tasks.Count);
        fixture.Apply();
        fixture.Apply();
        var restarted = await fixture.Repository.LoadAsync();
        Assert.Equal(5, restarted.GetGoal(fixture.Goal.Id).Tasks.Count);
        Assert.Single(restarted.GetGoal(fixture.Goal.Id).Timeline.Where(item => item.OperatorIntentApplied?.IntentId == intent.Id));
    }

    [Xunit.Fact]
    public async Task Apply_PreservesLifecycleHold()
    {
        using var fixture = new GraphRepairTestFixture();
        var loaded = await fixture.Repository.LoadAsync();
        loaded.ParkGoal(fixture.Goal.Id, "Operator proof hold");
        loaded.ObserveGoalHold(fixture.Goal.Id, "operator", "Operator proof hold", DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
        var hold = loaded.ExportGoalSnapshot(fixture.Goal.Id).CurrentHold;
        await fixture.Repository.SaveAsync(loaded);
        fixture.Enqueue();
        var kernel = await fixture.Repository.LoadGoalsAsync([fixture.Goal.Id]);
        var attempts = new ConductorParallelAcceptanceAttemptCoordinator(Path.Combine(fixture.Root, "attempts"), "main", fixture.Root);
        var result = OperatorIntentGoalApplication.ApplyPending(kernel, kernel.GetGoal(fixture.Goal.Id), new(fixture.Store), attempts);
        Assert.True(result.Result.MutatedGoalState);
        Assert.True(result.Result.PreserveGoalHold);
        Assert.Equal(GoalStatus.Parked, kernel.GetGoal(fixture.Goal.Id).Status);
        Assert.Equal(hold, kernel.ExportGoalSnapshot(fixture.Goal.Id).CurrentHold);
    }

    private static void Submit(GraphRepairTestFixture fixture, IReadOnlyList<string> args)
    {
        var output = InfrastructureTestSupport.CaptureConsole(() =>
        {
            var kernel = fixture.Repository.LoadGoalsAsync([fixture.Goal.Id]).GetAwaiter().GetResult();
            CliTaskGraphRepairCommand.Submit(args, fixture.Workspace, kernel.GetGoal(fixture.Goal.Id));
        });
        Assert.Contains("Operator intent queued:", output);
    }
}
