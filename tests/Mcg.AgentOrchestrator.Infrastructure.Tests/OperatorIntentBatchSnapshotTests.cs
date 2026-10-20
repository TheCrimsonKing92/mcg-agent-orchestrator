using System.Collections.Frozen;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its SQLite database and wake artifacts.
public sealed class OperatorIntentBatchSnapshotTests
{
    [Xunit.Fact]
    public async Task CaptureAndClaim_ExcludeLaterBackdatedRowsAndForeignClaims()
    {
        using var fixture = new Fixture();
        var first = await fixture.Store.EnqueueAsync(fixture.Intent("first"));
        var foreign = await fixture.Store.EnqueueAsync(fixture.Intent("foreign", goalId: "other-goal"));
        await fixture.Store.ClaimNextAsync(foreign.GoalId, "foreign-owner");
        await fixture.Store.ClaimNextAsync(first.GoalId, OperatorIntentCoordinator.ClaimOwner);
        var batch = await fixture.Store.CaptureGoalBatchAsync(OperatorIntentCoordinator.ClaimOwner);
        Assert.Equal(first.Id, Assert.Single(batch).Id);
        var late = await fixture.Store.EnqueueAsync(fixture.Intent("backdated") with { CreatedAt = first.CreatedAt.AddDays(-1) });
        var ids = batch.Select(intent => intent.Id).ToFrozenSet(StringComparer.Ordinal);

        var claimed = await fixture.Store.ClaimNextInBatchAsync(first.GoalId, OperatorIntentCoordinator.ClaimOwner, ids);
        Assert.Equal(first.Id, claimed!.Id);
        await fixture.Store.CompleteAsync(first.Id, OperatorIntentCoordinator.ClaimOwner,
            OperatorIntentStatus.Applied, "durable", fixture.Now);
        Assert.Null(await fixture.Store.ClaimNextInBatchAsync(first.GoalId, OperatorIntentCoordinator.ClaimOwner, ids));
        Assert.Equal(OperatorIntentStatus.Pending, (await fixture.Store.GetAsync(late.Id))!.Status);
        Assert.Equal("foreign-owner", (await fixture.Store.GetAsync(foreign.Id))!.ClaimOwner);
    }

    [Xunit.Fact]
    public async Task RejectionAndCompanionAnswer_RespectCapturedMembership()
    {
        using var fixture = new Fixture();
        var retry = await fixture.Store.EnqueueAsync(fixture.Intent("retry", OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("needs clarification", null)));
        var coordinator = new OperatorIntentCoordinator(fixture.Store, utcNow: () => fixture.Now);
        var ids = coordinator.CaptureGoalBatch().Select(intent => intent.Id).ToFrozenSet(StringComparer.Ordinal);
        var answer = await fixture.Store.EnqueueAsync(fixture.Intent("answer", OperatorIntentVerbs.Answer, new { answer = "later" }));
        var result = coordinator.ExecutePending(fixture.Kernel, fixture.Goal, ids);
        Assert.Contains(result.ProgressLines, line => line.Contains("awaiting-retry-cause", StringComparison.Ordinal));
        Assert.Equal(OperatorIntentStatus.Pending, (await fixture.Store.GetAsync(answer.Id))!.Status);
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.OperatorIntentApplied?.IntentId == answer.Id);

