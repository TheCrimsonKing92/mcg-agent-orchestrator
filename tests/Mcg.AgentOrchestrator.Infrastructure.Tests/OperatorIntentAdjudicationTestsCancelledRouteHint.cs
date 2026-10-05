using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class OperatorIntentAdjudicationTestsCancelledRouteHint(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const string RouteHint = "; task is Cancelled (cancel-dispatch): use route --cause <cause>";

    [Xunit.Theory]
    [Xunit.InlineData("close", "task-not-closable")]
    [Xunit.InlineData("reopen-regate", "goal-not-acceptance-failed")]
    public async Task Cancelled_task_rejects_closure_with_hint_then_accepts_route(string shape, string reason)
    {
        var root = CreateTempDirectory("mcg-adjudicate-cancelled");
        try
        {
            var (kernel, goal, task) = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.RunningTask();
            kernel.RecordTaskProcessCancelled(goal.Id, task.Id, task.LastProcess! with
            {
                WasCancelled = true,
                CompletedAt = task.LastProcess!.StartedAt.AddSeconds(1)
            });
            Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
            Assert.Equal(GoalStatus.Active, goal.Status);
            var harness = new Harness(root);
            var rejected = await harness.Enqueue(goal, task, shape);

            var result = harness.Coordinator.ExecutePending(kernel, goal);

            Assert.False(result.MutatedGoalState);
            Assert.True(result.RejectedAdjudication);
            Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
            await harness.AssertRejected(rejected, reason + RouteHint);

            var routed = await harness.Enqueue(goal, task, "route");
            Assert.True(harness.Coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            var state = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{routed.Id}");
            Assert.Equal(EffectReceiptStatus.Applied, state!.Effect!.Status);
            harness.Coordinator.CompletePersisted([goal.Id]);
            Assert.Equal(OperatorIntentStatus.Applied, (await harness.Store.GetAsync(routed.Id))!.Status);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("close", "task-not-closable")]
    [Xunit.InlineData("reopen-regate", "goal-not-acceptance-failed")]
    public async Task Running_to_cancelled_adds_hint_only_after_cancellation(string shape, string reason)
    {
        var root = CreateTempDirectory("mcg-adjudicate-running");
        try
        {
            var (kernel, goal, task) = BackgroundDispatchRunnerTestsOperatorCancelRequeueRace.RunningTask();
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            var harness = new Harness(root);
            var intent = await harness.Enqueue(goal, task, shape);

            Assert.True(harness.Coordinator.ExecutePending(kernel, goal).RejectedAdjudication);

            await harness.AssertRejected(intent, reason);
            Assert.Equal(WorkTaskStatus.Running, task.Status);

            kernel.RecordTaskProcessCancelled(goal.Id, task.Id, task.LastProcess! with
            {
                WasCancelled = true,
                CompletedAt = task.LastProcess!.StartedAt.AddSeconds(1)
            });
            Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
            var cancelledIntent = await harness.Enqueue(goal, task, shape);
            Assert.True(harness.Coordinator.ExecutePending(kernel, goal).RejectedAdjudication);
            await harness.AssertRejected(cancelledIntent, reason + RouteHint);
        }
        finally { TryDeleteDirectory(root); }
    }

    private sealed class Harness
    {
        private readonly string _root;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        internal Harness(string root)
        {
            _root = root;
            Store = new SqliteOperatorIntentStore(Path.Combine(root, "operator-intents.db"), Path.Combine(root, "logs"));
            Decisions = CollaborationItemStore.ForDirectory(root);
            Coordinator = new OperatorIntentCoordinator(Store, decisions: Decisions, goalStateVersionResolver: _ => 7);
        }

        internal SqliteOperatorIntentStore Store { get; }
        internal CollaborationItemStore Decisions { get; }
        internal OperatorIntentCoordinator Coordinator { get; }

        internal Task<OperatorIntentRecord> Enqueue(Goal goal, TaskSpec task, string shape)
        {
            var id = Guid.NewGuid().ToString("N");
            var payload = new AdjudicateOperatorIntentPayload(shape, "Operator explanation", ["receipt-1"], 7, _root,
                Cause: "MainDriftConflict");
            return Store.EnqueueAsync(new OperatorIntentRecord(
                id, id, OperatorIntentVerbs.Adjudicate, goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(payload, JsonOptions), [], "operator", "cli", "local-process",
                DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human));
        }

        internal async Task AssertRejected(OperatorIntentRecord intent, string reason)
        {
            var state = await Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
            Assert.Equal(EffectReceiptStatus.Rejected, state!.Effect!.Status);
            Assert.Equal(reason, state.Effect.Result);
            Assert.Equal("refused " + reason, state.Effect.Outcome);
            var stored = await Store.GetAsync(intent.Id);
            Assert.Equal(OperatorIntentStatus.Rejected, stored!.Status);
            Assert.Contains(reason, stored.Outcome, StringComparison.Ordinal);
        }
    }
}
