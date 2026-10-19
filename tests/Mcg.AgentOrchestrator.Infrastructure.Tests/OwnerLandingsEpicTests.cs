using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = OwnerLandingsQueryTests.Fixture;

// Parallel-safe: fixed clocks, in-memory state and a unique filesystem/database root per fixture.
public sealed class OwnerLandingsEpicTests
{
    [Fact]
    public void Render_MixedEpics_LabelsCurrentEpicAndKeepsTwoLinesPerRow()
    {
        using var fixture = new Fixture();
        var assigned = fixture.Add("10000001", fixture.Clock.Now);
        fixture.Add("20000002", fixture.Clock.Now.AddMinutes(-1), GoalPromotionFact.MergeTrainProvenance);
        var epics = new RecordingEpics(id => Task.FromResult(id == assigned.Id ? "Control plane" : ""));

        var result = Load(fixture, epics);
        var lines = OwnerLandingsQuery.Render(result);

        Assert.Equal(2, result.Rows.Count);
        Assert.Contains("10000001  2026-10-16 12:00 local  ccccccccc  solo  current epic: Control plane  current title: Goal 10000001", lines);
        Assert.Contains("20000002  2026-10-16 11:59 local  ccccccccc  merge train  current title: Goal 20000002", lines);
        Assert.Equal(2 * result.Rows.Count + 1, lines.Count);
        Assert.Equal("  acceptance evidence unavailable", lines[1]);
        Assert.Equal("  acceptance evidence unavailable", lines[3]);
        Assert.Equal("Undated: 0 landed goals have no recorded landing time", lines[4]);
    }

