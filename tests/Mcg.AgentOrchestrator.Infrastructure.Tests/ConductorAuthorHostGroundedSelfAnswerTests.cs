using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each harness owns a unique temporary workspace and SQLite stores; no shared state.
public sealed class ConductorAuthorHostGroundedSelfAnswerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const string Standing = "Workers must not run tests themselves; name the test classes and let the conductor execute them.";
    private const string Question = "Who executes tests and supplies review evidence?";
    private const string Answer = "The conductor does: " + Standing;
    private const string Fabricated = "The Developer may edit any file it needs to finish the change.";
    private static readonly string Brief = string.Join("\n",
        "Add a widget counter to src/Example/Widget.cs.",
        "Criterion: WidgetCounterCheck asserts the count. Developer owns; Acceptance executes.",
        "Scope: src/Example/Widget.cs and tests/Example/WidgetCounterCheck.cs.",
        Standing);

    [Xunit.Fact]
    public async Task Grounded_answer_submits_agent_intent_with_brief_provenance()
    {
        using var harness = new AuthorHarness(Answer, Standing);
        var goal = harness.Kernel.CreateGoal(Brief);
        var clarification = await harness.Raise(goal, Question);
        var item = new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification,
            clarification.Id, goal.Id.Value, Question, "runtime");

        await harness.RoundTrip();

        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Xunit.Assert.Equal(OperatorIntentVerbs.Answer, intent.Verb);
        Xunit.Assert.Equal("author", intent.Actor);
        Xunit.Assert.Equal("conductor-author", intent.Channel);
        Xunit.Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Xunit.Assert.Equal(OperatorActorKind.Agent, payload.ActorKind);
        Xunit.Assert.Equal(OperatorAnswerTargetKind.Clarification, payload.TargetKind);
        Xunit.Assert.Equal(clarification.Id, payload.TargetId);
        Xunit.Assert.Equal(Answer, payload.Text);
        Xunit.Assert.Equal(new[] { "brief-span=" + Standing, "grounded-reason=missing-evidence",
            "author-item=" + item.Identity }, payload.EvidenceReferences);
        Xunit.Assert.Null(goal.CurrentHold);
        Xunit.Assert.Equal("answer-submitted", harness.Outcome(item.Identity));
        Xunit.Assert.Contains($"item={item.Identity} kind=grounded-answer", File.ReadAllText(harness.ConductPath));
    }

    [Xunit.Theory]
    [Xunit.InlineData("May the Developer widen the scope to also edit src/Example/Other.cs?",
        Answer + " The Developer may also edit src/Example/Other.cs.", Standing, "authority-widening")]
    [Xunit.InlineData(Question, Fabricated, Fabricated, "missing-evidence")]
    public async Task Unsupported_answer_retains_baseline_owner_hold(
        string question, string answer, string citation, string expectedReason)
    {
        using var harness = new AuthorHarness(answer, citation);
        var goal = harness.Kernel.CreateGoal(Brief);
        var clarification = await harness.Raise(goal, question);
        var item = new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification,
            clarification.Id, goal.Id.Value, question, "runtime");
        var baseline = ConductorAuthorAnswerRouting.Decide(item, answer, null);
        Xunit.Assert.Equal(expectedReason, baseline.EscalationReason);

        await harness.RoundTrip();

        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(goal.Id.Value).WaitAsync(Bound));
        Xunit.Assert.Equal("author-owner-question", goal.CurrentHold?.State);
        Xunit.Assert.Contains($"reason={baseline.EscalationReason}", goal.CurrentHold!.Blocker);
        Xunit.Assert.Equal("owner-question", harness.Outcome(item.Identity));
        Xunit.Assert.DoesNotContain("kind=grounded-answer", File.ReadAllText(harness.ConductPath));
    }

    private sealed class FakeAuthorModel(string output) : IConductorAuthorModelRound
    {
        public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken) => Task.FromResult(output);
    }

    private sealed class AuthorHarness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"author-grounded-{Guid.NewGuid():N}");
        private readonly OrchestratorWorkspace _workspace;
        private readonly CollaborationItemStore _collaboration;
        private readonly ConductorAuthorHost _host;
        internal string ConductPath => _workspace.ConductEventsLogPath;
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal SqliteOperatorIntentStore Intents { get; }

        internal AuthorHarness(string answer, string citation)
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            _collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            Intents = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
            var output = JsonSerializer.Serialize(new { kind = "answer", text = answer, briefCitations = new[] { citation } });
            _host = new ConductorAuthorHost(
                new ConductorAuthorClaimStore(Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db")),
                _collaboration, new FakeAuthorModel(output), Intents,
                new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath), _ => _root,
                new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                new ConductEventLogWriter(_workspace.ConductEventsLogPath), enabled: true);
        }

        internal async Task<CollaborationItem> Raise(Goal goal, string question) =>
            await _collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
                "Runtime", $"Question: {question}\nFork kind: runtime", $"spec-clarification:{goal.Id.Value}:runtime")
                .WaitAsync(Bound);

        internal async Task RoundTrip()
        {
            _host.ServiceTick(Kernel);
            // The bound detects only a hung model round, never the behavior under assertion.
            await Task.WhenAll(_host.CurrentRounds).WaitAsync(Bound);
            _host.ServiceTick(Kernel);
        }

        internal string Outcome(string identity)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db"), Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT outcome FROM author_claims WHERE identity = $identity";
            command.Parameters.AddWithValue("$identity", identity);
            return Xunit.Assert.IsType<string>(command.ExecuteScalar());
        }

        public void Dispose()
        {
            _host.Stop();
            Directory.Delete(_root, recursive: true);
        }
    }
}
