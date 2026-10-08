using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its state fake, stream, output and question source.
public sealed class OwnerConsoleChangeStreamSessionTests : IDisposable
{
    private const string FirstId = "11111111111111111111111111111111";
    private const string SecondId = "22222222222222222222222222222222";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-console-changes-");
    private readonly OwnerConsoleHarness _harness = new();
    private readonly CountingState _state;
    private readonly CountingQuestions _questions = new();
    private string Log => Path.Combine(_root.FullName, ChangeStreamWriter.FileName);
    private DateTimeOffset Now => _harness.Clock.GetUtcNow();

    public OwnerConsoleChangeStreamSessionTests()
    {
        _harness.AddGoal(FirstId, "First goal", AgentRole.Developer);
        _harness.AddGoal(SecondId, "Second goal", AgentRole.Tester);
        _state = new CountingState(_harness.Kernel);
    }

    [Fact]
    public async Task Event_UntypedCausesZeroReadsAndTransitionLoadsOnlyItsGoal()
    {
        var session = await Start();
        await Wake(session, "blocked-recheck-heartbeat");
        AssertNoReads();
        var outputStart = _harness.Output.Text.Length;
        Write("watch-transition", FirstId[..8]);
        await Wake(session);
        Assert.Equal(FirstId, Assert.Single(Assert.Single(_state.Loads)));
        Assert.Equal(0, _state.MetadataReads);
        Assert.Equal(0, _questions.Reads);
        Assert.Contains("11111111 | First goal | Active | Developer", _harness.Output.Text[outputStart..]);
        Assert.DoesNotContain("Second goal", _harness.Output.Text[outputStart..]);
        Assert.DoesNotContain("board |", _harness.Output.Text[outputStart..]);
    }

    [Fact]
    public async Task Event_StartupCoveredAndOlderReplayAreIgnored()
    {
        Write("goal", FirstId);
        var reader = new ChangeStreamFileReader(Log);
        Write("goal", SecondId); // Arrives between reader creation and the startup snapshot.
        var session = Session(reader);
        await session.StartAsync(null, CancellationToken.None);
        ResetCounts();
        await Wake(session);
        AssertNoReads();
        AppendRaw(Record(1)); // Replay of a delta below the startup coverage anchor.
        AppendRaw(Record(2));
        await Wake(session);
        AssertNoReads();
        Write("goal", FirstId);
        await Wake(session);
        Assert.Equal(FirstId, Assert.Single(Assert.Single(_state.Loads)));
        Assert.Equal(0, _state.MetadataReads);
        Assert.Equal(0, _questions.Reads);
    }

    [Fact]
    public async Task Event_OwnerDecisionTriggersExactlyOneQuestionRead()
    {
        var session = await Start();
        _questions.Items.Add(new OwnerQuestion("owner-decision", FirstId, OwnerQuestionKind.HumanInput, "Continue work?"));
        var outputStart = _harness.Output.Text.Length;
        Write("goal-escalation", FirstId);
        await Wake(session);
        Assert.Equal(1, _questions.Reads);
        Assert.Equal(0, _state.MetadataReads);
        Assert.Empty(_state.Loads);
        Assert.Contains("[1] 11111111 Continue work?", _harness.Output.Text[outputStart..]);
    }

    [Fact]
    public async Task Start_ChangeDuringSnapshotRemainsAvailableForIncrementalReplay()
    {
        _state.OnMetadataRead = () => { _state.OnMetadataRead = null; Write("goal", FirstId); };
        var session = await Start();
        await Wake(session);
        Assert.Equal(FirstId, Assert.Single(Assert.Single(_state.Loads)));
        Assert.Equal(0, _state.MetadataReads);
        Assert.Equal(0, _questions.Reads);
    }

    [Fact]
    public async Task Event_TerminalTransitionRemovesOnlyItsBoardRow()
    {
        var session = await Start();
        _harness.Kernel.CancelGoal(new GoalId(FirstId), "finished by operator");
        Write("goal-left-working-set", FirstId);
        var outputStart = _harness.Output.Text.Length;
        await Wake(session);
        Assert.Equal(FirstId, Assert.Single(Assert.Single(_state.Loads)));
        Assert.Equal(0, _state.MetadataReads);
        Assert.Equal(0, _questions.Reads);
        Assert.Contains("11111111 | removed from board", _harness.Output.Text[outputStart..]);
        Assert.DoesNotContain("Second goal", _harness.Output.Text[outputStart..]);
    }

    [Fact]
    public async Task Event_GapInBatchInvalidatesAllDeltasAndRefreshesExactlyOnce()
    {
        var session = await Start();
        AppendRaw(Record(1));
        AppendRaw(Record(3));
        AppendRaw(Record(4));
        await Wake(session);
        Assert.Equal(1, _state.MetadataReads);
        Assert.Equal(1, _questions.Reads);
        Assert.Equal(new[] { FirstId, SecondId }, Assert.Single(_state.Loads));
        ResetCounts();
        AppendRaw(Record(5));
        await Wake(session);
        Assert.Equal(FirstId, Assert.Single(Assert.Single(_state.Loads)));
        Assert.Equal(0, _state.MetadataReads);
        Assert.Equal(0, _questions.Reads);
    }

