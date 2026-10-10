using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorIntentCoordinatorTerminalCompletionTests
{
    [Xunit.Fact]
    public async Task AlreadyTerminalCompletionDoesNotBlockNextIntentOrApplyFirstTwice()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal, store, first) = await CreateScenario(root);
            var second = CreateIntent(goal, goal.Tasks.Single(), root, "second", first.CreatedAt.AddTicks(1));
            await store.EnqueueAsync(second);
            var coordinator = new OperatorIntentCoordinator(store);

            var appliedFirst = coordinator.ExecutePending(kernel, goal);
            Xunit.Assert.Contains(appliedFirst.ProgressLines, line => line.Contains($"id={first.Id}", StringComparison.Ordinal) &&
                line.Contains("result=applied-pending-commit", StringComparison.Ordinal));
            await store.CompleteAsync(first.Id, OperatorIntentCoordinator.ClaimOwner,
                OperatorIntentStatus.Applied, "first attempt", DateTimeOffset.UtcNow);

            coordinator.CompletePersisted([goal.Id]);
            Xunit.Assert.Equal("first attempt", (await store.GetAsync(first.Id))!.Outcome);
            var appliedSecond = coordinator.ExecutePending(kernel, goal);
            Xunit.Assert.True(appliedSecond.MutatedGoalState);
            Xunit.Assert.Contains(appliedSecond.ProgressLines, line => line.Contains($"id={second.Id}", StringComparison.Ordinal) &&
                line.Contains("result=applied-pending-commit", StringComparison.Ordinal));
            Xunit.Assert.DoesNotContain(appliedSecond.ProgressLines, line => line.Contains("waiting-for-state-commit", StringComparison.Ordinal));
            coordinator.CompletePersisted([goal.Id]);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(second.Id))!.Status);
            Xunit.Assert.Equal(1, goal.Timeline.Count(item => item.OperatorIntentApplied?.IntentId == first.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("claimed")]
    [Xunit.InlineData("different-owner")]
    [Xunit.InlineData("read-fails")]
    public async Task OtherCompletionFailuresRemainPending(string caseName)
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal, store, first) = await CreateScenario(root);
            var failing = new FailingCompletionStore(store, caseName);
            var coordinator = new OperatorIntentCoordinator(failing);
            coordinator.ExecutePending(kernel, goal);

            var error = Xunit.Assert.Throws<InvalidOperationException>(() => coordinator.CompletePersisted([goal.Id]));
            Xunit.Assert.Equal("operator intent store unavailable", error.Message);
            Xunit.Assert.Equal(OperatorIntentStatus.Claimed, (await store.GetAsync(first.Id))!.Status);
            var waiting = coordinator.ExecutePending(kernel, goal);
            Xunit.Assert.Equal($"OPERATOR_INTENT goal={goal.Id.Value[..8]} result=waiting-for-state-commit count=1",
                Xunit.Assert.Single(waiting.ProgressLines));

            failing.FailCompletion = false;
            coordinator.CompletePersisted([goal.Id]);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(first.Id))!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(AgentOrchestratorKernel Kernel, Goal Goal, SqliteOperatorIntentStore Store, OperatorIntentRecord First)>
        CreateScenario(string root)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, AgentCatalog.Default().Agents, "Complete queued operator intents");
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Work completed.");
        var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), Path.Combine(root, "logs"));
        var first = CreateIntent(goal, task, root, "first", DateTimeOffset.UtcNow);
        await store.EnqueueAsync(first);
        return (kernel, goal, store, first);
    }

    private static OperatorIntentRecord CreateIntent(Goal goal, TaskSpec task, string root, string suffix, DateTimeOffset createdAt)
    {
        var verification = new TaskVerificationRecord(
            "manual-verification passed", root, 0, "Operator inspected the result.", string.Empty,
            createdAt, ModelFitNote: "OpenAI/gpt-test - adequate");
        return new OperatorIntentRecord(
            "verify-" + suffix, "verify-key-" + suffix, OperatorIntentVerbs.VerifyManual,
            goal.Id.Value, task.Id.Value,
            JsonSerializer.Serialize(new ManualVerificationOperatorIntentPayload(verification),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            [Path.Combine(root, "evidence.md")], "operator", "dashboard", "dashboard-operator-control", createdAt);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "operator-intent-coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FailingCompletionStore(IOperatorIntentStore inner, string caseName) : IOperatorIntentStore
    {
        public bool FailCompletion { get; set; } = true;

        public Task<OperatorIntentRecord> EnqueueAsync(OperatorIntentRecord intent, CancellationToken cancellationToken = default) =>
            inner.EnqueueAsync(intent, cancellationToken);

        public Task<OperatorIntentRecord?> ClaimNextAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) =>
            inner.ClaimNextAsync(goalId, claimOwner, cancellationToken);

        public Task<OperatorIntentRecord?> ClaimNextByVerbAsync(string goalId, string verb, string claimOwner, CancellationToken cancellationToken = default) =>
            inner.ClaimNextByVerbAsync(goalId, verb, claimOwner, cancellationToken);

        public Task<OperatorIntentRecord?> ClaimNextPendingAsync(string goalId, string claimOwner, CancellationToken cancellationToken = default) =>
            inner.ClaimNextPendingAsync(goalId, claimOwner, cancellationToken);

        public Task CompleteAsync(string intentId, string claimOwner, OperatorIntentStatus status, string outcome,
            DateTimeOffset completedAt, CancellationToken cancellationToken = default) =>
            FailCompletion
                ? Task.FromException(new InvalidOperationException("operator intent store unavailable"))
                : inner.CompleteAsync(intentId, claimOwner, status, outcome, completedAt, cancellationToken);

        public async Task<OperatorIntentRecord?> GetAsync(string intentId, CancellationToken cancellationToken = default)
        {
            if (caseName == "read-fails")
            {
                throw new IOException("operator intent read unavailable");
            }

            var stored = await inner.GetAsync(intentId, cancellationToken);
            return caseName == "different-owner" && stored is not null
                ? stored with { Status = OperatorIntentStatus.Applied, ClaimOwner = "other-owner" }
                : stored;
        }

        public Task<IReadOnlyList<OperatorIntentRecord>> ListForGoalAsync(string goalId, int limit = 20,
            CancellationToken cancellationToken = default) => inner.ListForGoalAsync(goalId, limit, cancellationToken);

        public Task<IReadOnlyList<string>> ListActionableGoalIdsAsync(CancellationToken cancellationToken = default) =>
            inner.ListActionableGoalIdsAsync(cancellationToken);

        public Task<IReadOnlyDictionary<string, ActionableOperatorIntentSummary>> ListActionableSummariesAsync(
            IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken = default) =>
            inner.ListActionableSummariesAsync(goalIds, cancellationToken);

        public void AcknowledgeWake(string intentId) => inner.AcknowledgeWake(intentId);
    }
}
