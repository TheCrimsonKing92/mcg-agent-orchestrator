using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each scenario owns its SQLite database and wake directory.
public sealed class OperatorIntentCoordinatorBatchApplyTests
{
    [Fact]
    public async Task SevenMappings_OneCall_AppliesInOrderAndCompletesAfterCommit()
    {
        using var scenario = new Scenario(7);
        var intents = await scenario.EnqueueMappings(7);

        var result = scenario.Execute();

        Assert.True(result.MutatedGoalState);
        AssertAppliedLines(result, intents);
        AssertMappingsAndMarkers(scenario.Goal, intents);
        await scenario.AssertStatuses(intents, OperatorIntentStatus.Claimed);
        scenario.Complete();
        await scenario.AssertStatuses(intents, OperatorIntentStatus.Applied);
        Assert.Equal(1, scenario.ExecuteCalls);
        Assert.Equal(1, scenario.CompleteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableBatch_CrashBeforeCompletion_RecoversOnlyUnfinishedRows(bool partial)
    {
        using var scenario = new Scenario(3);
        var intents = await scenario.EnqueueMappings(3);
        AssertAppliedLines(scenario.Execute(), intents);
        await scenario.AssertStatuses(intents, OperatorIntentStatus.Claimed);
        var snapshot = scenario.Kernel.ExportSnapshot();
        if (partial)
            await scenario.Store.CompleteAsync(intents[0].Id, OperatorIntentCoordinator.ClaimOwner,
                OperatorIntentStatus.Applied, "first completion survived", Scenario.CreatedAt);

        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot);
        var goal = restored.GetGoal(scenario.Goal.Id);
        var result = new OperatorIntentCoordinator(scenario.Store).ExecutePending(restored, goal);

        Assert.False(result.MutatedGoalState);
        var recoverable = intents.Skip(partial ? 1 : 0).ToArray();
        Assert.Equal(recoverable.Select(intent => intent.Id), result.ProgressLines.Select(IntentId));
        Assert.All(result.ProgressLines, line => Assert.Contains("result=applied-recovered", line));
        foreach (var intent in recoverable)
        {
            var row = await scenario.Store.GetAsync(intent.Id);
            Assert.Equal(OperatorIntentStatus.Applied, row!.Status);
            Assert.Contains("recovered durable operator-intent payload", row.Outcome);
        }
        if (partial)
        {
            var first = await scenario.Store.GetAsync(intents[0].Id);
            Assert.Equal("first completion survived", first!.Outcome);
            Assert.Equal(Scenario.CreatedAt, first.CompletedAt);
        }
        AssertMappingsAndMarkers(goal, intents);
    }

    [Fact]
    public async Task RejectedReceipt_MidBatch_DoesNotBlockLaterMapping()
    {
        using var scenario = new Scenario(2);
        var first = (await scenario.EnqueueMappings(1))[0];
        var rejected = await scenario.Enqueue(OperatorIntentVerbs.CriterionEvidenceRecord,
            new CriterionEvidenceReceiptOperatorIntentPayload("acceptance-obligation",
                CriterionEvidenceOwner.Acceptance, "candidate", "receipt", "scope", true, "not authorized"));
        var last = (await scenario.EnqueueMappings(1, 1))[0];

        var result = scenario.Execute();

        Assert.True(result.MutatedGoalState);
        Assert.Equal(new[] { first.Id, rejected.Id, last.Id }, result.ProgressLines.Select(IntentId));
        Assert.Contains("result=applied-pending-commit", result.ProgressLines[0]);
        Assert.Contains("result=rejected", result.ProgressLines[1]);
        Assert.Contains("result=applied-pending-commit", result.ProgressLines[2]);
        var rejectedRow = await scenario.Store.GetAsync(rejected.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, rejectedRow!.Status);
        Assert.StartsWith("Rejected criterion-evidence-record:", rejectedRow.Outcome);
        AssertMappingsAndMarkers(scenario.Goal, [first, last]);
        await scenario.AssertStatuses([first, last], OperatorIntentStatus.Claimed);
        scenario.Complete();
        await scenario.AssertStatuses([first, last], OperatorIntentStatus.Applied);
    }

    [Fact]
    public async Task ManualVerification_First_SeparatesFollowingMappingsIntoNextTick()
    {
        using var scenario = new Scenario(2);
        var task = scenario.Goal.Tasks.Single();
        scenario.Kernel.ReportTaskProgress(scenario.Goal.Id, task.Id, WorkTaskStatus.Completed, "completed");
        var manual = await scenario.Enqueue(OperatorIntentVerbs.VerifyManual,
            new ManualVerificationOperatorIntentPayload(new TaskVerificationRecord(
                "manual-verification passed", scenario.Root, 0, "Operator inspected the result.", "",
                Scenario.CreatedAt, ModelFitNote: "OpenAI/gpt-test - adequate")), task.Id.Value);
        var mappings = await scenario.EnqueueMappings(2);

        AssertAppliedLines(scenario.Execute(), [manual]);
        Assert.Empty(scenario.Goal.CriterionEvidenceObligations);
        await scenario.AssertStatuses(mappings, OperatorIntentStatus.Pending);
        scenario.Complete();
        AssertAppliedLines(scenario.Execute(), mappings);
        AssertMappingsAndMarkers(scenario.Goal, mappings, markerCount: 3);
        scenario.Complete();
        await scenario.AssertStatuses([manual, .. mappings], OperatorIntentStatus.Applied);
    }

    [Fact]
    public async Task Retry_AfterMappings_IsClaimedWithoutApplyingUntilNextTick()
    {
        using var scenario = new Scenario(2);
        var task = scenario.Goal.Tasks.Single();
        scenario.Kernel.ReportTaskProgress(scenario.Goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
        var mappings = await scenario.EnqueueMappings(2);
        var retry = await scenario.Enqueue(OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("retry with clarified contract", null,
                RetryCause: RetryCause.ContractClarification), task.Id.Value);

        AssertAppliedLines(scenario.Execute(), mappings);
        var held = await scenario.Store.GetAsync(retry.Id);
        Assert.Equal(OperatorIntentStatus.Claimed, held!.Status);
        Assert.Null(held.CompletedAt);
        Assert.Null(held.Outcome);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.TaskRetried);
        scenario.Complete();
        AssertAppliedLines(scenario.Execute(), [retry]);
        Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        Assert.NotEqual(WorkTaskStatus.Failed, task.Status);
        scenario.Complete();
        await scenario.AssertStatuses([retry], OperatorIntentStatus.Applied);
    }

    [Fact]
    public async Task MoreThanCap_UncommittedBatch_BlocksThenDrainsNextTick()
    {
        const int cap = OperatorIntentCoordinator.MaxIntentsPerGoalPerTick;
        using var scenario = new Scenario(cap + 3);
        var intents = await scenario.EnqueueMappings(cap + 3);
        var first = intents.Take(cap).ToArray();
        var rest = intents.Skip(cap).ToArray();

        AssertAppliedLines(scenario.Execute(), first);
        await scenario.AssertStatuses(first, OperatorIntentStatus.Claimed);
        await scenario.AssertStatuses(rest, OperatorIntentStatus.Pending);
        var waiting = scenario.Execute();
        Assert.Equal($"OPERATOR_INTENT goal={scenario.Goal.Id.Value[..8]} result=waiting-for-state-commit count={cap}",
            Assert.Single(waiting.ProgressLines));
        Assert.Equal(cap, scenario.Goal.CriterionEvidenceObligations.Count);
        Assert.Equal(cap, scenario.Goal.Timeline.Count(item => item.OperatorIntentApplied is not null));
        scenario.Complete();
        AssertAppliedLines(scenario.Execute(), rest);
        AssertMappingsAndMarkers(scenario.Goal, intents);
        scenario.Complete();
        await scenario.AssertStatuses(intents, OperatorIntentStatus.Applied);
    }

    [Fact]
    public async Task RealStore_BatchBeforeCommit_LeavesEveryApplicationClaimed()
    {
        using var scenario = new Scenario(3);
        var intents = await scenario.EnqueueMappings(3);

        var result = scenario.Execute();

        AssertAppliedLines(result, intents);
        Assert.DoesNotContain(result.ProgressLines, line => line.Contains("applied-recovered"));
        AssertMappingsAndMarkers(scenario.Goal, intents);
        await scenario.AssertStatuses(intents, OperatorIntentStatus.Claimed);
        foreach (var intent in intents)
            Assert.Null((await scenario.Store.GetAsync(intent.Id))!.CompletedAt);
        scenario.Complete();
        await scenario.AssertStatuses(intents, OperatorIntentStatus.Applied);
    }

    [Fact]
    public async Task PendingClaim_OwnerClaimedRows_SkipsThemAndOrdersPendingRows()
    {
        using var scenario = new Scenario(4);
        var claimed = await scenario.Enqueue(OperatorIntentVerbs.CriterionEvidenceMap, new { }, id: "held");
        Assert.Equal(claimed.Id, (await scenario.Store.ClaimNextAsync(scenario.Goal.Id.Value, "owner"))!.Id);
        var late = await scenario.Enqueue(OperatorIntentVerbs.CriterionEvidenceMap, new { }, id: "late",
            createdAt: Scenario.CreatedAt.AddSeconds(2));
        var tieB = await scenario.Enqueue(OperatorIntentVerbs.CriterionEvidenceMap, new { }, id: "b",
            createdAt: Scenario.CreatedAt.AddSeconds(1));
        var tieA = await scenario.Enqueue(OperatorIntentVerbs.CriterionEvidenceMap, new { }, id: "a",
            createdAt: Scenario.CreatedAt.AddSeconds(1));

        foreach (var expected in new[] { tieA, tieB, late })
            Assert.Equal(expected.Id, (await scenario.Store.ClaimNextPendingAsync(scenario.Goal.Id.Value, "owner"))!.Id);
        Assert.Null(await scenario.Store.ClaimNextPendingAsync(scenario.Goal.Id.Value, "owner"));
        Assert.Equal(claimed.Id, (await scenario.Store.ClaimNextAsync(scenario.Goal.Id.Value, "owner"))!.Id);
        await scenario.AssertStatuses([claimed, tieA, tieB, late], OperatorIntentStatus.Claimed);
    }

    [Theory]
    [InlineData(OperatorIntentVerbs.CriterionEvidenceMap, true)]
    [InlineData(OperatorIntentVerbs.CriterionEvidenceRecord, true)]
    [InlineData(OperatorIntentVerbs.CriterionEvidenceRepair, true)]
    [InlineData(OperatorIntentVerbs.Answer, false)]
    [InlineData(OperatorIntentVerbs.Retry, false)]
    [InlineData(OperatorIntentVerbs.Adjudicate, false)]
    [InlineData(OperatorIntentVerbs.CancelDispatch, false)]
    [InlineData(OperatorIntentVerbs.VerifyManual, false)]
    [InlineData(OperatorIntentVerbs.Progress, false)]
    [InlineData(OperatorIntentVerbs.ApprovePolicyChange, false)]
    [InlineData(OperatorIntentVerbs.LessonRecord, false)]
    [InlineData(OperatorIntentVerbs.LessonRetire, false)]
    [InlineData(OperatorIntentVerbs.EscapeRecord, false)]
    [InlineData(OperatorIntentVerbs.ExperimentApplyFlag, false)]
    [InlineData(OperatorIntentVerbs.ExperimentRevertFlag, false)]
    [InlineData("unknown", false)]
    public void CanShareTick_Verb_ReturnsOnlyApprovedEvidenceVerbs(string verb, bool expected) =>
        Assert.Equal(expected, OperatorIntentBatchPolicy.CanShareTick(verb));

    private static string IntentId(string line) => line.Split(' ')[1]["id=".Length..];

    private static void AssertAppliedLines(OperatorIntentExecutionResult result, IReadOnlyList<OperatorIntentRecord> intents)
    {
        Assert.Equal(intents.Select(intent => intent.Id), result.ProgressLines.Select(IntentId));
        Assert.All(result.ProgressLines, line => Assert.Contains("result=applied-pending-commit", line));
    }

    private static void AssertMappingsAndMarkers(Goal goal, IReadOnlyList<OperatorIntentRecord> intents, int? markerCount = null)
    {
        Assert.Equal(intents.Count, goal.CriterionEvidenceObligations.Count);
        Assert.Equal(Enumerable.Range(0, intents.Count),
            goal.CriterionEvidenceObligations.Select(item => item.CriterionIndex).Order());
        Assert.Equal(markerCount ?? intents.Count, goal.Timeline.Count(item => item.OperatorIntentApplied is not null));
        foreach (var intent in intents)
            Assert.Single(goal.Timeline.Where(item => item.OperatorIntentApplied?.IntentId == intent.Id));
    }

    private sealed class Scenario : IDisposable
    {
        public static readonly DateTimeOffset CreatedAt = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "operator-intent-batch-" + Guid.NewGuid().ToString("N"));
        public AgentOrchestratorKernel Kernel { get; } = new();
        public Goal Goal { get; }
        public SqliteOperatorIntentStore Store { get; }
        private readonly OperatorIntentCoordinator _coordinator;
        private int _sequence;
        public int ExecuteCalls { get; private set; }
        public int CompleteCalls { get; private set; }

        public Scenario(int criteria)
        {
            Directory.CreateDirectory(Root);
            Goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, AgentCatalog.Default().Agents, "Batch evidence");
            Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Batch evidence",
                Enumerable.Range(0, criteria).Select(index => $"Criterion {index}").ToArray(),
                VerificationClass.RealWorldDependent, [], []));
            Store = new SqliteOperatorIntentStore(Path.Combine(Root, "intents.db"), Path.Combine(Root, "logs"));
            _coordinator = new OperatorIntentCoordinator(Store);
        }

        public async Task<OperatorIntentRecord[]> EnqueueMappings(int count, int start = 0)
        {
            var intents = new List<OperatorIntentRecord>();
            for (var index = start; index < start + count; index++)
                intents.Add(await Enqueue(OperatorIntentVerbs.CriterionEvidenceMap,
                    new CriterionEvidenceMappingOperatorIntentPayload(index, 1, CriterionEvidenceOwner.Operator,
                        "operator:replay", $"finding-{index}", "candidate")));
            return intents.ToArray();
        }

        public Task<OperatorIntentRecord> Enqueue<T>(string verb, T payload, string? taskId = null,
            string? id = null, DateTimeOffset? createdAt = null)
        {
            var sequence = _sequence++;
            id ??= $"intent-{sequence:D3}";
            return Store.EnqueueAsync(new OperatorIntentRecord(id, "key-" + id, verb, Goal.Id.Value, taskId,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), [],
                "operator", "cli", "local-process", createdAt ?? CreatedAt.AddTicks(sequence)));
        }

        public OperatorIntentExecutionResult Execute()
        {
            ExecuteCalls++;
            return _coordinator.ExecutePending(Kernel, Goal);
        }

        public void Complete()
        {
            CompleteCalls++;
            _coordinator.CompletePersisted([Goal.Id]);
        }

        public async Task AssertStatuses(IEnumerable<OperatorIntentRecord> intents, OperatorIntentStatus expected)
        {
            foreach (var intent in intents)
                Assert.Equal(expected, (await Store.GetAsync(intent.Id))!.Status);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
