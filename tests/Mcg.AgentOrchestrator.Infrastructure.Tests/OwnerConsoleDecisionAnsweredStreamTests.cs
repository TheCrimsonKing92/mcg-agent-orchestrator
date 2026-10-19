using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its workspace, clock, state, stream and output.
public sealed class OwnerConsoleDecisionAnsweredStreamTests
{
    private const string FirstId = "11111111111111111111111111111111";
    private const string SecondId = "22222222222222222222222222222222";

    [Theory]
    [InlineData(ProgressKind.HumanInputReceived)]
    [InlineData(ProgressKind.HumanInputSuperseded)]
    public async Task Event_DecisionResolvedElsewhereRetiresQuestionAndReloadsOnlyItsGoal(ProgressKind kind)
    {
        using var fixture = new Fixture();
        fixture.Questions.Items.Add(Question());
        var session = await fixture.Start();
        Assert.Contains("[1] 11111111 Continue work?", fixture.Harness.Output.Text);
        fixture.Questions.Items.Clear();
        fixture.AppendLifecycle(kind);
        Assert.Single(File.ReadAllLines(fixture.Log)); // Prove the real sink committed to the reader's path.

        await fixture.Wake(session);

        Assert.Equal(1, fixture.Questions.Reads);
        Assert.Equal(FirstId, Assert.Single(Assert.Single(fixture.State.Loads)));
        Assert.Equal(0, fixture.State.MetadataReads);
        var commandStart = fixture.Harness.Output.Text.Length;
        await session.HandleCommandAsync("answer 1 yes", CancellationToken.None);
        Assert.Contains("question 1 is no longer open", fixture.Harness.Output.Text[commandStart..]);
        Assert.Empty(fixture.Harness.Answers.Calls);
    }

    [Fact]
    public async Task Event_TaskCompletedMatchesExistingLifecycleBaseline()
    {
        using var candidate = new Fixture();
        using var baseline = new Fixture();
        var candidateSession = await candidate.Start();
        var baselineSession = await baseline.Start();
        candidate.AppendLifecycle(ProgressKind.TaskCompleted);
        baseline.Write("goal-lifecycle", "TaskCompleted task=task-1");

        var actual = await candidate.ObserveWake(candidateSession);
        var expected = await baseline.ObserveWake(baselineSession);

        Assert.Equal(0, actual.QuestionReads);
        Assert.Equal(FirstId, Assert.Single(Assert.Single(actual.Loads)));
        Assert.Equal(0, actual.MetadataReads);
        Assert.Contains("11111111 | First goal | Active | Developer", actual.Output);
        AssertSameWake(expected, actual);
    }

    [Fact]
    public async Task Event_RaisedDecisionMatchesExistingQuestionOnlyBaseline()
    {
        using var candidate = new Fixture();
        using var baseline = new Fixture();
        var candidateSession = await candidate.Start();
        var baselineSession = await baseline.Start();
        candidate.Questions.Items.Add(Question());
        baseline.Questions.Items.Add(Question());
        candidate.Write("goal-escalation", "changed");
        baseline.Write("goal-escalation", "changed");

        var actual = await candidate.ObserveWake(candidateSession);
        var expected = await baseline.ObserveWake(baselineSession);

        Assert.Equal(1, actual.QuestionReads);
        Assert.Empty(actual.Loads);
        Assert.Equal(0, actual.MetadataReads);
        Assert.Contains("[1] 11111111 Continue work?", actual.Output);
        AssertSameWake(expected, actual);
    }

    [Fact]
    public async Task Event_CoveredAnsweredReplayMatchesExistingCoveredBaseline()
    {
        using var candidate = new Fixture();
        using var baseline = new Fixture();
        var answered = candidate.Write("goal-decision-answered", "changed");
        var stage = baseline.Write("goal-lifecycle", "changed");
        // Both sequences 1 and 2 will be covered by startup; replay below and at the anchor.
        candidate.Write("goal", "anchor");
        baseline.Write("goal", "anchor");
        var candidateSession = await candidate.Start();
        var baselineSession = await baseline.Start();
        candidate.AppendRaw(answered);
        candidate.AppendRaw(answered with { Sequence = 2 });
        baseline.AppendRaw(stage);
        baseline.AppendRaw(stage with { Sequence = 2 });

        var actual = await candidate.ObserveWake(candidateSession);
        var expected = await baseline.ObserveWake(baselineSession);

        Assert.Equal(0, actual.QuestionReads);
        Assert.Empty(actual.Loads);
        Assert.Equal(0, actual.MetadataReads);
        Assert.Equal("", actual.Output);
        AssertSameWake(expected, actual);
    }

