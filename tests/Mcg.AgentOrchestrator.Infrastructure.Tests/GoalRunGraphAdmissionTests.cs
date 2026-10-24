using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its SQLite stores; launch callbacks stop before process creation.
public sealed class GoalRunGraphAdmissionTests
{
    [Xunit.Fact]
    public async Task ActiveStateWrite_RejectsBeforeAdvanceOrLaunch()
    {
        using var fixture = new GraphRepairTestFixture();
        var advanceCalls = 0;
        var launchCalls = 0;
        await fixture.Repository.TransactWithOutboxAsync((kernel, _) =>
        {
            Assert.True(StateDbWriteSession.IsActiveFor(fixture.Workspace.SqliteStatePath));
            // An already committed graph isolates the transaction-ownership guard:
            // without it the fake launch is reached, rather than a nested-writer timeout.
            var target = kernel.GetGoal(fixture.Goal.Id);
            var failure = Assert.Throws<InvalidOperationException>(() =>
                RunGoalService.RunAsync(kernel, [], new WorkerProfileCatalog([]), fixture.Workspace, target, false,
                    advanceStep: (_, _, _, _, goal, _, _) =>
                    {
                        advanceCalls++;
                        var admission = fixture.Store.CaptureAdmission(goal.Id.Value, goal.Tasks[0].Id.Value,
                            TaskGraphIdentity.Compute(goal), fixture.Workspace.SqliteStatePath);
                        TaskIntentAdmission.Launch(admission, "uncommitted-launch", () =>
                        {
                            launchCalls++;
                            throw new ProbeReached();
                        });
                        return Stopped(goal);
                    }).GetAwaiter().GetResult());
            Assert.Contains("RUN_GOAL_REQUIRES_COMMITTED_STATE", failure.Message);
            return Task.FromResult((false, false, (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
        });
        Assert.Equal(0, advanceCalls);
        Assert.Equal(0, launchCalls);
        Assert.NotNull(await fixture.Repository.LoadGoalAsync(fixture.Goal.Id));
    }

    [Xunit.Fact]
    public async Task NewGoal_CommitsTargetBeforeAdvance_AndAllowsBoundAdmission()
    {
        using var fixture = new GraphRepairTestFixture();
        var before = await fixture.Repository.LoadAsync();
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal("Standalone run", [new(TaskId.New(), "Plan", AgentRole.Planner)]);
        var unowned = kernel.CreateGoal("Unrelated in-memory work", [new(TaskId.New(), "Review", AgentRole.Reviewer)]);
        var question = kernel.RequestHumanInput(target.Id, target.Tasks[0].Id, "Target request");
        var advanceCalls = 0;
        var launchCalls = 0;

        await RunGoalService.RunAsync(kernel, [], new WorkerProfileCatalog([]), fixture.Workspace, target, false,
            advanceStep: (_, _, _, _, goal, _, _) =>
            {
                advanceCalls++;
                var prepared = fixture.Store.CaptureAdmission(goal.Id.Value, goal.Tasks[0].Id.Value,
                    TaskGraphIdentity.Compute(goal), fixture.Workspace.SqliteStatePath);
                var failure = Xunit.Record.Exception(() => TaskIntentAdmission.Launch(prepared, "standalone-first", () =>
                {
                    launchCalls++;
                    throw new ProbeReached();
                }));
                Assert.IsType<ProbeReached>(failure);
                return Stopped(goal);
            });

        Assert.Equal(1, advanceCalls);
        Assert.True(launchCalls == 1, "A standalone goal must have a committed graph before its first native admission.");
        var reloaded = await fixture.Repository.LoadAsync();
        Assert.Equal(TaskGraphIdentity.Compute(target), TaskGraphIdentity.Compute(reloaded.GetGoal(target.Id)));
        Assert.Contains(reloaded.HumanInputRequests, request => request.Id == question.Id);
        Assert.DoesNotContain(reloaded.Goals, goal => goal.Id == unowned.Id);
        // Creation owns only the target, including its requests; no caller-wide state replacement.
        Assert.Equal(before.Goals.Count + 1, reloaded.Goals.Count);
        foreach (var existing in before.Goals)
            Assert.Equal(JsonSerializer.Serialize(before.ExportGoalSnapshot(existing.Id)),
                JsonSerializer.Serialize(reloaded.ExportGoalSnapshot(existing.Id)));
        Assert.Equal(JsonSerializer.Serialize(before.ExportSnapshot().HumanInputRequests),
            JsonSerializer.Serialize(reloaded.ExportSnapshot().HumanInputRequests
                .Where(request => request.GoalId != target.Id.Value).ToArray()));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, "pending-graph-repair")]
    [Xunit.InlineData(true, "prepared-graph-changed")]
    public async Task ExistingGoal_PreservesRepair_AndRejectsStaleAdmission(bool apply, string reason)
    {
        using var fixture = new GraphRepairTestFixture();
        var stale = await fixture.Repository.LoadAsync();
        var target = stale.GetGoal(fixture.Goal.Id);
        var intent = fixture.Enqueue();
        if (apply) fixture.Apply();
        var before = GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync());
        var advanceCalls = 0;
        var launchCalls = 0;

        await RunGoalService.RunAsync(stale, [], new WorkerProfileCatalog([]), fixture.Workspace, target, false,
            advanceStep: (_, _, _, _, goal, _, _) =>
            {
                advanceCalls++;
                var prepared = fixture.Store.CaptureAdmission(goal.Id.Value, goal.Tasks[0].Id.Value,
                    TaskGraphIdentity.Compute(goal), fixture.Workspace.SqliteStatePath);
                var failure = Xunit.Record.Exception(() => TaskIntentAdmission.Launch(prepared, "standalone-stale", () =>
                {
                    launchCalls++;
                    throw new ProbeReached();
                }));
                Assert.Equal(reason, Assert.IsType<TaskIntentDispatchSuppressedException>(failure).Receipt.Reason);
                return Stopped(goal);
            });

        Assert.Equal(1, advanceCalls);
        Assert.Equal(0, launchCalls);
        Assert.Equal(before, GraphRepairTestFixture.Snapshot(await fixture.Repository.LoadAsync()));
        Assert.Equal(apply ? OperatorIntentStatus.Applied : OperatorIntentStatus.Pending,
            (await fixture.Store.GetAsync(intent.Id))!.Status);
    }

    private static GoalAdvanceLoopOutcome Stopped(Goal goal) =>
        new(goal.Id, false, 0, "Admission probe complete.", null, []);

    private sealed class ProbeReached : Exception;
}
