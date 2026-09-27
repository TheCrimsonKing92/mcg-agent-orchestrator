using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class OperatorIntentAdjudicationTests : ConductorBatchLoopTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OperatorIntentAdjudicationTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(WorkTaskStatus.Failed)]
    [Xunit.InlineData(WorkTaskStatus.Completed)]
    public async Task Close_applies_in_one_coordinator_call_and_records_typed_decision(WorkTaskStatus initialStatus)
    {
        await WithHarness(async harness =>
        {
            var (kernel, goal) = SimpleGoal("Close a task atomically");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, initialStatus, "before adjudication");
            var intent = await harness.Enqueue(goal, task, Payload("close"), OperatorActorKind.Agent);

            var result = harness.Coordinator.ExecutePending(kernel, goal);
            harness.Coordinator.CompletePersisted([goal.Id]);

            Assert.True(result.MutatedGoalState);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.True(task.LastVerification?.Succeeded);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            await AssertDecision(harness.Decisions, goal, intent, "applied", OperatorActorKind.Agent);
        });
    }

    [Xunit.Fact]
    public async Task Reopen_regate_returns_acceptance_failed_goal_to_verified_in_one_coordinator_call()
    {
        await WithHarness(async harness =>
        {
            var (kernel, goal) = SimpleGoal("Re-gate without a worker round");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");
            kernel.RecordTaskVerification(goal.Id, task.Id, PassingVerification(harness.Root));
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "gate started"));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["gate"], "gate failed"));
            var intent = await harness.Enqueue(goal, task, Payload("reopen-regate"));

            var result = harness.Coordinator.ExecutePending(kernel, goal);
            harness.Coordinator.CompletePersisted([goal.Id]);

            Assert.True(result.MutatedGoalState);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.NotNull(task.LatestRetryAt);
            Assert.Equal("Operator explanation", task.AcceptedRetryFeedback?.Message);
            Assert.Null(task.LastDispatch);
            await AssertDecision(harness.Decisions, goal, intent, "applied", OperatorActorKind.Human);
        });
    }

    [Xunit.Fact]
    public async Task Route_retries_named_task_with_cause_and_feedback()
    {
        await WithHarness(async harness =>
        {
            var (kernel, goal) = SimpleGoal("Route an upstream correction");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var intent = await harness.Enqueue(
                goal,
                task,
                Payload("route") with { Cause = nameof(RetryCause.ContractClarification), Text = "Apply the clarified contract." });

            harness.Coordinator.ExecutePending(kernel, goal);
            harness.Coordinator.CompletePersisted([goal.Id]);

            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Equal(RetryCause.ContractClarification, task.PendingRetryCause);
            Assert.Equal("Apply the clarified contract.", task.AcceptedRetryFeedback?.Message);
            await AssertDecision(harness.Decisions, goal, intent, "applied", OperatorActorKind.Human);
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData(OperatorActorKind.Agent, false)]
    [Xunit.InlineData(OperatorActorKind.Human, true)]
    public async Task AdjudicationCorrectionUsesIntentActorKind(OperatorActorKind actorKind, bool applies)
    {
        await WithHarness(async harness =>
        {
            var (kernel, goal) = SimpleGoal("Protect criteria during adjudication");
            var task = goal.Tasks.Single();
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
                "Protect criteria", ["ship it"], VerificationClass.TestVerifiable, [], []));
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var correction = "CRITERIA CORRECTION: supersedes=\"ship it\"; correction=\"skip it\"";
            await harness.Enqueue(goal, task,
                Payload("route") with { Cause = nameof(RetryCause.ContractClarification), Text = correction },
                actorKind);

            harness.Coordinator.ExecutePending(kernel, goal);

            if (applies)
                Assert.Equal("operator", Assert.Single(goal.EffectiveAcceptanceCriteriaCorrections).Actor);
            else
            {
                Assert.Empty(goal.EffectiveAcceptanceCriteriaCorrections);
                Assert.Contains(goal.Timeline, item => item.Message.Contains(
                    "CRITERIA_CORRECTION_IGNORED source=agent-intent", StringComparison.Ordinal));
            }
        });
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing-text")]
    [Xunit.InlineData("missing-evidence")]
    [Xunit.InlineData("unknown-cause")]
    [Xunit.InlineData("stale-version")]
    public async Task Invalid_adjudication_is_rejected_without_kernel_change(string invalidCase)
    {
        await WithHarness(async harness =>
        {
            var (kernel, goal) = SimpleGoal("Reject an invalid adjudication");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var payload = Payload("route") with { Cause = nameof(RetryCause.ContractClarification) };
            payload = invalidCase switch
            {
                "missing-text" => payload with { Text = "" },
                "missing-evidence" => payload with { EvidenceReferences = [] },
                "unknown-cause" => payload with { Cause = "not-a-cause" },
                "stale-version" => payload with { Precondition = AdjudicationPrecondition.Capture(goal, task) with { TaskStatus = WorkTaskStatus.Completed.ToString() } },
                _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
            };
            var before = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id) with { Timeline = [] }, JsonOptions);
            var timelineCount = goal.Timeline.Count;
            var intent = await harness.Enqueue(goal, task, payload);

            var result = harness.Coordinator.ExecutePending(kernel, goal);

            var after = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id) with { Timeline = [] }, JsonOptions);
            Assert.False(result.MutatedGoalState);
            Assert.Equal(before, after);
            Assert.Equal(timelineCount + 1, goal.Timeline.Count);
            var state = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
            Assert.Equal(EffectReceiptStatus.Rejected, state!.Effect!.Status);
            Assert.Contains(state.Effect.Result, new[]
            {
                "missing-explanation", "missing-evidence", "unknown-retry-cause", "stale-goal-state-version"
            });
            Assert.Single(goal.Timeline.Where(item => item.OperatorIntentApplied?.DecisionId == state.Receipt!.Id));
        });
    }

    [Xunit.Fact]
    public void Cli_contract_registers_help_and_defaults_actor_kind_to_human()
    {
        Assert.Contains("adjudicate", CliArgumentParser.RecognizedCommands);
        Assert.Null(Record.Exception(() => CliCommandHelp.ThrowIfInvalidFlags(["adjudicate", "--help"])));
        Assert.Contains("--evidence", CliCommandHelp.AdjudicateUsage, StringComparison.Ordinal);
        Assert.Equal(
            OperatorActorKind.Human,
            CliPersistentStateRunner.ResolveOperatorIntentAttribution(
                [], CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli).ActorKind);
        Assert.Equal(
            OperatorActorKind.Agent,
            CliPersistentStateRunner.ResolveOperatorIntentAttribution(
                ["--actor-kind", "agent"], CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli).ActorKind);
    }

    private static AdjudicateOperatorIntentPayload Payload(string shape) =>
        new(shape, "Operator explanation", ["receipt-1"], Harness.Version, "C:\\tmp");

    private static TaskVerificationRecord PassingVerification(string root) =>
        new("focused test", root, 0, "passed", "", DateTimeOffset.UtcNow);

    private static async Task AssertDecision(
        CollaborationItemStore decisions,
        Goal goal,
        OperatorIntentRecord intent,
        string outcome,
        OperatorActorKind actorKind)
    {
        var state = await decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.NotNull(state?.Receipt);
        Assert.Equal(EffectReceiptStatus.Applied, state!.Effect!.Status);
        Assert.Single(state.Receipt!.EvidenceHashes);
        Assert.True(OperatorActorIdentity.TryParse(state.Receipt.ActorId, out var actor, out var parsedKind));
        Assert.Equal(intent.Actor, actor);
        Assert.Equal(actorKind, parsedKind);
        Assert.Equal(intent.Channel, state.Receipt.Channel);
        Assert.Single(goal.Timeline.Where(item =>
            item.OperatorIntentApplied?.DecisionId == state.Receipt.Id &&
            item.OperatorIntentApplied.Outcome == outcome));
    }

    private static async Task WithHarness(Func<Harness, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-adjudicate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { await action(new Harness(root)); }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private sealed class Harness
    {
        internal const long Version = 7;
        internal Harness(string root)
        {
            Root = root;
            Store = new SqliteOperatorIntentStore(Path.Combine(root, "operator-intents.db"), Path.Combine(root, "logs"));
            Decisions = CollaborationItemStore.ForDirectory(root);
            Coordinator = new OperatorIntentCoordinator(
                Store,
                decisions: Decisions,
                goalStateVersionResolver: _ => Version);
        }

        internal string Root { get; }
        internal SqliteOperatorIntentStore Store { get; }
        internal CollaborationItemStore Decisions { get; }
        internal OperatorIntentCoordinator Coordinator { get; }

        internal async Task<OperatorIntentRecord> Enqueue(
            Goal goal,
            TaskSpec task,
            AdjudicateOperatorIntentPayload payload,
            OperatorActorKind actorKind = OperatorActorKind.Human)
        {
            var id = Guid.NewGuid().ToString("N");
            return await Store.EnqueueAsync(new OperatorIntentRecord(
                id,
                id,
                OperatorIntentVerbs.Adjudicate,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(payload, JsonOptions),
                [],
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow,
                ActorKind: actorKind));
        }
    }
}
