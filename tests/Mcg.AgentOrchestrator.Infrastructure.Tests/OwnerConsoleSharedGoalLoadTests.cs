using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its query probe, refresh generations and temporary directory.
public sealed class OwnerConsoleSharedGoalLoadTests
{
    private static readonly string[] Active = ["11111111", "22222222", "33333333"];
    private const string Parked = "44444444";
    private const string Completed = "55555555";
    private const string OpenRequest = "open-request";

    [Fact]
    public async Task BoardThenDecisions_LoadsEachGoalOnce_MatchesBaseline_AndStartsFreshNextRefresh()
    {
        using var fixture = new Fixture();
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(fixture.Probe);
        var builder = fixture.Builder(fixture.Probe, snapshots);
        var baseline = fixture.Builder(new Probe(fixture.Snapshot, fixture.Clock));
        var expectedBoard = await baseline.BuildBoardAsync();
        var expectedDecisions = await baseline.ReadDecisionsAsync(CancellationToken.None);

        using (snapshots.BeginRefresh())
        {
            var board = await builder.BuildBoardAsync();
            var decisions = await builder.ReadDecisionsAsync(CancellationToken.None);

            Assert.Collection(fixture.Probe.Loaded,
                ids => Assert.Equal(Active, ids), ids => Assert.Equal([Parked], ids));
            var loaded = fixture.Probe.Loaded.SelectMany(ids => ids).ToArray();
            Assert.Equal(loaded.Length, loaded.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(Completed, loaded);
            Assert.Equal(3, board.Board.Length);
            Assert.Equal(expectedBoard.Board.Select(Row), board.Board.Select(Row));
            Assert.Equal(expectedDecisions.Decisions.Select(item => item.Id), decisions.Decisions.Select(item => item.Id));
            Assert.Equal(expectedDecisions.Hidden, decisions.Hidden);
            Assert.Contains(expectedDecisions.Decisions, item => item.Id == OpenRequest);
            Assert.Contains(decisions.Decisions, item => item.Id == OpenRequest);
        }

        using (snapshots.BeginRefresh())
        {
            await builder.BuildBoardAsync();
            await builder.ReadDecisionsAsync(CancellationToken.None);
        }
        Assert.Collection(fixture.Probe.Loaded,
            ids => Assert.Equal(Active, ids), ids => Assert.Equal([Parked], ids),
            ids => Assert.Equal(Active, ids), ids => Assert.Equal([Parked], ids));
    }

    [Fact]
    public async Task WithoutHandle_EachBoardReadLoadsAgain()
    {
        using var fixture = new Fixture();
        var builder = fixture.Builder(fixture.Probe, new(fixture.Probe));
        await builder.BuildBoardAsync();
        await builder.BuildBoardAsync();
        Assert.Collection(fixture.Probe.Loaded,
            ids => Assert.Equal(Active, ids), ids => Assert.Equal(Active, ids));
    }

    [Fact]
    public async Task DisposingOlderHandle_DoesNotEndNewerGeneration()
    {
        using var fixture = new Fixture();
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(fixture.Probe);
        using var h1 = snapshots.BeginRefresh();
        await snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
        using (snapshots.BeginRefresh())
        {
            h1.Dispose();
            await snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
            await snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
        }
        Assert.Collection(fixture.Probe.Loaded,
            ids => Assert.Equal([Active[0]], ids), ids => Assert.Equal([Active[0]], ids));
        // Closing h2 does not restore h1, and repeated h1 disposal is harmless.
        h1.Dispose();
        await snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
        await snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
        Assert.Equal(4, fixture.Probe.Loaded.Count);
    }

    [Fact]
    public async Task EmptyIds_NeverQueryInsideOrOutsideGeneration()
    {
        using var fixture = new Fixture();
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(fixture.Probe);
        var outside = await snapshots.LoadAsync([], CancellationToken.None);
        using var refresh = snapshots.BeginRefresh();
        var inside = await snapshots.LoadAsync([], CancellationToken.None);
        Assert.Empty(outside.Goals);
        Assert.Empty(outside.Requests);
        Assert.Empty(inside.Goals);
        Assert.Empty(inside.Requests);
        Assert.Empty(fixture.Probe.Loaded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadsNormalizeIds_FilterResults_AndPreserveRepositoryRequestOrder(bool inRefresh)
    {
        using var fixture = new Fixture();
        var snapshot = fixture.Snapshot with
        {
            HumanInputRequests = [Request("second-goal-first", Active[1], fixture.Clock.Now),
                Request(OpenRequest, Active[0], fixture.Clock.Now), Request("unrequested", Active[2], fixture.Clock.Now)]
        };
        // Deliberately over-return to verify that a repository cannot leak unrelated data.
        fixture.Probe.Load = (_, _) => Task.FromResult(AgentOrchestratorKernel.FromSnapshot(snapshot));
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(fixture.Probe);
        using var refresh = inRefresh ? snapshots.BeginRefresh() : null;
        var result = await snapshots.LoadAsync([new(Active[1]), new(Active[0]), new(Active[1])], CancellationToken.None);
        Assert.Equal([Active[0], Active[1]], Assert.Single(fixture.Probe.Loaded));
        Assert.Equal([Active[0], Active[1]], result.Goals.Select(goal => goal.Id.Value));
        Assert.Equal(["second-goal-first", OpenRequest], result.Requests.Select(request => request.Id.Value));
    }

    [Fact]
    public async Task MissingIds_AreReserved_AndReturnedCollectionsDoNotGrow()
    {
        using var fixture = new Fixture();
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(fixture.Probe);
        using var refresh = snapshots.BeginRefresh();
        var first = await snapshots.LoadAsync([new(Active[0]), new("missing")], CancellationToken.None);
        await snapshots.LoadAsync([new("missing")], CancellationToken.None);
        await snapshots.LoadAsync([new(Active[1])], CancellationToken.None);
        Assert.Collection(fixture.Probe.Loaded,
            ids => Assert.Equal([Active[0], "missing"], ids), ids => Assert.Equal([Active[1]], ids));
        Assert.Equal([Active[0]], first.Goals.Select(goal => goal.Id.Value));
        Assert.Equal([OpenRequest], first.Requests.Select(request => request.Id.Value));
    }

    [Fact]
    public async Task ConcurrentOverlap_JoinsReservedIds_AndKeepsRequestReservationOrder()
    {
        using var fixture = new Fixture();
        var firstBatch = NewBatch();
        var secondBatch = NewBatch();
        fixture.Probe.Load = (ids, _) => ids.Any(id => id.Value == Active[0]) ? firstBatch.Task : secondBatch.Task;
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(fixture.Probe);
        using var refresh = snapshots.BeginRefresh();
        var first = snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
        var joined = snapshots.LoadAsync([new(Active[1]), new(Active[0])], CancellationToken.None);
        Assert.Collection(fixture.Probe.Loaded,
            ids => Assert.Equal([Active[0]], ids), ids => Assert.Equal([Active[1]], ids));

        secondBatch.SetResult(fixture.Probe.Kernel([new(Active[1])],
            [Request("second-batch", Active[1], fixture.Clock.Now)]));
        firstBatch.SetResult(fixture.Probe.Kernel([new(Active[0])]));
        var result = await AwaitLoad(joined);
        Assert.Equal([Active[0]], (await AwaitLoad(first)).Goals.Select(goal => goal.Id.Value));
        Assert.Equal([Active[0], Active[1]], result.Goals.Select(goal => goal.Id.Value));
        Assert.Equal([OpenRequest, "second-batch"], result.Requests.Select(request => request.Id.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightResults_StayWithCapturedGeneration(bool disposeBeforeCompletion)
    {
        using var fixture = new Fixture();
        var pending = NewBatch();
        fixture.Probe.Load = (_, _) => pending.Task;
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(fixture.Probe);
        using var older = snapshots.BeginRefresh();
        var oldLoad = snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
        using var newer = disposeBeforeCompletion ? null : snapshots.BeginRefresh();
        older.Dispose();
        fixture.Probe.Load = null;
        var current = await snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
        // Different object content distinguishes a late old result from the current load.
        pending.SetResult(AgentOrchestratorKernel.FromSnapshot(fixture.Snapshot with
        {
            Goals = [fixture.Snapshot.Goals[0] with { Objective = "old generation" }], HumanInputRequests = []
        }));
        Assert.Equal("old generation", Assert.Single((await AwaitLoad(oldLoad)).Goals).Objective);
        Assert.Equal(Active[0], Assert.Single(current.Goals).Objective);
        var again = await snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
        Assert.Equal(Active[0], Assert.Single(again.Goals).Objective);
        Assert.Equal(disposeBeforeCompletion ? 3 : 2, fixture.Probe.Loaded.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledBatch_IsNotRetriedUntilGenerationEnds(bool cancelled)
    {
        using var fixture = new Fixture();
        var pending = NewBatch();
        fixture.Probe.Load = (_, _) => pending.Task;
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(fixture.Probe);
        using (snapshots.BeginRefresh())
        {
            var first = snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
            var joined = snapshots.LoadAsync([new(Active[0])], CancellationToken.None);
            if (cancelled) pending.SetCanceled(new CancellationToken(true));
            else pending.SetException(new IOException("probe load failed"));
            await AssertLoadFailure(first, cancelled);
            await AssertLoadFailure(joined, cancelled);
            await AssertLoadFailure(snapshots.LoadAsync([new(Active[0])], CancellationToken.None), cancelled);
            Assert.Single(fixture.Probe.Loaded);
        }
        fixture.Probe.Load = null;
        using var next = snapshots.BeginRefresh();
        Assert.Single((await snapshots.LoadAsync([new(Active[0])], CancellationToken.None)).Goals);
        Assert.Equal(2, fixture.Probe.Loaded.Count);
    }

    [Fact]
    public async Task JoiningCaller_CancelsOnlyItsWait_WhileInitiatorTokenReachesRepository()
    {
        using var fixture = new Fixture();
        var pending = NewBatch();
        using var initiating = new CancellationTokenSource();
        using var joining = new CancellationTokenSource();
        CancellationToken repositoryToken = default;
        fixture.Probe.Load = (_, token) => { repositoryToken = token; return pending.Task; };
        var snapshots = new OwnerConsoleRefreshGoalSnapshots(fixture.Probe);
        using var refresh = snapshots.BeginRefresh();
        var first = snapshots.LoadAsync([new(Active[0])], initiating.Token);
        var joined = snapshots.LoadAsync([new(Active[0])], joining.Token);
        joining.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitLoad(joined));
        Assert.Equal(initiating.Token, repositoryToken);
        Assert.False(repositoryToken.IsCancellationRequested);
        pending.SetResult(fixture.Probe.Kernel([new(Active[0])]));
        Assert.Single((await AwaitLoad(first)).Goals);
        Assert.Single((await snapshots.LoadAsync([new(Active[0])], CancellationToken.None)).Goals);
        Assert.Single(fixture.Probe.Loaded);
    }

    private static (string, string, string, string, bool) Row(OwnerConsoleBoardRow row) =>
        (row.GoalId, row.Title, row.State, row.Stage, row.Dimmed);

    private static TaskCompletionSource<AgentOrchestratorKernel> NewBatch() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<OwnerConsoleLoadedGoals> AwaitLoad(Task<OwnerConsoleLoadedGoals> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException ex) { throw new TimeoutException("Hang guard: reserved goal batch did not complete", ex); }
    }

    private static async Task AssertLoadFailure(Task<OwnerConsoleLoadedGoals> load, bool cancelled)
    {
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AwaitLoad(load));
        else Assert.Equal("probe load failed", (await Assert.ThrowsAsync<IOException>(() => AwaitLoad(load))).Message);
    }

    private static HumanInputRequestSnapshot Request(string id, string goal, DateTimeOffset at) =>
        new(id, goal, null, "Continue " + id + "?", at);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        internal readonly OwnerConsoleTestClock Clock = new();
        internal readonly OrchestratorSnapshot Snapshot;
        internal readonly Probe Probe;

        internal Fixture()
        {
            Snapshot = new([.. Active.Select(id => Goal(id, GoalStatus.Active)),
                Goal(Parked, GoalStatus.Parked), Goal(Completed, GoalStatus.Completed)],
                [Request(OpenRequest, Active[0], Clock.Now)]);
            Probe = new(Snapshot, Clock);
        }

        internal OwnerConsoleViewModelBuilder Builder(Probe probe, OwnerConsoleRefreshGoalSnapshots? snapshots = null) =>
            new(probe, new SerializedOwnerQuestionSource(new OwnerQuestionReadModel(probe, _root, snapshots)),
                new OwnerConsoleHarness.FakeLiveness(), new EmptyEpics(), Clock, snapshots: snapshots);

        private static GoalSnapshot Goal(string id, GoalStatus status)
        {
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId(id), id, [new TaskSpec(new TaskId(id + "-task"), "Work", AgentRole.Developer)]);
            return kernel.ExportSnapshot().Goals.Single() with { Status = status };
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }

    private sealed class EmptyEpics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("");
    }

    private sealed class Probe(OrchestratorSnapshot snapshot, OwnerConsoleTestClock clock) : IOrchestratorStateQueries
    {
        internal readonly List<string[]> Loaded = [];
        internal Func<GoalId[], CancellationToken, Task<AgentOrchestratorKernel>>? Load;

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalSummary>>(snapshot.Goals.Select(goal => new GoalSummary(
                goal.Id, goal.Status.ToString(), goal.Objective,
                (goal.Id == Parked ? clock.Now.AddDays(-3) : clock.Now).ToString("O"))).ToArray());

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids,
            CancellationToken cancellationToken = default)
        {
            var query = ids.ToArray();
            Loaded.Add(query.Select(id => id.Value).ToArray());
            return Load?.Invoke(query, cancellationToken) ?? Task.FromResult(Kernel(query));
        }

        internal AgentOrchestratorKernel Kernel(GoalId[] ids, HumanInputRequestSnapshot[]? requests = null)
        {
            var selected = ids.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            return AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Where(goal => selected.Contains(goal.Id)).ToArray(),
                HumanInputRequests = (requests ?? snapshot.HumanInputRequests)
                    .Where(request => selected.Contains(request.GoalId)).ToArray()
            });
        }

        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>(
                snapshot.HumanInputRequests.Where(request => !request.IsCompleted).ToArray());

        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> ids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>([]);
    }
}