    [Theory]
    [InlineData("  Two\r\nlines\nhere  ", "Two lines here")]
    [InlineData("one\rtwo", "one two")]
    [InlineData("one\r\n\nthree", "one  three")]
    [InlineData("  one\ttwo\u0085three\u2028four  ", "one\ttwo\u0085three\u2028four")]
    [InlineData(" \t\r\n ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Load_EpicTitle_FoldsOnlyCrLfAndTrims(string? title, string? expected)
    {
        using var fixture = new Fixture();
        fixture.Add("10000001", fixture.Clock.Now);
        var epics = new RecordingEpics(_ => Task.FromResult(title!));

        Assert.Equal(expected, Assert.Single(Load(fixture, epics).Rows).Epic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_LookupFailure_KeepsFailedRowAndContinues(bool faultedTask)
    {
        using var fixture = new Fixture();
        var first = fixture.Add("10000001", fixture.Clock.Now);
        var failed = fixture.Add("20000002", fixture.Clock.Now.AddMinutes(-1));
        var last = fixture.Add("30000003", fixture.Clock.Now.AddMinutes(-2));
        var epics = new RecordingEpics(id => id != failed.Id ? Task.FromResult("Board")
            : faultedTask ? Task.FromException<string>(new InvalidOperationException("lookup failed"))
            : throw new InvalidOperationException("lookup failed"));

        var result = Load(fixture, epics);

        Assert.Equal(new[] { first.Id, failed.Id, last.Id }, result.Rows.Select(row => row.GoalId));
        Assert.Equal(new string?[] { "Board", null, "Board" }, result.Rows.Select(row => row.Epic));
        Assert.Equal(new[] { first.Id, failed.Id, last.Id }, epics.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_LookupCancellation_Propagates(bool faultedTask)
    {
        using var fixture = new Fixture();
        fixture.Add("10000001", fixture.Clock.Now);
        var cancellation = new OperationCanceledException("lookup canceled");
        var epics = new RecordingEpics(_ => faultedTask
            ? Task.FromException<string>(cancellation) : throw cancellation);

        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(() => Load(fixture, epics)));
        Assert.Single(epics.Calls);
    }

    [Fact]
    public void Load_CanceledTask_PropagatesTaskCanceledException()
    {
        using var fixture = new Fixture();
        fixture.Add("10000001", fixture.Clock.Now);
        var epics = new RecordingEpics(_ => Task.FromCanceled<string>(new CancellationToken(true)));

        Assert.Throws<TaskCanceledException>(() => Load(fixture, epics));
        Assert.Single(epics.Calls);
    }

    [Fact]
    public void Load_WindowFilters_LooksUpOnlyListedGoalsOnce()
    {
        using var fixture = new Fixture();
        fixture.Add("30000003", fixture.Clock.Now.AddDays(-8));
        var first = fixture.Add("10000001", fixture.Clock.Now);
        fixture.Goals.Add(new GoalSummary("40000004".PadRight(32, '0'), nameof(GoalStatus.Completed),
            "Undated goal", fixture.Clock.Now.ToString("O"), PromotionFact: null));
        var second = fixture.Add("20000002", fixture.Clock.Now.AddMinutes(-1));
        fixture.Add("50000005", fixture.Clock.Now.AddMinutes(1));
        var epics = new RecordingEpics(_ => Task.FromResult("Board"));

        var result = Load(fixture, epics);

        Assert.Equal(new[] { first.Id, second.Id }, epics.Calls);
        Assert.Equal(new[] { first.Id, second.Id }, result.Rows.Select(row => row.GoalId));
        Assert.All(result.Rows, row => Assert.Equal("Board", row.Epic));
        Assert.Equal(1, result.UndatedCount);
    }

    [Fact]
    public void Load_InvalidWindow_ReadsNeitherStateNorEpics()
    {
        using var fixture = new Fixture();
        fixture.Add("10000001", fixture.Clock.Now);
        var epics = new RecordingEpics(_ => throw new InvalidOperationException("must not look up"));

        var result = Load(fixture, epics, "yesterday");

        Assert.True(result.InvalidWindow);
        Assert.Equal(new[] { "usage: landings [today|30]" }, OwnerLandingsQuery.Render(result));
        Assert.Empty(epics.Calls);
        Assert.Equal(0, fixture.Reads);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.DirectoryPath));
    }

    [Fact]
    public async Task Load_DefaultLookup_ReadsAssignedEpicFromPortfolioStore()
    {
        using var fixture = new Fixture();
        var assigned = fixture.Add("10000001", fixture.Clock.Now);
        var unassigned = fixture.Add("20000002", fixture.Clock.Now.AddMinutes(-1));
        var path = Path.Combine(fixture.DirectoryPath, "portfolio.db");
        var store = new PortfolioStore(path);
        var epic = await store.AddEpicAsync("Board");
        await store.AssignGoalToEpicAsync(assigned.Id, epic.Id);
        Assert.True(File.Exists(path));
        Assert.Equal("Board", (await store.GetGoalMembershipAsync(assigned.Id))?.EpicTitle);

        var result = fixture.Load();

        Assert.Equal("Board", Assert.Single(result.Rows, row => row.GoalId == assigned.Id).Epic);
        Assert.Null(Assert.Single(result.Rows, row => row.GoalId == unassigned.Id).Epic);
    }

    [Fact]
    public void Render_NoPortfolioOrEmptyLookup_PreservesLiteralBaseline()
    {
        using var fixture = new Fixture();
        fixture.Add("10000001", fixture.Clock.Now);
        fixture.Add("20000002", fixture.Clock.Now.AddMinutes(-1), GoalPromotionFact.MergeTrainProvenance);
        fixture.Add("30000003", fixture.Clock.Now.AddMinutes(-2), GoalPromotionFact.CohortProvenance, new string('d', 40));
        fixture.Add("40000004", fixture.Clock.Now.AddMinutes(-2), GoalPromotionFact.CohortProvenance, new string('d', 40));
        string[] expected =
        [
            "10000001  2026-10-16 12:00 local  ccccccccc  solo  current title: Goal 10000001",
            "  acceptance evidence unavailable",
            "20000002  2026-10-16 11:59 local  ccccccccc  merge train  current title: Goal 20000002",
            "  acceptance evidence unavailable",
            "30000003  2026-10-16 11:58 local  ddddddddd  cohort  current title: Goal 30000003",
            "  acceptance evidence unavailable",
            "40000004  2026-10-16 11:58 local  ddddddddd  cohort  current title: Goal 40000004",
            "  acceptance evidence unavailable",
            "Undated: 0 landed goals have no recorded landing time"
        ];
        var epics = new RecordingEpics(_ => Task.FromResult(""));
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "portfolio.db")));

        var defaults = OwnerLandingsQuery.Render(fixture.Load());
        var injected = OwnerLandingsQuery.Render(Load(fixture, epics));

        Assert.Equal(expected, defaults);
        Assert.Equal(defaults, injected);
        Assert.Equal(fixture.Goals.Select(goal => goal.Id), epics.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.DirectoryPath));
    }

    [Fact]
    public void Text_InjectedLookup_ForwardsEpicToRenderedRow()
    {
        using var fixture = new Fixture();
        var goal = fixture.Add("10000001", fixture.Clock.Now);
        var epics = new RecordingEpics(_ => Task.FromResult("Control plane"));

        var text = OwnerLandingsQuery.Text(fixture, fixture.DirectoryPath, fixture.Clock, "", epics);

        Assert.Equal("10000001  2026-10-16 12:00 local  ccccccccc  solo  current epic: Control plane  current title: Goal 10000001\n"
            + "  acceptance evidence unavailable\nUndated: 0 landed goals have no recorded landing time", text);
        Assert.Equal(new[] { goal.Id }, epics.Calls);
    }

    private static OwnerLandingsQuery.Result Load(Fixture fixture, IOwnerGoalEpicLookup epics, string window = "") =>
        OwnerLandingsQuery.Load(fixture, fixture.DirectoryPath, fixture.Clock, window, epics);

    private sealed class RecordingEpics(Func<string, Task<string>> lookup) : IOwnerGoalEpicLookup
    {
        internal List<string> Calls { get; } = [];

        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken)
        {
            Assert.Equal(CancellationToken.None, cancellationToken);
            Calls.Add(goalId);
            return lookup(goalId);
        }
    }
}
