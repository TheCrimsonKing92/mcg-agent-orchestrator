using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test in-memory source, kernel and clock; no application or stores.
public sealed class OwnerEpicDetailQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingSourceIsUnavailableWhileEmptySourceIsLoaded()
    {
        Assert.Null(await new OwnerEpicDetailQuery(null, new Clock()).LoadAsync(
            OwnerConsoleEpicWindow.Day, null, CancellationToken.None));
        var model = await new OwnerEpicDetailQuery(new Source(), new Clock()).LoadAsync(
            OwnerConsoleEpicWindow.Day, null, CancellationToken.None);
        Assert.NotNull(model);
        Assert.Empty(model.Epics);
        Assert.Null(model.SelectedDetail);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(1, 168)]
    [InlineData(2, 0)]
    public async Task ListUsesUtcCutoffAndSuccessfulLocalTime(int windowValue, int hours)
    {
        var window = (OwnerConsoleEpicWindow)windowValue;
        var source = new Source();
        var clock = new Clock();
        source.OnRead = () => clock.Current = Now.AddMinutes(1);
        var model = await new OwnerEpicDetailQuery(source, clock).LoadAsync(window, null, CancellationToken.None);
        Assert.NotNull(model);
        Assert.Equal(window, model.Window);
        Assert.Equal(hours == 0 ? (DateTimeOffset?)null : Now.AddHours(-hours), model.Since);
        Assert.Equal(model.Since, source.Since);
        Assert.Equal(Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(3)), model.LoadedAt);
        Assert.Equal(TimeSpan.FromHours(3), model.LoadedAt!.Value.Offset);
        Assert.Equal(1, source.ListCalls);
    }

    [Fact]
    public async Task SelectedDetailUsesOnlyNarrowSourceAndHydratesItsGoals()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("goal", "Goal", AgentRole.Tester);
        var source = new Source();
        var epic = new PortfolioEpic("epic", "Epic", null, Now, "test", Now, "test");
        var row = Assert.Single(EpicProgressReadModel.Build([epic], [],
            [new("epic", PortfolioMemberKind.Goal, "goal", Now, "test")],
            [new("goal", "Active", "Goal", Now.ToString("O"))], [], null));
        source.Detail = new(row, null, new Dictionary<string, Goal> { ["goal"] = goal });

        var model = await new OwnerEpicDetailQuery(source, new Clock()).LoadAsync(
            OwnerConsoleEpicWindow.Day, "epic", CancellationToken.None);

        Assert.NotNull(model);
        Assert.Equal(0, source.ListCalls);
        Assert.Equal("epic", source.DetailId);
        Assert.Equal(Now.AddHours(-24), source.Since);
        Assert.Same(row, Assert.Single(model.Epics));
        var detail = Assert.IsType<OwnerConsoleEpicViewModel.Detail>(model.SelectedDetail);
        Assert.Equal("epic", detail.Epic.Id);
        Assert.Equal("Tester", Assert.Single(detail.InFlight).Stage);
    }

    [Fact]
    public async Task SourceFailurePropagatesForAdapterRetryPresentation()
    {
        var failure = new InvalidOperationException("epic read failed");
        var source = new Source { OnRead = () => throw failure };
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OwnerEpicDetailQuery(source, new Clock()).LoadAsync(
                OwnerConsoleEpicWindow.Day, null, CancellationToken.None));
        Assert.Same(failure, actual);
    }

    private sealed class Source : IOwnerConsoleEpicSource
    {
        internal int ListCalls;
        internal string? DetailId;
        internal DateTimeOffset? Since;
        internal Action? OnRead;
        internal EpicDetailReadSnapshot Detail = null!;
        public Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken token)
        {
            ListCalls++;
            Since = since;
            OnRead?.Invoke();
            return Task.FromResult<IReadOnlyList<EpicProgressRollup>>([]);
        }
        public Task<EpicDetailReadSnapshot> LoadDetailAsync(string epicId, DateTimeOffset? since, CancellationToken token)
        {
            DetailId = epicId;
            Since = since;
            return Task.FromResult(Detail);
        }
        public Task<string?> FindGoalEpicAsync(string goalId, CancellationToken token) =>
            throw new InvalidOperationException("Query must not look up goal membership.");
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Current = Now;
        public override DateTimeOffset GetUtcNow() => Current;
        public override TimeZoneInfo LocalTimeZone =>
            TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(3), "Test", "Test");
    }
}
