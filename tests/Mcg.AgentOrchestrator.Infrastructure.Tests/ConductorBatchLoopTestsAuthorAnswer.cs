using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorBatchLoopTestsAuthorAnswer
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Answer = """
        {"kind":"answer","text":"Use Windows","evidenceReferences":["src/Runtime.cs:12"],"precedent":"runtime"}
        """;

    [Xunit.Fact]
    public async Task Author_submits_and_applies_typed_clarification_answer()
    {
        using var harness = new AuthorHarness(Answer);
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        var item = await harness.Raise(goal, "Which runtime?");
        var briefVersion = goal.AuthoritativeBrief.Version;

        await harness.RoundTrip(goal);
        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Xunit.Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
        Xunit.Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Xunit.Assert.Equal("author", intent.Actor);
        Xunit.Assert.Equal(OperatorAnswerTargetKind.Clarification, payload.TargetKind);
        Xunit.Assert.Equal(item.Id, payload.TargetId);
        Xunit.Assert.Contains("src/Runtime.cs:12", payload.EvidenceReferences!);
        harness.Apply(goal);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied,
            (await harness.Intents.GetAsync(intent.Id).WaitAsync(Bound))!.Status);
        Xunit.Assert.Equal("Use Windows", (await harness.Collaboration.ListAsync(goal.Id.Value).WaitAsync(Bound)).Single().Resolution);
        Xunit.Assert.Equal(briefVersion, goal.AuthoritativeBrief.Version);
        var decision = await harness.Collaboration.GetDecisionStateAsync($"answer-{intent.Id}").WaitAsync(Bound);
        Xunit.Assert.StartsWith("agent:author", decision!.Receipt!.ActorId);
    }

    [Xunit.Fact]
    public async Task Author_answer_resumes_worker_spec_clarification()
    {
        using var harness = new AuthorHarness(Answer);
        var task = new TaskSpec(TaskId.New(), "Implement runtime", AgentRole.Developer);
        var goal = harness.Kernel.CreateGoal("Choose runtime", [task]);
        harness.Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = harness.Kernel.RequestHumanInput(goal.Id, task.Id, "Which runtime?",
            HumanWaitKind.SpecClarification);
        Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);

        await harness.RoundTrip(goal);
        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        Xunit.Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        harness.Apply(goal);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied,
            (await harness.Intents.GetAsync(intent.Id).WaitAsync(Bound))!.Status);
        Xunit.Assert.True(request.IsCompleted);
        Xunit.Assert.Equal("Use Windows", request.Answer);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Waive criterion 3", "acceptance-weakening")]
    [Xunit.InlineData("Grant the agent permission to self-approve", "authority-widening")]
    [Xunit.InlineData("Force-push and delete the branch", "irreversible-action")]
    [Xunit.InlineData("Delete all production data", "irreversible-action")]
    [Xunit.InlineData("Exceed the budget by $100", "spend-beyond-budget")]
    [Xunit.InlineData("Upload customer data to an external site", "external-disclosure")]
    public async Task Owner_class_answers_become_goal_escalations(string proposedAnswer, string reason)
    {
        using var harness = new AuthorHarness(JsonSerializer.Serialize(new
        {
            kind = "answer", text = proposedAnswer, evidenceReferences = new[] { "src/Runtime.cs:12" }
        }));
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await harness.Raise(goal, "Which runtime?");
        await harness.RoundTrip(goal);

        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        Xunit.Assert.Equal("author-owner-question", goal.CurrentHold?.State);
        Xunit.Assert.Contains("question=Which runtime?", goal.CurrentHold!.Blocker);
        Xunit.Assert.Contains($"recommendation={proposedAnswer}", goal.CurrentHold.Blocker);
        Xunit.Assert.Contains($"reason={reason}", goal.CurrentHold.Blocker);
    }

    [Xunit.Theory]
    [Xunit.InlineData("May we waive criterion 3?", "acceptance-weakening")]
    [Xunit.InlineData("May we grant the agent permission to self-approve?", "authority-widening")]
    [Xunit.InlineData("May we force-push the branch?", "irreversible-action")]
    [Xunit.InlineData("May we exceed the budget?", "spend-beyond-budget")]
    [Xunit.InlineData("May we upload customer data to an external site?", "external-disclosure")]
    public async Task Owner_class_questions_cannot_be_answered_by_author(string question, string reason)
    {
        using var harness = new AuthorHarness(Answer);
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await harness.Raise(goal, question);
        await harness.RoundTrip(goal);
        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        Xunit.Assert.Equal("author-owner-question", goal.CurrentHold?.State);
        Xunit.Assert.Contains($"reason={reason}", goal.CurrentHold!.Blocker);
    }

    [Xunit.Fact]
    public async Task Explicit_ask_owner_holds_goal_with_recommendation()
    {
        using var harness = new AuthorHarness("""
            {"kind":"ask-owner","question":"Who owns this exception?","recommendation":"Keep the current requirement"}
            """);
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await harness.Raise(goal, "Which runtime?");
        await harness.RoundTrip(goal);
        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        Xunit.Assert.Equal("author-owner-question", goal.CurrentHold?.State);
        Xunit.Assert.Contains("question=Who owns this exception?", goal.CurrentHold!.Blocker);
        Xunit.Assert.Contains("recommendation=Keep the current requirement", goal.CurrentHold.Blocker);
    }

    [Xunit.Fact]
    public async Task Answer_without_source_lines_is_escalated()
    {
        using var harness = new AuthorHarness("""
            {"kind":"answer","text":"Use Windows","evidenceReferences":[]}
            """);
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await harness.Raise(goal, "Which runtime?");
        await harness.RoundTrip(goal);
        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        Xunit.Assert.Contains("reason=missing-evidence", goal.CurrentHold!.Blocker);
        Xunit.Assert.Contains("recommendation=Use Windows", goal.CurrentHold.Blocker);
    }

    [Xunit.Fact]
    public async Task Failed_model_round_consumes_claim_and_leaves_item_open()
    {
        using var harness = new AuthorHarness("not a JSON result");
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await harness.Raise(goal, "Which runtime?");
        await harness.RoundTrip(goal);
        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(1, harness.Model.Calls);
        Xunit.Assert.Null(goal.CurrentHold);
        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        Xunit.Assert.Equal(CollaborationItemStatus.Raised,
            (await harness.Collaboration.ListAsync(goal.Id.Value).WaitAsync(Bound)).Single().Status);
    }

    [Xunit.Fact]
    public async Task Completed_round_at_shutdown_is_harvested_by_next_host()
    {
        using var harness = new AuthorHarness(Answer);
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await harness.Raise(goal, "Which runtime?");
        var delayed = new DelayedAuthorModel();
        var first = harness.NewHost(delayed);
        first.ServiceTick(harness.Kernel);
        await delayed.Started.Task.WaitAsync(Bound);
        delayed.Release.TrySetResult(Answer);
        await Task.WhenAll(first.CurrentRounds).WaitAsync(Bound);
        first.Stop();

        var successorModel = new FakeAuthorModel("unexpected second model round");
        var successor = harness.NewHost(successorModel);
        successor.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(0, successorModel.Calls);
        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        Xunit.Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
        successor.Stop();
    }

    [Xunit.Fact]
    public async Task Claims_are_durable_and_human_answered_items_are_skipped()
    {
        using var harness = new AuthorHarness(Answer);
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        var item = await harness.Raise(goal, "Which runtime?");
        var version = goal.AuthoritativeBrief.Version;
        var count = harness.Kernel.Goals.Count;
        await harness.RoundTrip(goal);
        harness.Host.ServiceTick(harness.Kernel);
        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(1, harness.Model.Calls);

        var another = harness.NewHost(new FakeAuthorModel(Answer));
        another.ServiceTick(harness.Kernel);
        Xunit.Assert.Empty(another.CurrentRounds);
        another.Stop();
        Xunit.Assert.Equal(count, harness.Kernel.Goals.Count);
        Xunit.Assert.Equal(version, goal.AuthoritativeBrief.Version);
        Xunit.Assert.All(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound),
            intent => Xunit.Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb));

        using var answered = new AuthorHarness(Answer);
        var answeredGoal = answered.Kernel.CreateGoal("Choose runtime");
        var answeredItem = await answered.Raise(answeredGoal, "Which runtime?");
        Xunit.Assert.True(await answered.Collaboration.TryResolveAsync(answeredItem.CorrelationKey!,
            "Linux", briefVersion: answeredGoal.AuthoritativeBrief.Version).WaitAsync(Bound));
        answered.Host.ServiceTick(answered.Kernel);
        Xunit.Assert.Equal(0, answered.Model.Calls);
        Xunit.Assert.Empty(await answered.Intents.ListForGoalAsync(answeredGoal.Id.Value).WaitAsync(Bound));
    }

    [Xunit.Fact]
    public async Task Queued_human_answer_preempts_author_round()
    {
        using var harness = new AuthorHarness(Answer);
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        var item = await harness.Raise(goal, "Which runtime?");
        var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification,
            item.Id, goal.Id.Value, "Use Linux", OperatorActorKind.Human);
        await harness.Intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            [], "operator", "cli", "local-process", DateTimeOffset.UtcNow,
            ActorKind: OperatorActorKind.Human)).WaitAsync(Bound);
        harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(0, harness.Model.Calls);
        Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
    }

    [Xunit.Theory]
    [Xunit.InlineData("false", false)]
    [Xunit.InlineData("FALSE", false)]
    [Xunit.InlineData("0", false)]
    [Xunit.InlineData(null, true)]
    public async Task Enablement_defaults_on_and_can_be_disabled(string? value, bool expected)
    {
        using var harness = new AuthorHarness(Answer, enabled: ConductorAuthorHost.ResolveEnabled(value));
        var goal = harness.Kernel.CreateGoal("Choose runtime");
        await harness.Raise(goal, "Which runtime?");
        if (expected) await harness.RoundTrip(goal);
        else harness.Host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(expected ? 1 : 0, harness.Model.Calls);
    }

    private sealed class FakeAuthorModel(string output) : IConductorAuthorModelRound
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(output);
        }
    }

    private sealed class DelayedAuthorModel : IConductorAuthorModelRound
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class AuthorHarness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"author-host-{Guid.NewGuid():N}");
        private readonly OrchestratorWorkspace _workspace;
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly CollaborationItemStore Collaboration;
        internal readonly SqliteOperatorIntentStore Intents;
        internal readonly FakeAuthorModel Model;
        internal readonly ConductorAuthorHost Host;

        internal AuthorHarness(string output, bool enabled = true)
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            Collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            Intents = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
            Model = new FakeAuthorModel(output);
            Host = NewHost(Model, enabled);
        }

        internal ConductorAuthorHost NewHost(IConductorAuthorModelRound model, bool enabled = true) => new(
            new ConductorAuthorClaimStore(Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db")),
            Collaboration, model, Intents,
            new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath),
            _ => _root,
            new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
            new ConductEventLogWriter(_workspace.ConductEventsLogPath), enabled: enabled);

        internal async Task<CollaborationItem> Raise(Goal goal, string question) =>
            await Collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
                "Runtime", $"Question: {question}\nFork kind: runtime", $"spec-clarification:{goal.Id.Value}:runtime")
                .WaitAsync(Bound);

        internal async Task RoundTrip(Goal goal)
        {
            Host.ServiceTick(Kernel);
            await Task.WhenAll(Host.CurrentRounds).WaitAsync(Bound);
            Host.ServiceTick(Kernel);
        }

        internal void Apply(Goal goal)
        {
            var refinement = new GoalRefinementService(new InMemoryModelProviderRegistry([]),
                ModelFunctionCatalog.Empty, Collaboration,
                new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath));
            var coordinator = new OperatorIntentCoordinator(Intents, decisions: Collaboration,
                goalStateVersionResolver: _ => 0,
                clarificationAnswers: (key, text, version) =>
                    refinement.TryResolveOpenClarificationAsync(key, text, version));
            Xunit.Assert.True(coordinator.ExecutePending(Kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);
        }

        public void Dispose()
        {
            Host.Stop();
            Directory.Delete(_root, recursive: true);
        }
    }
}