    [Theory]
    [InlineData(ChangeStreamFileReader.Gap)]
    [InlineData(ChangeStreamFileReader.Rotation)]
    [InlineData(ChangeStreamFileReader.UnknownSchema)]
    public async Task Event_DiscontinuityRefreshesOnceThenResumesSingleGoalLoads(string signal)
    {
        Write("goal", FirstId);
        var session = await Start();
        if (signal == ChangeStreamFileReader.Rotation)
            File.Move(Log, Path.Combine(_root.FullName, "change-stream-moved.log"));
        var sequence = signal == ChangeStreamFileReader.Gap ? 3 : 2;
        AppendRaw(Record(sequence) with { Schema = signal == ChangeStreamFileReader.UnknownSchema ? 99 : 1 });
        var outputStart = _harness.Output.Text.Length;
        await Wake(session);
        Assert.Equal(1, _questions.Reads);
        Assert.Equal(1, _state.MetadataReads);
        Assert.Equal(new[] { FirstId, SecondId }, Assert.Single(_state.Loads));
        Assert.Contains("board | active goals: 2", _harness.Output.Text[outputStart..]);

        ResetCounts();
        AppendRaw(Record(sequence + 1));
        await Wake(session);
        Assert.Equal(FirstId, Assert.Single(Assert.Single(_state.Loads)));
        Assert.Equal(0, _state.MetadataReads);
        Assert.Equal(0, _questions.Reads);
    }

    [Fact]
    public async Task Event_CoalescedWakeDrainsEveryTypedDelta()
    {
        var session = await Start();
        Write("goal", FirstId);
        Write("goal", SecondId);
        Write("goal-escalation", FirstId);
        await Wake(session, "acceptance-lease");
        Assert.Equal(new[] { FirstId, SecondId }, _state.Loads.Select(ids => Assert.Single(ids)));
        Assert.Equal(1, _questions.Reads);
        Assert.Equal(0, _state.MetadataReads);
    }

    [Fact]
    public async Task Event_UnknownPrefixRefreshesOnceAndNextDeltaUsesFreshBoard()
    {
        var session = await Start();
        _harness.AddGoal("33333333333333333333333333333333", "New goal", AgentRole.Reviewer);
        Write("goal", "33333333");
        await Wake(session);
        Assert.Equal(1, _state.MetadataReads);
        Assert.Equal(1, _questions.Reads);
        Assert.Equal(3, Assert.Single(_state.Loads).Length);
        ResetCounts();
        Write("goal", "33333333");
        await Wake(session);
        Assert.Equal("33333333333333333333333333333333", Assert.Single(Assert.Single(_state.Loads)));
        Assert.Equal(0, _state.MetadataReads);
        Assert.Equal(0, _questions.Reads);
    }

    private OwnerConsoleSession Session(ChangeStreamFileReader reader) => new(_state, _questions,
        _harness.Answers, _harness.Liveness, _harness.Digest, _harness.Tail, _harness.Output, _harness.Clock, changes: reader);
    private async Task<OwnerConsoleSession> Start()
    {
        var session = Session(new ChangeStreamFileReader(Log));
        await session.StartAsync(null, CancellationToken.None);
        ResetCounts();
        return session;
    }
    private Task Wake(OwnerConsoleSession session, string kind = "goal") =>
        session.HandleEventAsync(new OwnerConductEvent(Now, kind, FirstId, "wake"), CancellationToken.None);
    private void Write(string kind, string id) => new ChangeStreamWriter(Log).Append(kind, id, "changed", Now);
    private ChangeStreamRecord Record(long sequence) => new(1, sequence, Now,
        ChangeStreamRecord.GoalTransition, FirstId, "goal", "changed");
    private void AppendRaw(ChangeStreamRecord record) => File.AppendAllText(Log,
        JsonSerializer.Serialize(record, ChangeStreamRecord.JsonOptions) + Environment.NewLine);
    private void ResetCounts() { _state.MetadataReads = 0; _state.Loads.Clear(); _questions.Reads = 0; }
    private void AssertNoReads()
    {
        Assert.Equal(0, _state.MetadataReads);
        Assert.Empty(_state.Loads);
        Assert.Equal(0, _questions.Reads);
    }

    private sealed class CountingState(AgentOrchestratorKernel kernel) : IOrchestratorStateQueries
    {
        internal int MetadataReads;
        internal Action? OnMetadataRead;
        internal readonly List<string[]> Loads = [];
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            MetadataReads++;
            OnMetadataRead?.Invoke();
            return Task.FromResult<IReadOnlyList<GoalSummary>>(kernel.Goals.Select(goal =>
                new GoalSummary(goal.Id.Value, goal.Status.ToString(), goal.Objective, "2026-10-08T12:00:00Z")).ToArray());
        }
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids,
            CancellationToken cancellationToken = default)
        {
            Loads.Add(ids.Select(id => id.Value).ToArray());
            return Task.FromResult(kernel); // Deliberately includes other goals: assert session filtering.
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
    public void Dispose() => _root.Delete(true);
}