        coordinator.RejectPending(fixture.Goal.Id.Value, "test rejection", intentIds: ids);
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(retry.Id))!.Status);
        Assert.Equal(OperatorIntentStatus.Pending, (await fixture.Store.GetAsync(answer.Id))!.Status);
    }

    [Xunit.Fact]
    public async Task CapturedReplay_AfterDurableReloadDoesNotRepeatRetry()
    {
        using var fixture = new Fixture();
        var intent = await fixture.Store.EnqueueAsync(fixture.Intent("retry", OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("repair", null, RetryCause: RetryCause.ContractClarification)));
        var first = new OperatorIntentCoordinator(fixture.Store, utcNow: () => fixture.Now);
        var ids = first.CaptureGoalBatch().Select(item => item.Id).ToFrozenSet(StringComparer.Ordinal);
        Assert.True(first.ExecutePending(fixture.Kernel, fixture.Goal, ids).MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Store.GetAsync(intent.Id))!.Status);
        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot());
        var goal = restored.GetGoal(fixture.Goal.Id);
        var second = new OperatorIntentCoordinator(fixture.Store, utcNow: () => fixture.Now.AddMinutes(6));

        var replay = second.ExecutePending(restored, goal,
            second.CaptureGoalBatch().Select(item => item.Id).ToFrozenSet(StringComparer.Ordinal));

        Assert.False(replay.MutatedGoalState);
        Assert.Contains(replay.ProgressLines, line => line.Contains("applied-recovered", StringComparison.Ordinal));
        Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
        Assert.Single(goal.Timeline.Where(item => item.OperatorIntentApplied?.IntentId == intent.Id));
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData("expired")]
    [Xunit.InlineData("malformed")]
    [Xunit.InlineData("missing-task")]
    public async Task CapturedInvalidCommand_PreservesRejectionWithoutEffect(string kind)
    {
        using var fixture = new Fixture();
        var intent = fixture.Intent("invalid", OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("repair", null, RetryCause: RetryCause.ContractClarification));
        intent = kind switch
        {
            "expired" => intent,
            "malformed" => intent with { PayloadJson = "{" },
            _ => intent with { TaskId = TaskId.New().Value }
        };
        await fixture.Store.EnqueueAsync(intent);
        var coordinator = new OperatorIntentCoordinator(fixture.Store,
            utcNow: () => kind == "expired" ? fixture.Now.AddMinutes(6) : fixture.Now);
        var result = coordinator.ExecutePending(fixture.Kernel, fixture.Goal,
            coordinator.CaptureGoalBatch().Select(item => item.Id).ToFrozenSet(StringComparer.Ordinal));
        Assert.False(result.MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Goal.Tasks.Single().Status);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Xunit.Fact]
    public async Task CapturedRetry_LiveProcessRetainsExistingAdmissionRejection()
    {
        using var fixture = new Fixture();
        var (kernel, goal, task) = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.RunningTask();
        var process = task.LastProcess;
        var intent = fixture.Intent("busy", OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("repair", null, RetryCause: RetryCause.ContractClarification))
            with { GoalId = goal.Id.Value, TaskId = task.Id.Value };
        await fixture.Store.EnqueueAsync(intent);
        var coordinator = new OperatorIntentCoordinator(fixture.Store, utcNow: () => fixture.Now);

        var result = coordinator.ExecutePending(kernel, goal,
            coordinator.CaptureGoalBatch().Select(item => item.Id).ToFrozenSet(StringComparer.Ordinal));

        Assert.False(result.MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Same(process, task.LastProcess);
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        Assert.DoesNotContain(goal.Timeline, item => item.OperatorIntentApplied?.IntentId == intent.Id);
    }

    [Xunit.Fact]
    public async Task CapturedMappings_RetainExistingPerGoalLimit()
    {
        using var fixture = new Fixture();
        fixture.Kernel.SetGoalRefinedSpec(fixture.Goal.Id, new RefinedSpec("mappings", ["criterion"],
            VerificationClass.TestVerifiable, [], []));
        for (var index = 0; index < 33; index++)
            await fixture.Store.EnqueueAsync(fixture.Intent($"map-{index:00}", OperatorIntentVerbs.CriterionEvidenceMap,
                new CriterionEvidenceMappingOperatorIntentPayload(0, 1, CriterionEvidenceOwner.Operator,
                    "operator:replay", $"finding-{index}", "candidate")) with { TaskId = null });
        var coordinator = new OperatorIntentCoordinator(fixture.Store, utcNow: () => fixture.Now);
        var result = coordinator.ExecutePending(fixture.Kernel, fixture.Goal,
            coordinator.CaptureGoalBatch().Select(item => item.Id).ToFrozenSet(StringComparer.Ordinal));
        Assert.True(result.MutatedGoalState);
        Assert.Equal(32, fixture.Goal.Timeline.Count(item => item.OperatorIntentApplied is not null));
        Assert.Equal(OperatorIntentStatus.Pending, (await fixture.Store.GetAsync("map-32"))!.Status);
        coordinator.CompletePersisted([fixture.Goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync("map-31"))!.Status);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-intent-snapshot-{Guid.NewGuid():N}");
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }
        internal SqliteOperatorIntentStore Store { get; }
        internal DateTimeOffset Now { get; } = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        internal Fixture()
        {
            Goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, AgentCatalog.Default().Agents, "Snapshot target");
            Kernel.ReportTaskProgress(Goal.Id, Goal.Tasks.Single().Id, WorkTaskStatus.Failed, "repair");
            Store = new SqliteOperatorIntentStore(Path.Combine(_root, "intents.db"), Path.Combine(_root, "logs"), utcNow: () => Now);
        }
        internal OperatorIntentRecord Intent(string id, string verb = "unknown", object? payload = null, string? goalId = null) =>
            new(id, id, verb, goalId ?? Goal.Id.Value, Goal.Tasks.Single().Id.Value,
                JsonSerializer.Serialize(payload ?? new { }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [], "operator", "test", "local-process", Now);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
