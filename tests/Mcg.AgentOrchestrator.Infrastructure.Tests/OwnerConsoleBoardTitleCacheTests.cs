using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its probes, cache, clock and temporary directory.
public sealed class OwnerConsoleBoardTitleCacheTests
{
    [Fact]
    public void Merge_ReusesUnchangedTitlesAndRetainsMissingIds()
    {
        var calls = 0;
        var cache = new OwnerConsoleGoalTitleCache(objective => { calls++; return OwnerGoalTitle.Full(objective); });
        GoalSummary[] rows = [new("abcdefgh-one", "Completed", "First", "1"),
            new("abcdefgh-two", "Cancelled", "Second", "1"), new("unique-third", "Active", "Third", "1")];
        cache.Merge(rows);
        Assert.Equal(3, calls);
        cache.Merge(rows);
        Assert.Equal(3, calls);
        rows[0] = rows[0] with { Objective = "Changed", UpdatedAt = "2" };
        cache.Merge(rows);
        Assert.Equal(4, calls);
        Assert.Equal("Changed", cache.Title("ABCDEFGH-ONE"));
        cache.Merge(rows[..2]);
        Assert.Equal(4, calls);
        Assert.Equal("Third", cache.Title("unique"));
        Assert.Equal("Third", cache.Titles["unique-third"]);
        Assert.Equal(string.Empty, cache.Title("abcdefgh"));
        Assert.Equal(string.Empty, cache.Title("unknown"));
        Assert.Equal(string.Empty, cache.Title(null));
        Assert.Equal(3, cache.Titles.Count);
    }

    [Fact]
    public async Task Board_UsesBoundedQueryAndKeepsActivityTitleAfterRowDisappears()
    {
        var harness = new OwnerConsoleHarness();
        var probe = new BoardProbe
        {
            Rows = [new("11111111-terminal", "Completed", "Retained title", "1")]
        };
        var builder = new OwnerConsoleViewModelBuilder(probe, harness.Questions, harness.Liveness, new Epics(), harness.Clock);
        await builder.BuildBoardAsync();
        probe.Rows = [];
        var board = await builder.BuildBoardAsync();
        Assert.Equal(2, probe.Queries.Count);
        Assert.All(probe.Queries, query => Assert.Equal(
            new GoalBoardRowQuery(200, 1024, BacklogIntakePlanner.TargetScopeHeadingLine), query));
        var now = harness.Clock.GetUtcNow();
        var withActivity = builder.WithActivity(board, new(now, now,
            [new(now, "goal-lifecycle", "11111111", "TaskDispatched role=Developer")], 0));
        Assert.Equal("Retained title", Assert.Single(withActivity.Activity).GoalTitle);
    }

    [Fact]
    public async Task Questions_UseUnboundedQueryWithoutObjectives()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var probe = new BoardProbe();
            var snapshot = await new OwnerQuestionReadModel(probe, root).ReadAsync(CancellationToken.None);
            Assert.Empty(snapshot.Live);
            Assert.Equal(new GoalBoardRowQuery(null, 0, null), Assert.Single(probe.Queries));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task Questions_DefaultQueryMatchesMetadataOverloadAndHydrationOrder()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            foreach (var id in new[] { "completed", "active-first", "cancelled", "active-last" })
                kernel.CreateGoal(new GoalId(id), id, [new TaskSpec(new TaskId(id + "-task"), "Work", AgentRole.Developer)]);
            var now = new OwnerConsoleTestClock().Now;
            var arrangement = kernel.ExportSnapshot() with
            {
                Goals = kernel.ExportSnapshot().Goals.Select(goal => goal with
                {
                    Status = goal.Id == "completed" ? GoalStatus.Completed : goal.Id == "cancelled" ? GoalStatus.Cancelled : GoalStatus.Active,
                    CurrentHold = goal.Id == "completed" ?
                        new GoalHoldSnapshot("terminal-hold", "steward-owner-question", "question=Review hold evidence=[receipt]", now) : null
                }).ToArray(),
                HumanInputRequests = [new("active-request", "active-first", null, "Continue?", now),
                    new("terminal-request", "cancelled", null, "Old question", now)]
            };
            var baselineProbe = new HydrationProbe(arrangement);
            var defaultProbe = new HydrationProbe(arrangement);
            var baselineModel = new OwnerQuestionReadModel(baselineProbe, root);
            var defaultModel = new OwnerQuestionReadModel(defaultProbe, root);
            var baseline = await baselineModel.ReadAsync(await baselineProbe.ListGoalMetadataAsync(), CancellationToken.None);
            var actual = await defaultModel.ReadAsync(CancellationToken.None);
            Assert.Equal(baseline.Live, actual.Live);
            Assert.Equal("active-request", Assert.Single(actual.Live).ItemId);
            Assert.Equal(baseline.Hidden.ToDictionary(item => item.Question.ItemId, item => item.Reason),
                actual.Hidden.ToDictionary(item => item.Question.ItemId, item => item.Reason));
            Assert.Contains(actual.Hidden, item => item.Question.ItemId == "terminal-hold");
            Assert.Equal(Assert.Single(baselineProbe.Loaded), Assert.Single(defaultProbe.Loaded));
            Assert.Equal(new[] { "active-first", "active-last" }, Assert.Single(defaultProbe.Loaded));
            Assert.Equal(Assert.Single(baselineProbe.HoldIds), Assert.Single(defaultProbe.HoldIds));
            Assert.Equal(new[] { "completed", "cancelled" }, Assert.Single(defaultProbe.HoldIds));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private sealed class BoardProbe : IOrchestratorStateQueries
    {
        internal IReadOnlyList<GoalSummary> Rows = [];
        internal readonly List<GoalBoardRowQuery> Queries = [];
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken token = default) =>
            throw new InvalidOperationException("The console must use the board row query.");
        public Task<IReadOnlyList<GoalSummary>> ListGoalBoardRowsAsync(GoalBoardRowQuery query, CancellationToken token = default)
        {
            Queries.Add(query);
            return Task.FromResult(Rows);
        }
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken token = default) =>
            throw new InvalidOperationException("This fixture has no non-terminal goals.");
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>([]);
        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> ids, CancellationToken token = default) => Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>([]);
    }

    // Omits the new method to exercise its default implementation, including listing order.
    private sealed class HydrationProbe(OrchestratorSnapshot snapshot) : IOrchestratorStateQueries
    {
        internal readonly List<string[]> Loaded = [];
        internal readonly List<string[]> HoldIds = [];
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<GoalSummary>>(snapshot.Goals.Select(goal =>
                new GoalSummary(goal.Id, goal.Status.ToString(), goal.Objective, "updated")).ToArray());
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken token = default)
        {
            var selected = ids.Select(id => id.Value).ToHashSet();
            Loaded.Add(ids.Select(id => id.Value).ToArray());
            return Task.FromResult(AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Where(goal => selected.Contains(goal.Id)).ToArray(),
                HumanInputRequests = snapshot.HumanInputRequests.Where(request => selected.Contains(request.GoalId)).ToArray()
            }));
        }
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>(snapshot.HumanInputRequests);
        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> ids, CancellationToken token = default)
        {
            HoldIds.Add(ids.Select(id => id.Value).ToArray());
            return Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>(snapshot.Goals.Where(goal =>
                ids.Contains(new GoalId(goal.Id)) && goal.CurrentHold is not null).Select(goal =>
                new TerminalOwnerQuestionHold(goal.Id, goal.CurrentHold!.Identity, goal.CurrentHold.State,
                    goal.CurrentHold.Blocker, goal.CurrentHold.StartedAt)).ToArray());
        }
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