    private static OwnerQuestion Question() => new("owner-decision", FirstId, OwnerQuestionKind.HumanInput, "Continue work?");

    private static void AssertSameWake(WakeObservation expected, WakeObservation actual)
    {
        Assert.Equal(expected.QuestionReads, actual.QuestionReads);
        Assert.Equal(expected.MetadataReads, actual.MetadataReads);
        Assert.Equal(expected.Loads.Length, actual.Loads.Length);
        for (var index = 0; index < expected.Loads.Length; index++)
            Assert.Equal(expected.Loads[index], actual.Loads[index]);
        Assert.Equal(expected.Output, actual.Output);
    }

    private sealed record WakeObservation(int QuestionReads, string[][] Loads, int MetadataReads, string Output);

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-console-decisions-");
        internal readonly OwnerConsoleHarness Harness = new();
        internal readonly CountingState State;
        internal readonly CountingQuestions Questions = new();
        private OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(_root.FullName);
        internal string Log => Path.Combine(Workspace.LogDirectory, ChangeStreamWriter.FileName);
        private DateTimeOffset Now => Harness.Clock.GetUtcNow();

        internal Fixture()
        {
            Harness.AddGoal(FirstId, "First goal", AgentRole.Developer);
            Harness.AddGoal(SecondId, "Second goal", AgentRole.Tester);
            State = new CountingState(Harness.Kernel);
        }

        internal async Task<OwnerConsoleSession> Start()
        {
            var session = new OwnerConsoleSession(State, Questions, Harness.Answers, Harness.Liveness,
                Harness.Digest, Harness.Tail, Harness.Output, Harness.Clock, changes: new ChangeStreamFileReader(Log));
            await session.StartAsync(null, CancellationToken.None);
            State.MetadataReads = 0;
            State.Loads.Clear();
            Questions.Reads = 0;
            return session;
        }

        internal void AppendLifecycle(ProgressKind kind)
        {
            var writer = new GoalLifecycleEventWriter(Workspace.GoalLifecycleEventsDirectory,
                new FixtureClock(Harness.Clock), committed: GoalLifecycleChangeSink.Create(Workspace));
            writer.AppendTimelineEvent(new ProgressEvent(new GoalId(FirstId), new TaskId("task-1"), kind,
                "private owner input", Now));
        }

        internal ChangeStreamRecord Write(string source, string detail) =>
            Assert.IsType<ChangeStreamRecord>(new ChangeStreamWriter(Log).Append(source, FirstId, detail, Now));
        internal void AppendRaw(ChangeStreamRecord record) => File.AppendAllText(Log,
            JsonSerializer.Serialize(record, ChangeStreamRecord.JsonOptions) + Environment.NewLine);
        internal Task Wake(OwnerConsoleSession session) =>
            session.HandleEventAsync(new OwnerConductEvent(Now, "goal", FirstId, "wake"), CancellationToken.None);
        internal async Task<WakeObservation> ObserveWake(OwnerConsoleSession session)
        {
            var outputStart = Harness.Output.Text.Length;
            await Wake(session);
            return new(Questions.Reads, State.Loads.ToArray(), State.MetadataReads, Harness.Output.Text[outputStart..]);
        }

        public void Dispose() => _root.Delete(true);
    }

    private sealed class FixtureClock(TimeProvider clock) : IClock
    {
        public DateTimeOffset UtcNow => clock.GetUtcNow();
    }

    private sealed class CountingState(AgentOrchestratorKernel kernel) : IOrchestratorStateQueries
    {
        internal int MetadataReads;
        internal readonly List<string[]> Loads = [];
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            MetadataReads++;
            return Task.FromResult<IReadOnlyList<GoalSummary>>(kernel.Goals.Select(goal =>
                new GoalSummary(goal.Id.Value, goal.Status.ToString(), goal.Objective, "2026-10-08T12:00:00Z")).ToArray());
        }
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids,
            CancellationToken cancellationToken = default)
        {
            Loads.Add(ids.Select(id => id.Value).ToArray());
            return Task.FromResult(kernel); // Includes the other goal to verify session filtering.
        }
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>([]);
        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>([]);
    }

    private sealed class CountingQuestions : IOwnerQuestionSource
    {
        internal int Reads;
        internal readonly List<OwnerQuestion> Items = [];
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<OwnerQuestion>>(Items.ToArray());
        }
    }
}
