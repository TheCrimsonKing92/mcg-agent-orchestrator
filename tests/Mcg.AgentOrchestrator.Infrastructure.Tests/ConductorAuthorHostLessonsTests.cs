using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAuthorHostLessonsTests
{
    [Xunit.Fact]
    public async Task Host_selects_general_and_fork_lessons_for_round_input_and_prompt()
    {
        using var fixture = new AuthorLessonsFixture();
        fixture.Lessons.Add("general", ["author"], situation: "General case", rule: "Read the brief");
        fixture.Lessons.Add("runtime", ["author:runtime"], situation: "Runtime fork", rule: "Check source");
        fixture.Lessons.Add("other-fork", ["author:other"]);
        fixture.Lessons.Add("steward-only", ["steward"]);
        fixture.Lessons.Add("retired", ["author"], retire: true);

        var input = await fixture.Dispatch();
        var prompt = fixture.Model.Prompt!;

        Xunit.Assert.Equal(new[] { "general", "runtime" },
            input.Lessons!.Lessons.Select(lesson => lesson.Id).OrderBy(id => id).ToArray());
        Xunit.Assert.Contains("general | situation: General case | rule: Read the brief", prompt);
        Xunit.Assert.Contains("runtime | situation: Runtime fork | rule: Check source", prompt);
        Xunit.Assert.DoesNotContain("other-fork |", prompt);
        Xunit.Assert.DoesNotContain("steward-only |", prompt);
        Xunit.Assert.DoesNotContain("retired |", prompt);
        Xunit.Assert.True(prompt.IndexOf("Operator lessons:", StringComparison.Ordinal) >
            prompt.IndexOf("Matching precedent:", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Host_still_dispatches_with_none_when_store_is_missing_or_unreadable(bool unreadable)
    {
        using var fixture = new AuthorLessonsFixture();
        if (unreadable) File.WriteAllText(fixture.Lessons.Path, "not a SQLite database");

        var input = await fixture.Dispatch();

        Xunit.Assert.Empty(input.Lessons!.Lessons);
        Xunit.Assert.Contains("Operator lessons:" + Environment.NewLine + "none", fixture.Model.Prompt);
        Xunit.Assert.Single(fixture.Notes);
    }

    private sealed class RecordingModel(string receiptDirectory) : IConductorAuthorModelRound
    {
        internal ConductorAuthorRoundInput? Input { get; private set; }
        internal string? Prompt { get; private set; }

        public async Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken)
        {
            Input = input;
            var round = new ClaudeConductorAuthorModelRound(receiptDirectory, (request, _) =>
            {
                Prompt = request.StandardInput;
                return Task.FromResult(new WorkerProcessRunResult(0,
                    """{"kind":"ask-owner","question":"Which runtime?","recommendation":"Inspect source"}""", ""));
            });
            return await round.DispatchAsync(input, workingDirectory, cancellationToken);
        }
    }

    private sealed class AuthorLessonsFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "author-lessons-" + Guid.NewGuid().ToString("N"));
        private readonly OrchestratorWorkspace _workspace;
        private readonly CollaborationItemStore _collaboration;
        private readonly SqliteOperatorIntentStore _intents;
        private readonly ConductorAuthorHost _host;
        internal ConductorLessonTestStore Lessons { get; } = new();
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal RecordingModel Model { get; }
        internal List<string> Notes { get; } = [];

        internal AuthorLessonsFixture()
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            Model = new RecordingModel(Path.Combine(_root, "author-rounds"));
            _collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            _intents = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
            _host = new ConductorAuthorHost(
                new ConductorAuthorClaimStore(Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db")),
                _collaboration, Model, _intents,
                new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath),
                _ => _root, new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                new ConductEventLogWriter(_workspace.ConductEventsLogPath),
                lessons: new ConductorLessonSelector(Lessons.Path, Notes.Add));
        }

        internal async Task<ConductorAuthorRoundInput> Dispatch()
        {
            var goal = Kernel.CreateGoal("Choose runtime");
            await _collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
                "Runtime", "Question: Which runtime?\nFork kind: runtime",
                $"spec-clarification:{goal.Id.Value}:runtime");
            _host.ServiceTick(Kernel);
            await Task.WhenAll(_host.CurrentRounds).WaitAsync(TimeSpan.FromSeconds(30));
            return Xunit.Assert.IsType<ConductorAuthorRoundInput>(Model.Input);
        }

        public void Dispose()
        {
            _host.Stop();
            Lessons.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
