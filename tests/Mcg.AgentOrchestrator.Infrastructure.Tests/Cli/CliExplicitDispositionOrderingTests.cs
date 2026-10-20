using System.Text.Json;
using Xunit;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: application entry points return outcomes without Console capture;
// each test owns its SQLite store and deterministic competing-commit callback.
public sealed class CliExplicitDispositionOrderingTests : CliGoalParkTestSupport
{
    [Fact]
    public async Task Park_CasExhaustion_PublishesNoSpeculativeEffects()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root);
            var kernel = await seed.Repository.LoadAsync();
            kernel.RequestHumanInput(seed.GoalId, seed.TaskIds.Single(), "Keep unresolved");
            kernel.ObserveGoalHold(seed.GoalId, "existing", "existing blocker",
                DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(1));
            await seed.Repository.SaveAsync(kernel);
            var before = (await LoadGoalStateExactly(seed.Repository, seed.GoalId))!;
            var writer = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                StateAfterApplication = (attempt, _, token) => writer.TransactGoalStateAsync(
                    seed.GoalId, (state, transactionToken) => Task.FromResult((true,
                        (GoalStateSnapshot?)(state! with
                        {
                            Goal = state.Goal with { Objective = $"Competing commit {attempt}" }
                        }), true)), token)
            };
            var outcome = CliPersistentStateRunner.ExecuteGoalParkApplication(
                new(seed.GoalId.Value, "Must not commit", true), seed.GoalId, probe);
            Assert.Equal(CliCommandHandlers.GoalLifecycleTransitionDisposition.ConflictExhausted,
                outcome.Disposition);
            Assert.False(outcome.ShouldSave);
            Assert.True(probe.StateApplicationCount > 1);
            var after = (await LoadGoalStateExactly(seed.Repository, seed.GoalId))!;
            Assert.Equal($"Competing commit {probe.StateApplicationCount}", after.Goal.Objective);
            Assert.Equal(before.Goal.Status, after.Goal.Status);
            Assert.Equal(before.Goal.CurrentHold, after.Goal.CurrentHold);
            Assert.Null(after.Goal.ExplicitDisposition);
            Assert.Equal(JsonSerializer.Serialize(before.Goal.Timeline), JsonSerializer.Serialize(after.Goal.Timeline));
            Assert.Equal(JsonSerializer.Serialize(before.HumanInputRequests), JsonSerializer.Serialize(after.HumanInputRequests));
            Assert.Null(outcome.Goal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Park_CompetingExplicitCommand_ReevaluatesRevisionAfterCasLoss()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root);
            var writer = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                StateAfterApplication = (attempt, _, _) =>
                {
                    if (attempt == 1)
                    {
                        var committed = CliPersistentStateRunner.ExecuteGoalParkApplication(
                            new(seed.GoalId.Value, "First committed park", true), seed.GoalId, writer);
                        Assert.True(committed.ShouldSave);
                    }
                    return Task.CompletedTask;
                }
            };
            var outcome = CliPersistentStateRunner.ExecuteGoalParkApplication(
                new(seed.GoalId.Value, "Later committed park", true), seed.GoalId, probe);
            Assert.True(outcome.ShouldSave);
            Assert.Equal(2, probe.StateApplicationCount);
            var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
            Assert.Equal(new GoalExplicitDisposition(2, GoalExplicitDispositionAction.Park), stored.ExplicitDisposition);
            Assert.Single(stored.Timeline.Where(e => e.Message == "Goal parked: First committed park"));
            Assert.Single(stored.Timeline.Where(e => e.Message == "Goal parked: Later committed park"));
            var released = CliPersistentStateRunner.ExecuteGoalUnparkApplication(
                new(seed.GoalId.Value, "Later unpark", true), seed.GoalId, writer);
            Assert.True(released.ShouldSave);
            stored = (await writer.LoadGoalAsync(seed.GoalId))!;
            Assert.Equal(new GoalExplicitDisposition(3, GoalExplicitDispositionAction.Unpark), stored.ExplicitDisposition);
            Assert.Equal(GoalStatus.Active, stored.Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Park_CompletionCommitsFirst_RejectsAfterConflictReload()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root);
            var kernel = await seed.Repository.LoadAsync();
            var task = seed.TaskIds.Single();
            kernel.ReportTaskProgress(seed.GoalId, task, WorkTaskStatus.Completed, "Done");
            kernel.RecordTaskVerification(seed.GoalId, task,
                new TaskVerificationRecord("verify", root, 0, "passed", "", DateTimeOffset.UnixEpoch));
            kernel.BeginGoalAcceptanceVerification(seed.GoalId, "Already owned gate");
            await seed.Repository.SaveAsync(kernel);
            var writer = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath);
            var probe = new GoalTransactionProbeRepository(seed.Repository)
            {
                StateAfterApplication = async (attempt, state, token) =>
                {
                    Assert.Equal(1, attempt);
                    var completion = AgentOrchestratorKernel.FromSnapshot(new([state.Goal], state.HumanInputRequests));
                    completion.CompleteGoal(seed.GoalId, "Committed completion");
                    await writer.SaveAsync(completion, token);
                }
            };
            Assert.Throws<InvalidOperationException>(() =>
                CliPersistentStateRunner.ExecuteGoalParkApplication(
                    new(seed.GoalId.Value, "Stale park", true), seed.GoalId, probe));
            var stored = (await writer.LoadGoalAsync(seed.GoalId))!;
            Assert.Equal(GoalStatus.Completed, stored.Status);
            Assert.Null(stored.ExplicitDisposition);
            Assert.DoesNotContain(stored.Timeline, e => e.Message == "Goal parked: Stale park");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Unpark_RetainedCompletion_ReportsActualCommittedOutcome()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateActiveSeed(root);
            var kernel = await seed.Repository.LoadAsync();
            var task = seed.TaskIds.Single();
            kernel.ReportTaskProgress(seed.GoalId, task, WorkTaskStatus.Completed, "Done");
            kernel.RecordTaskVerification(seed.GoalId, task,
                new TaskVerificationRecord("verify", root, 0, "passed", "", DateTimeOffset.UnixEpoch));
            kernel.BeginGoalAcceptanceVerification(seed.GoalId, "Owned gate");
            kernel.ParkGoal(seed.GoalId, "Committed before completion");
            kernel.CompleteGoal(seed.GoalId, "Already landed");
            await seed.Repository.SaveAsync(kernel);
            var outcome = CliPersistentStateRunner.ExecuteGoalUnparkApplication(
                new(seed.GoalId.Value, "Release completed outcome", true), seed.GoalId, seed.Repository);
            Assert.True(outcome.ShouldSave);
            Assert.Equal(GoalStatus.Completed, outcome.ObservedStatus);
            Assert.Equal(GoalStatus.Completed, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
