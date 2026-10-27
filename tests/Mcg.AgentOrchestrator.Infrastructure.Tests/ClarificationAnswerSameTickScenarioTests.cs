using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ClarificationAnswerSameTickScenarioTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Distinct_answers_apply_in_one_pass_and_complete_after_commit(bool captured)
    {
        using var fixture = new Fixture();
        var a = await fixture.Raise("a");
        var b = await fixture.Raise("b");
        var c = await fixture.Raise("c");
        var answers = new[]
        {
            await fixture.Answer(a, "answer-a"),
            await fixture.Answer(b, "answer-b"),
            await fixture.Answer(c, "answer-c")
        };

        var result = fixture.Execute(captured);

        AssertAppliedLines(result, answers);
        foreach (var (item, text) in new[] { (a, "answer-a"), (b, "answer-b"), (c, "answer-c") })
            await fixture.AssertResolved(item, text);
        foreach (var answer in answers)
        {
            Assert.NotNull((await fixture.Collaboration.GetDecisionStateAsync($"answer-{answer.Id}")
                .WaitAsync(Bound))?.Receipt);
            Assert.Single(fixture.Goal.Timeline.Where(entry => entry.OperatorIntentApplied?.IntentId == answer.Id));
            var row = await fixture.Row(answer);
            Assert.Equal(OperatorIntentStatus.Claimed, row.Status);
            Assert.Null(row.CompletedAt);
        }
        Assert.Equal(3, fixture.Goal.Timeline.Count(entry => entry.OperatorIntentApplied is not null));

        fixture.Complete();
        await fixture.AssertStatuses(answers, OperatorIntentStatus.Applied);
        var empty = fixture.Execute(captured);
        Assert.Empty(empty.ProgressLines);
        Assert.False(empty.MutatedGoalState);
    }

    [Fact]
    public async Task Duplicate_is_rejected_without_replacing_answer_or_stopping_siblings()
    {
        using var fixture = new Fixture();
        var a = await fixture.Raise("a");
        var b = await fixture.Raise("b");
        var c = await fixture.Raise("c");
        var first = await fixture.Answer(a, "answer-a");
        var second = await fixture.Answer(b, "answer-b");
        var duplicate = await fixture.Answer(a, "answer-a-second");
        var last = await fixture.Answer(c, "answer-c");

        var result = fixture.Execute();

        Assert.True(result.MutatedGoalState);
        Assert.Equal(4, result.ProgressLines.Count);
        AssertLine(result.ProgressLines[0], first, "applied-pending-commit");
        AssertLine(result.ProgressLines[1], second, "applied-pending-commit");
        AssertLine(result.ProgressLines[2], duplicate, "rejected");
        AssertLine(result.ProgressLines[3], last, "applied-pending-commit");
        var rejected = await fixture.Row(duplicate);
        Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
        Assert.Contains("already answered", rejected.Outcome);
        await fixture.AssertResolved(a, "answer-a");
        await fixture.AssertResolved(b, "answer-b");
        await fixture.AssertResolved(c, "answer-c");
        Assert.DoesNotContain(fixture.Goal.Timeline, entry => entry.OperatorIntentApplied?.IntentId == duplicate.Id);
        Assert.Null((await fixture.Collaboration.GetDecisionStateAsync($"answer-{duplicate.Id}")
            .WaitAsync(Bound))?.Receipt);
        fixture.Complete();
        await fixture.AssertStatuses([first, second, last], OperatorIntentStatus.Applied);
    }

    [Fact]
    public async Task Retry_separates_answer_batches_and_remains_claimed_until_its_pass()
    {
        using var fixture = new Fixture(simpleGoal: true);
        var task = Assert.Single(fixture.Goal.Tasks);
        fixture.Kernel.ReportTaskProgress(fixture.Goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
        var a = await fixture.Answer(await fixture.Raise("a"), "answer-a");
        var b = await fixture.Answer(await fixture.Raise("b"), "answer-b");
        var retry = await fixture.Enqueue(OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("retry with clarified contract", null,
                RetryCause: RetryCause.ContractClarification), task.Id.Value);
        var c = await fixture.Answer(await fixture.Raise("c"), "answer-c");

        AssertAppliedLines(fixture.Execute(), [a, b]);
        var held = await fixture.Row(retry);
        Assert.Equal(OperatorIntentStatus.Claimed, held.Status);
        Assert.Null(held.Outcome);
        Assert.Null(held.CompletedAt);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.DoesNotContain(fixture.Goal.Timeline, entry => entry.Kind == ProgressKind.TaskRetried);
        await fixture.AssertStatuses([c], OperatorIntentStatus.Pending);

        fixture.Complete();
        AssertAppliedLines(fixture.Execute(), [retry]);
        Assert.Single(fixture.Goal.Timeline.Where(entry => entry.Kind == ProgressKind.TaskRetried));
        Assert.NotEqual(WorkTaskStatus.Failed, task.Status);
        await fixture.AssertStatuses([c], OperatorIntentStatus.Pending);

        fixture.Complete();
        AssertAppliedLines(fixture.Execute(), [c]);
        fixture.Complete();
        await fixture.AssertStatuses([a, b, retry, c], OperatorIntentStatus.Applied);
    }

    [Fact]
    public async Task Human_input_answer_keeps_its_own_pass_between_clarifications()
    {
        using var fixture = new Fixture();
        var task = Assert.Single(fixture.Goal.Tasks);
        var request = fixture.Kernel.RequestHumanInput(fixture.Goal.Id, task.Id,
            "Which path?", HumanWaitKind.SpecClarification);
        var a = await fixture.Answer(await fixture.Raise("a"), "answer-a");
        var human = await fixture.Enqueue(OperatorIntentVerbs.Answer,
            new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput, request.Id.Value,
                fixture.Goal.Id.Value, "Use existing path", OperatorActorKind.Human));
        var b = await fixture.Answer(await fixture.Raise("b"), "answer-b");

        AssertAppliedLines(fixture.Execute(), [a]);
        var held = await fixture.Row(human);
        Assert.Equal(OperatorIntentStatus.Claimed, held.Status);
        Assert.Null(held.CompletedAt);
        Assert.Null(held.Outcome);
        Assert.False(request.IsCompleted);
        await fixture.AssertStatuses([b], OperatorIntentStatus.Pending);

        fixture.Complete();
        AssertAppliedLines(fixture.Execute(), [human]);
        Assert.True(request.IsCompleted);
        Assert.Equal("Use existing path", request.Answer);
        await fixture.AssertStatuses([b], OperatorIntentStatus.Pending);

        fixture.Complete();
        AssertAppliedLines(fixture.Execute(), [b]);
        fixture.Complete();
        await fixture.AssertStatuses([a, human, b], OperatorIntentStatus.Applied);
    }

    [Theory]
    [InlineData(null, OperatorIntentVerbs.Retry, "{}", true, false)]
    [InlineData(OperatorIntentVerbs.Answer, OperatorIntentVerbs.Answer, "Clarification", true, true)]
    [InlineData(OperatorIntentVerbs.CriterionEvidenceMap, OperatorIntentVerbs.CriterionEvidenceRecord, "{}", true, false)]
    [InlineData(OperatorIntentVerbs.Answer, OperatorIntentVerbs.Answer, "HumanInput", false, false)]
    [InlineData(OperatorIntentVerbs.Answer, OperatorIntentVerbs.CriterionEvidenceMap, "{}", false, false)]
    [InlineData(OperatorIntentVerbs.CriterionEvidenceMap, OperatorIntentVerbs.Answer, "Clarification", false, true)]
    [InlineData(OperatorIntentVerbs.Answer, OperatorIntentVerbs.Retry, "{}", false, false)]
    [InlineData(OperatorIntentVerbs.Answer, OperatorIntentVerbs.Answer, "{", false, false)]
    [InlineData(OperatorIntentVerbs.Answer, OperatorIntentVerbs.Answer, "null", false, false)]
    public void Policy_admits_only_matching_batches(string? applied, string verb, string payload,
        bool canJoin, bool clarification)
    {
        if (payload is "Clarification" or "HumanInput")
            payload = JsonSerializer.Serialize(new AnswerOperatorIntentPayload(
                Enum.Parse<OperatorAnswerTargetKind>(payload), "item", "goal", "answer", OperatorActorKind.Human),
                OperatorIntentJson.Options);
        var intent = new OperatorIntentRecord("intent", "key", verb, "goal", null, payload, [],
            "operator", "cli", "local-process", DateTimeOffset.UnixEpoch);

        Assert.Equal(canJoin, OperatorIntentBatchPolicy.CanJoinPass(applied is null ? [] : [applied], intent));
        Assert.Equal(clarification, OperatorIntentBatchPolicy.IsClarificationAnswer(intent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_hold_considers_every_answer_in_the_pass(bool includesOwner)
    {
        using var fixture = new Fixture();
        var a = await fixture.Raise("a");
        var b = await fixture.Raise("b");
        var c = await fixture.Raise("c");
        var d = await fixture.Raise("d");
        const string state = "author-owner-question";
        var hold = fixture.Kernel.ObserveGoalHold(fixture.Goal.Id, state,
            $"{state} item=Clarification:{a.Id} reason=model-ask-owner question=Which scope?",
            DateTimeOffset.UnixEpoch, TimeSpan.MaxValue, $"{state}:Clarification:{a.Id}").Hold;
        var answers = new[]
        {
            await fixture.Answer(b, "answer-b"),
            await fixture.Answer(includesOwner ? a : c, includesOwner ? "answer-a" : "answer-c"),
            await fixture.Answer(includesOwner ? c : d, includesOwner ? "answer-c" : "answer-d")
        };

        var result = OperatorIntentGoalApplication.ApplyPending(fixture.Kernel, fixture.Goal, fixture.Coordinator,
            new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
                TrunkBranchName.Default, fixture.Workspace.ExecutionDirectory), fixture.Capture()).Result;

        AssertAppliedLines(result, answers);
        Assert.Equal(!includesOwner, result.PreserveGoalHold);
        if (includesOwner)
            Assert.Null(fixture.Goal.CurrentHold);
        else
            Assert.Equal(hold, fixture.Goal.CurrentHold);
        foreach (var answer in answers)
        {
            var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(answer.PayloadJson, OperatorIntentJson.Options)!;
            var item = Assert.Single(await fixture.Collaboration.ListAsync(fixture.Goal.Id.Value).WaitAsync(Bound),
                candidate => candidate.Id == payload.TargetId);
            Assert.Equal(CollaborationItemStatus.Resolved, item.Status);
            Assert.Equal(payload.Text, item.Resolution);
        }
        fixture.Complete();
        await fixture.AssertStatuses(answers, OperatorIntentStatus.Applied);
    }

    private static void AssertAppliedLines(OperatorIntentExecutionResult result, IReadOnlyList<OperatorIntentRecord> intents)
    {
        Assert.True(result.MutatedGoalState);
        Assert.Equal(intents.Count, result.ProgressLines.Count);
        for (var i = 0; i < intents.Count; i++)
            AssertLine(result.ProgressLines[i], intents[i], "applied-pending-commit");
    }

    private static void AssertLine(string line, OperatorIntentRecord intent, string outcome)
    {
        Assert.Contains($"id={intent.Id} ", line);
        Assert.Contains($"verb={intent.Verb} ", line);
        Assert.Contains($"result={outcome}", line);
    }

    // Every store is isolated under this fixture's unique temporary directory.
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"clarification-batch-{Guid.NewGuid():N}");
        private int _sequence;
        public OrchestratorWorkspace Workspace { get; }
        public CollaborationItemStore Collaboration { get; }
        public SqliteOperatorIntentStore Store { get; }
        public AgentOrchestratorKernel Kernel { get; } = new();
        public Goal Goal { get; }
        public OperatorIntentCoordinator Coordinator { get; }

        public Fixture(bool simpleGoal = false)
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Collaboration = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            var refinement = new GoalRefinementService(new InMemoryModelProviderRegistry([]),
                ModelFunctionCatalog.Empty, Collaboration, new SpecRefinerPrecedentStore(Workspace.SpecRefinerPrecedentsPath));
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            if (simpleGoal)
                Goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, AgentCatalog.Default().Agents, "Clarify scope");
            else
            {
                Goal = Kernel.CreateGoal("Clarify scope", [new TaskSpec(TaskId.New(), "Choose scope", AgentRole.Developer)]);
                Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            }
            Coordinator = new OperatorIntentCoordinator(Store, decisions: Collaboration, goalStateVersionResolver: _ => 0,
                clarificationAnswers: (correlation, text, version) =>
                    refinement.TryResolveOpenClarificationAsync(correlation, text, version));
        }

        public Task<CollaborationItem> Raise(string name) => Collaboration.RaiseAsync(CollaborationItemType.Clarification,
            Goal.Id.Value, $"Scope {name}", $"Which scope for {name}?", $"spec-clarification:{Goal.Id.Value}:scope:{name}")
            .WaitAsync(Bound);

        public Task<OperatorIntentRecord> Answer(CollaborationItem item, string text) => Enqueue(OperatorIntentVerbs.Answer,
            new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification, item.Id, Goal.Id.Value,
                text, OperatorActorKind.Human));

        public Task<OperatorIntentRecord> Enqueue<T>(string verb, T payload, string? taskId = null) =>
            Store.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                verb, Goal.Id.Value, taskId, JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [],
                "operator", "cli", "local-process", DateTimeOffset.UnixEpoch.AddSeconds(++_sequence))).WaitAsync(Bound);

        public IReadOnlySet<string> Capture() => Coordinator.CaptureGoalBatch().Select(intent => intent.Id).ToHashSet();
        public OperatorIntentExecutionResult Execute(bool captured = true) => Coordinator.ExecutePending(Kernel, Goal,
            captured ? Capture() : null);
        public void Complete() => Coordinator.CompletePersisted([Goal.Id]);
        public async Task<OperatorIntentRecord> Row(OperatorIntentRecord intent) =>
            (await Store.GetAsync(intent.Id).WaitAsync(Bound))!;

        public async Task AssertStatuses(IEnumerable<OperatorIntentRecord> intents, OperatorIntentStatus status)
        {
            foreach (var intent in intents)
                Assert.Equal(status, (await Row(intent)).Status);
        }

        public async Task AssertResolved(CollaborationItem item, string text)
        {
            var resolved = Assert.Single(await Collaboration.ListAsync(Goal.Id.Value).WaitAsync(Bound),
                candidate => candidate.Id == item.Id);
            Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
            Assert.Equal(text, resolved.Resolution);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
