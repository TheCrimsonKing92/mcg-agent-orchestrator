using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class OperatorIntentAdjudicationTestsVerifyingReopen(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Xunit.Fact]
    public async Task Reopen_regate_returns_stranded_verifying_goal_to_verified_in_one_coordinator_call()
    {
        await WithHarness(async root =>
        {
            var (kernel, goal) = CreateVerifyingGoal(root);
            var task = goal.Tasks.Single();
            var queryCalls = 0;
            var harness = new Harness(root, id =>
            {
                Assert.Equal(goal.Id, id);
                queryCalls++;
                return false;
            });
            var priorVerification = task.LastVerification;
            var intent = await harness.Enqueue(goal, task);

            var result = harness.Coordinator.ExecutePending(kernel, goal);

            Assert.True(result.MutatedGoalState);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.True(task.LastVerification?.Succeeded);
            Assert.NotSame(priorVerification, task.LastVerification);
            Assert.Equal("Operator explanation", task.AcceptedRetryFeedback?.Message);
            Assert.NotNull(task.LatestRetryAt);
            Assert.Null(task.LastDispatch);
            Assert.Equal(1, queryCalls);
            var state = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
            Assert.Equal(EffectReceiptStatus.Applied, state!.Effect!.Status);
            harness.Coordinator.CompletePersisted([goal.Id]);
        });
    }

    [Xunit.Fact]
    public async Task Reopen_regate_rejects_verifying_goal_with_live_attempt_without_kernel_change()
    {
        await WithHarness(async root =>
        {
            var (kernel, goal) = CreateVerifyingGoal(root);
            var harness = new Harness(root, _ => true);
            var intent = await harness.Enqueue(goal, goal.Tasks.Single());

            await AssertRejectedWithoutKernelChange(kernel, goal, harness, intent);
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Reopen_regate_fails_closed_when_live_attempt_query_is_missing_or_throws(bool throws)
    {
        await WithHarness(async root =>
        {
            var (kernel, goal) = CreateVerifyingGoal(root);
            Func<GoalId, bool>? query = throws ? _ => throw new IOException("attempt unreadable") : null;
            var harness = new Harness(root, query);
            var intent = await harness.Enqueue(goal, goal.Tasks.Single());

            await AssertRejectedWithoutKernelChange(kernel, goal, harness, intent);
        });
    }

    [Xunit.Fact]
    public async Task Acceptance_failed_reopen_does_not_consult_live_attempt_query()
    {
        await WithHarness(async root =>
        {
            var (kernel, goal) = CreateVerifyingGoal(root);
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["gate"], "gate failed"));
            var queryCalls = 0;
            var harness = new Harness(root, _ => { queryCalls++; return true; });
            var intent = await harness.Enqueue(goal, goal.Tasks.Single());

            harness.Coordinator.ExecutePending(kernel, goal);

            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(0, queryCalls);
            var state = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
            Assert.Equal(EffectReceiptStatus.Applied, state!.Effect!.Status);
            harness.Coordinator.CompletePersisted([goal.Id]);
        });
    }

    private static async Task AssertRejectedWithoutKernelChange(
        AgentOrchestratorKernel kernel, Goal goal, Harness harness, OperatorIntentRecord intent)
    {
        var before = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id) with { Timeline = [] }, JsonOptions);
        var timelineCount = goal.Timeline.Count;

        var result = harness.Coordinator.ExecutePending(kernel, goal);

        var after = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id) with { Timeline = [] }, JsonOptions);
        Assert.False(result.MutatedGoalState);
        Assert.True(result.RejectedAdjudication);
        Assert.Equal(before, after);
        Assert.Equal(timelineCount + 1, goal.Timeline.Count);
        var state = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal(EffectReceiptStatus.Rejected, state!.Effect!.Status);
        Assert.Equal("acceptance-attempt-live", state.Effect.Result);
        Assert.Single(goal.Timeline.Where(item => item.OperatorIntentApplied?.DecisionId == state.Receipt!.Id));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateVerifyingGoal(string root)
    {
        var (kernel, goal) = SimpleGoal("Re-gate stranded acceptance atomically");
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("focused test", root, 0, "passed", "", DateTimeOffset.UtcNow));
        Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "gate started"));
        Assert.Equal(GoalStatus.Verifying, goal.Status);
        Assert.All(goal.Tasks, candidate => Assert.Equal(WorkTaskStatus.Completed, candidate.Status));
        return (kernel, goal);
    }

    private static async Task WithHarness(Func<string, Task> action)
    {
        var root = CreateTempDirectory("mcg-adjudicate-verifying");
        try { await action(root); }
        finally { TryDeleteDirectory(root); }
    }

    private sealed class Harness
    {
        private readonly string _root;
        private readonly SqliteOperatorIntentStore _store;

        internal Harness(string root, Func<GoalId, bool>? query)
        {
            _root = root;
            _store = new SqliteOperatorIntentStore(Path.Combine(root, "operator-intents.db"), Path.Combine(root, "logs"));
            Decisions = CollaborationItemStore.ForDirectory(root);
            Coordinator = new OperatorIntentCoordinator(_store, decisions: Decisions,
                goalStateVersionResolver: _ => 7, hasLiveAcceptanceAttempt: query);
        }

        internal CollaborationItemStore Decisions { get; }
        internal OperatorIntentCoordinator Coordinator { get; }

        internal Task<OperatorIntentRecord> Enqueue(Goal goal, TaskSpec task)
        {
            var id = Guid.NewGuid().ToString("N");
            var payload = new AdjudicateOperatorIntentPayload(
                "reopen-regate", "Operator explanation", ["receipt-1"], 7, _root);
            return _store.EnqueueAsync(new OperatorIntentRecord(
                id, id, OperatorIntentVerbs.Adjudicate, goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(payload, JsonOptions), [], "operator", "cli", "local-process",
                DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human));
        }
    }
}
