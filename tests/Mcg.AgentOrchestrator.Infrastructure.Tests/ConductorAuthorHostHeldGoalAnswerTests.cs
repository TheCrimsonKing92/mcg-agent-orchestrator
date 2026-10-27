using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each fixture owns its temporary workspace, SQLite stores, clock and model signals.
public sealed class ConductorAuthorHostHeldGoalAnswerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 32, 21, TimeSpan.Zero);
    private const string Standing = "Workers must not run tests themselves; name the test classes and let the conductor execute them.";
    private const string Brief = "Add a widget counter.\n" + Standing;
    private const string OwnerQuestion = "Which runtime should the owner select?";
    private const string PlainQuestion = "Who executes tests and supplies review evidence?";
    private const string Answer = "The conductor does: " + Standing;
    private static readonly string PlainOutput = JsonSerializer.Serialize(new
        { kind = "answer", text = Answer, briefCitations = new[] { Standing } });
    private static readonly string OwnerOutput = JsonSerializer.Serialize(new
        { kind = "ask-owner", question = OwnerQuestion, recommendation = "Use Windows." });

    [Xunit.Theory]
    [Xunit.InlineData("author-owner-question")]
    [Xunit.InlineData("steward-owner-question")]
    public async Task Standing_hold_harvests_all_ready_plain_answers_without_changing_hold(string state)
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateGoal();
        var owner = await fixture.Raise(goal, "owner", OwnerQuestion);
        var hold = fixture.Hold(goal, owner, state);
        var answers = new List<CollaborationItem>();
        for (var i = 0; i < 3; i++)
        {
            var answer = await fixture.Raise(goal, $"plain-{i}", PlainQuestion);
            fixture.Ready(goal, answer, PlainQuestion, PlainOutput);
            answers.Add(answer);
        }
        Xunit.Assert.Equal(3, fixture.Claims.Ready().Count);

        fixture.Host.ServiceTick(fixture.Kernel);

        var intents = await fixture.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound);
        AssertAnswerTargets(intents, answers);
        AssertHold(hold, goal);
        foreach (var answer in answers)
            Xunit.Assert.Equal("answer-submitted", fixture.Outcome(goal, answer));
        Xunit.Assert.Empty(fixture.Claims.Ready());
        Xunit.Assert.Empty(fixture.Host.CurrentRounds);
        Xunit.Assert.Equal(0, fixture.Model.Calls);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, (await fixture.Reload(goal, owner)).Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData("author-owner-question")]
    [Xunit.InlineData("steward-owner-question")]
    public async Task ApplyPending_unrelated_answers_preserves_hold_until_owner_answer(string state)
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateGoal();
        var owner = await fixture.Raise(goal, "owner", OwnerQuestion);
        var hold = fixture.Hold(goal, owner, state);
        var answers = new List<CollaborationItem>();
        for (var i = 0; i < 3; i++)
        {
            var answer = await fixture.Raise(goal, $"plain-{i}", PlainQuestion);
            fixture.Ready(goal, answer, PlainQuestion, PlainOutput);
            answers.Add(answer);
        }
        fixture.Kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(Brief, [Standing],
            VerificationClass.TestVerifiable, [], answers.Append(owner).Select(item =>
                new RefinedSpecOpenQuestion(item.CorrelationKey!, item.Body, "runtime", "Open")).ToArray()));
        fixture.Host.ServiceTick(fixture.Kernel);
        AssertAnswerTargets(await fixture.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound), answers);

        var coordinator = fixture.Coordinator();
        foreach (var _ in answers)
        {
            var result = fixture.ApplyPending(goal, coordinator).Result;
            Xunit.Assert.True(result.MutatedGoalState);
            Xunit.Assert.True(result.PreserveGoalHold);
            AssertHold(hold, goal);
            // A following tick before persistence must retain the same hold as well.
            var waiting = fixture.ApplyPending(goal, coordinator).Result;
            Xunit.Assert.True(waiting.PreserveGoalHold);
            Xunit.Assert.Contains(waiting.ProgressLines, line => line.Contains("waiting-for-state-commit"));
            AssertHold(hold, goal);
            coordinator.CompletePersisted([goal.Id]);
            Xunit.Assert.Equal(CollaborationItemStatus.Raised, (await fixture.Reload(goal, owner)).Status);
        }
        foreach (var answer in answers)
            Xunit.Assert.Equal(CollaborationItemStatus.Resolved, (await fixture.Reload(goal, answer)).Status);

        await fixture.EnqueueOwnerAnswer(goal, owner);
        var ownerResult = fixture.ApplyPending(goal, coordinator).Result;
        Xunit.Assert.True(ownerResult.MutatedGoalState);
        Xunit.Assert.False(ownerResult.PreserveGoalHold);
        Xunit.Assert.Null(goal.CurrentHold);
        coordinator.CompletePersisted([goal.Id]);
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, (await fixture.Reload(goal, owner)).Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData("author-owner-question")]
    [Xunit.InlineData("steward-owner-question")]
    public async Task ApplyPending_non_answer_mutation_clears_owner_hold(string state)
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateGoal();
        var owner = await fixture.Raise(goal, "owner", OwnerQuestion);
        fixture.Hold(goal, owner, state);
        var task = Xunit.Assert.Single(goal.Tasks);
        await fixture.EnqueueProgress(goal, task.Id);

        var coordinator = fixture.Coordinator();
        var result = fixture.ApplyPending(goal, coordinator).Result;

        Xunit.Assert.True(result.MutatedGoalState);
        Xunit.Assert.False(result.PreserveGoalHold);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.Null(goal.CurrentHold);
        coordinator.CompletePersisted([goal.Id]);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, (await fixture.Reload(goal, owner)).Status);
    }

    [Xunit.Fact]
    public async Task Mixed_batch_applies_three_answers_and_only_owner_resolution_releases_hold()
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateGoal();
        var answers = new List<CollaborationItem>();
        for (var i = 0; i < 3; i++)
            answers.Add(await fixture.Raise(goal, $"plain-{i}", PlainQuestion));
        // Raise the owner item last: harvesting must still handle its result first.
        var owner = await fixture.Raise(goal, "owner", OwnerQuestion);
        var questions = answers.Append(owner).ToArray();
        fixture.Kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(Brief, [Standing],
            VerificationClass.TestVerifiable, [], questions.Select(item =>
                new RefinedSpecOpenQuestion(item.CorrelationKey!, item.Body, "runtime", "Open")).ToArray()));

        fixture.Host.ServiceTick(fixture.Kernel);
        var rounds = fixture.Host.CurrentRounds.ToArray();
        Xunit.Assert.Equal(4, rounds.Length);
        fixture.Model.Release.TrySetResult();
        await AwaitRounds(rounds, "all four mixed-batch Author rounds completed");
        fixture.Host.ServiceTick(fixture.Kernel);

        var hold = Xunit.Assert.IsType<GoalHoldState>(goal.CurrentHold);
        Xunit.Assert.Equal("author-owner-question", hold.State);
        Xunit.Assert.Contains($"item=Clarification:{owner.Id}", hold.Blocker);
        Xunit.Assert.Equal("owner-question", fixture.Outcome(goal, owner));
        var intents = await fixture.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound);
        AssertAnswerTargets(intents, answers);
        foreach (var answer in answers)
            Xunit.Assert.Equal("answer-submitted", fixture.Outcome(goal, answer));
        var authorEvents = fixture.Events("author");
        Xunit.Assert.Contains($"item={fixture.Item(goal, owner, OwnerQuestion).Identity} kind=ask-owner",
            authorEvents[0].GetProperty("detail").GetString());

        var coordinator = fixture.Coordinator();
        // Exercise the named coordinator seam; the production tick wrapper has a separate operator check.
        for (var i = 0; i < 3; i++)
        {
            Xunit.Assert.True(coordinator.ExecutePending(fixture.Kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);
            fixture.Host.ServiceTick(fixture.Kernel);
            AssertHold(hold, goal);
            Xunit.Assert.Equal(CollaborationItemStatus.Raised, (await fixture.Reload(goal, owner)).Status);
            var planner = Xunit.Assert.Single(goal.Tasks);
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, planner.Status);
            Xunit.Assert.Null(planner.LastProcess);
            Xunit.Assert.Empty(fixture.Host.CurrentRounds);
            Xunit.Assert.Empty(fixture.Events("owner-question-hold-reconciled"));
        }
        foreach (var intent in intents)
            Xunit.Assert.Equal(OperatorIntentStatus.Applied,
                (await fixture.Intents.GetAsync(intent.Id).WaitAsync(Bound))!.Status);
        foreach (var answer in answers)
        {
            var resolved = await fixture.Reload(goal, answer);
            Xunit.Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
            Xunit.Assert.Equal(Answer, resolved.AuthoritativeAnswer?.Text);
        }
        Xunit.Assert.Equal(4, fixture.Model.Calls);
        AssertAnswerTargets(await fixture.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound), answers);

        await fixture.EnqueueOwnerAnswer(goal, owner);
        Xunit.Assert.True(coordinator.ExecutePending(fixture.Kernel, goal).MutatedGoalState);
        coordinator.CompletePersisted([goal.Id]);
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, (await fixture.Reload(goal, owner)).Status);
        AssertHold(hold, goal); // The existing Author reconciler owns release.
        fixture.Host.ServiceTick(fixture.Kernel);

        Xunit.Assert.Null(goal.CurrentHold);
        var reconciled = Xunit.Assert.Single(fixture.Events("owner-question-hold-reconciled"));
        Xunit.Assert.Contains($"item=Clarification:{owner.Id} target=resolved",
            reconciled.GetProperty("detail").GetString());
        fixture.Host.ServiceTick(fixture.Kernel);
        Xunit.Assert.Single(fixture.Events("owner-question-hold-reconciled"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("author-owner-question", "owner-answer")]
    [Xunit.InlineData("author-owner-question", "ask-owner")]
    [Xunit.InlineData("author-owner-question", "frozen-fact-ruling")]
    [Xunit.InlineData("author-owner-question", "unparseable")]
    [Xunit.InlineData("steward-owner-question", "owner-answer")]
    [Xunit.InlineData("steward-owner-question", "ask-owner")]
    [Xunit.InlineData("steward-owner-question", "frozen-fact-ruling")]
    [Xunit.InlineData("steward-owner-question", "unparseable")]
    public async Task Standing_hold_preserves_ineligible_results_as_ready(string state, string kind)
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateGoal();
        var owner = await fixture.Raise(goal, "owner", OwnerQuestion);
        var hold = fixture.Hold(goal, owner, state);
        var blocked = await fixture.Raise(goal, "blocked", PlainQuestion);
        var output = kind switch
        {
            "owner-answer" => "{\"kind\":\"answer\",\"text\":\"An unsupported answer.\"}",
            "ask-owner" => OwnerOutput,
            "frozen-fact-ruling" => FrozenOutput(),
            _ => "not JSON"
        };
        var parsed = ConductorAuthorResultParser.Parse(output);
        if (kind == "owner-answer")
        {
            Xunit.Assert.Equal("answer", parsed!.Kind);
            Xunit.Assert.NotNull(ConductorAuthorAnswerRouting.Decide(
                fixture.Item(goal, blocked, PlainQuestion), parsed.Text!, parsed.EvidenceReferences,
                goal.Objective, parsed.BriefCitations).EscalationReason);
        }
        else if (kind == "unparseable") Xunit.Assert.Null(parsed);
        else Xunit.Assert.Equal(kind, parsed!.Kind);
        fixture.Ready(goal, blocked, PlainQuestion, output);

        fixture.Host.ServiceTick(fixture.Kernel);
        fixture.Host.ServiceTick(fixture.Kernel);

        Xunit.Assert.Empty(await fixture.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        Xunit.Assert.Equal("ready", fixture.Outcome(goal, blocked));
        var ready = Xunit.Assert.Single(fixture.Claims.Ready());
        Xunit.Assert.Equal(fixture.Item(goal, blocked, PlainQuestion).Identity, ready.Item.Identity);
        Xunit.Assert.Equal(output, ready.Output);
        AssertHold(hold, goal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("author-owner-question")]
    [Xunit.InlineData("steward-owner-question")]
    public async Task Standing_hold_does_not_claim_or_start_new_author_rounds(string state)
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateGoal();
        var owner = await fixture.Raise(goal, "owner", OwnerQuestion);
        var hold = fixture.Hold(goal, owner, state);
        var fresh = await fixture.Raise(goal, "fresh", PlainQuestion);

        fixture.Host.ServiceTick(fixture.Kernel);

        Xunit.Assert.Equal(0, fixture.Model.Calls);
        Xunit.Assert.Empty(fixture.Host.CurrentRounds);
        Xunit.Assert.Null(fixture.Outcome(goal, fresh));
        Xunit.Assert.Null(fixture.Outcome(goal, owner));
        Xunit.Assert.Empty(await fixture.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        AssertHold(hold, goal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("author-owner-question")]
    [Xunit.InlineData("steward-owner-question")]
    public async Task Running_same_goal_round_defers_ready_answer_until_round_completes(string state)
    {
        using var fixture = new Fixture();
        var goal = fixture.CreateGoal();
        var ready = await fixture.Raise(goal, "ready", PlainQuestion);
        fixture.Ready(goal, ready, PlainQuestion, PlainOutput);
        var pending = await fixture.Raise(goal, "pending", PlainQuestion);
        fixture.Host.ServiceTick(fixture.Kernel);
        var round = Xunit.Assert.Single(fixture.Host.CurrentRounds);
        Xunit.Assert.False(round.IsCompleted);
        Xunit.Assert.Equal("in-flight", fixture.Outcome(goal, pending));
        var owner = await fixture.Raise(goal, "owner", OwnerQuestion);
        var hold = fixture.Hold(goal, owner, state);

        fixture.Host.ServiceTick(fixture.Kernel);

        Xunit.Assert.Equal("ready", fixture.Outcome(goal, ready));
        Xunit.Assert.Empty(await fixture.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        AssertHold(hold, goal);
        fixture.Model.Release.TrySetResult();
        await AwaitRounds([round], "the remaining same-goal Author round completed");
        fixture.Host.ServiceTick(fixture.Kernel);

        AssertAnswerTargets(await fixture.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound), [ready, pending]);
        Xunit.Assert.Equal("answer-submitted", fixture.Outcome(goal, ready));
        Xunit.Assert.Equal("answer-submitted", fixture.Outcome(goal, pending));
        Xunit.Assert.Equal(1, fixture.Model.Calls);
        Xunit.Assert.Empty(fixture.Host.CurrentRounds);
        AssertHold(hold, goal);
    }

    private static void AssertHold(GoalHoldState expected, Goal goal)
    {
        var actual = Xunit.Assert.IsType<GoalHoldState>(goal.CurrentHold);
        Xunit.Assert.Equal(expected, actual); // Includes state, identity, blocker and original StartedAt.
        Xunit.Assert.True(ConductorOwnerQuestionHolds.ExcludesFromWalk(actual.State));
    }

    private static void AssertAnswerTargets(IReadOnlyList<OperatorIntentRecord> intents,
        IReadOnlyList<CollaborationItem> targets)
    {
        Xunit.Assert.Equal(targets.Count, intents.Count);
        var payloads = intents.Select(intent =>
        {
            Xunit.Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
            Xunit.Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
            var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
            Xunit.Assert.Equal(OperatorActorKind.Agent, payload.ActorKind);
            Xunit.Assert.Equal(OperatorAnswerTargetKind.Clarification, payload.TargetKind);
            Xunit.Assert.Equal(Answer, payload.Text);
            return payload;
        }).ToArray();
        Xunit.Assert.Equal(targets.Select(item => item.Id).Order(), payloads.Select(item => item.TargetId).Order());
    }

    private static async Task AwaitRounds(Task[] rounds, string completion)
    {
        // Hang detector only: model completion is controlled by an explicit signal, never a delay.
        try { await Task.WhenAll(rounds).WaitAsync(Bound); }
        catch (TimeoutException ex) { throw new TimeoutException($"Not observed: {completion}.", ex); }
    }

    private static string FrozenOutput() => JsonSerializer.Serialize(new
    {
        kind = "frozen-fact-ruling", frozenClasses = new[] { "FrozenFixtureTests" },
        amendedFacts = new[] { new { fact = "FrozenFixtureTests.PinsPath", file = "tests/Fixture.cs", allowedChange = "Update path." } },
        basis = "The source moved.", unmodified = "Every other fact in FrozenFixtureTests stays unmodified.",
        diffCheck = "Confirm only PinsPath changes.", evidenceReferences = new[] { "tests/Fixture.cs:1" }
    });

    private sealed class ControlledModel : IConductorAuthorModelRound
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Release.Task.WaitAsync(cancellationToken);
            return input.Item.Question == OwnerQuestion ? OwnerOutput : PlainOutput;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"author-held-{Guid.NewGuid():N}");
        private readonly OrchestratorWorkspace _workspace;
        private readonly CollaborationItemStore _collaboration;
        private readonly GoalRefinementService _refinement;
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal ControlledModel Model { get; } = new();
        internal ConductorAuthorClaimStore Claims { get; }
        internal SqliteOperatorIntentStore Intents { get; }
        internal ConductorAuthorHost Host { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            _collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            Claims = new ConductorAuthorClaimStore(Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db"));
            Intents = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
            var precedents = new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath);
            _refinement = new GoalRefinementService(new InMemoryModelProviderRegistry([]),
                ModelFunctionCatalog.Empty, _collaboration, precedents);
            Host = new ConductorAuthorHost(Claims, _collaboration, Model, Intents, precedents, _ => _root,
                new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                new ConductEventLogWriter(_workspace.ConductEventsLogPath, utcNow: () => Now), utcNow: () => Now);
        }

        internal Goal CreateGoal()
        {
            var goal = Kernel.CreateGoal(Brief, [new TaskSpec(TaskId.New(), "Plan widget", AgentRole.Planner)]);
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            return goal;
        }

        internal Task<CollaborationItem> Raise(Goal goal, string key, string question) =>
            _collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value, key,
                $"Question: {question}\nFork kind: runtime", $"spec-clarification:{goal.Id.Value}:{key}").WaitAsync(Bound);

        internal ConductorAuthorItem Item(Goal goal, CollaborationItem item, string question) =>
            new(OperatorAnswerTargetKind.Clarification, item.Id, goal.Id.Value, question, "runtime");

        internal void Ready(Goal goal, CollaborationItem item, string question, string output)
        {
            var authorItem = Item(goal, item, question);
            Xunit.Assert.True(Claims.TryClaim(authorItem, Now));
            Claims.Preserve(authorItem, output);
            Xunit.Assert.Equal("ready", Outcome(goal, item));
        }

        internal GoalHoldState Hold(Goal goal, CollaborationItem owner, string state) =>
            Kernel.ObserveGoalHold(goal.Id, state,
                $"{state} item=Clarification:{owner.Id} reason=model-ask-owner question={OwnerQuestion}",
                Now, TimeSpan.MaxValue, $"{state}:Clarification:{owner.Id}").Hold;

        internal async Task<CollaborationItem> Reload(Goal goal, CollaborationItem item) =>
            (await _collaboration.ListAsync(goal.Id.Value).WaitAsync(Bound)).Single(candidate => candidate.Id == item.Id);

        internal OperatorIntentCoordinator Coordinator() => new(Intents, decisions: _collaboration,
            goalStateVersionResolver: _ => 0,
            clarificationAnswers: (correlation, text, version) =>
                _refinement.TryResolveOpenClarificationAsync(correlation, text, version));

        internal OperatorIntentGoalApplicationResult ApplyPending(Goal goal, OperatorIntentCoordinator coordinator) =>
            OperatorIntentGoalApplication.ApplyPending(Kernel, goal, coordinator,
                new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(_workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
                    TrunkBranchName.Default, _workspace.ExecutionDirectory));

        internal async Task EnqueueProgress(Goal goal, TaskId taskId)
        {
            var payload = new ProgressOperatorIntentPayload(WorkTaskStatus.Running, "Continue planning.");
            await Intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Progress, goal.Id.Value, taskId.Value,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "owner", "cli",
                "local-process", Now)).WaitAsync(Bound);
        }

        internal async Task EnqueueOwnerAnswer(Goal goal, CollaborationItem owner)
        {
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification,
                owner.Id, goal.Id.Value, "Use Windows.", OperatorActorKind.Human, [], null);
            await Intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "owner", "cli",
                "local-process", Now, ActorKind: OperatorActorKind.Human)).WaitAsync(Bound);
        }

        internal string? Outcome(Goal goal, CollaborationItem item)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db"), Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT outcome FROM author_claims WHERE identity = $identity";
            command.Parameters.AddWithValue("$identity", Item(goal, item, "").Identity);
            return command.ExecuteScalar() as string;
        }

        internal JsonElement[] Events(string kind) => File.ReadAllLines(_workspace.ConductEventsLogPath)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .Where(entry => entry.GetProperty("eventKind").GetString() == kind).ToArray();

        public void Dispose()
        {
            Host.Stop();
            Directory.Delete(_root, recursive: true);
        }
    }
}
