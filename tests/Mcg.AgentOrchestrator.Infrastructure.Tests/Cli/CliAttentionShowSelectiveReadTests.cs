using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each fixture owns its database directory; console capture uses the existing CLI collection.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionShowSelectiveReadTests : CliTaskQueryTestSupport
{
    private const string A = "b1000000aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "b2000000aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string C = "c3000000aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string D = "c2000000aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string E = "c1000000aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string P = "b3000000aaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void UnscopedShowLoadsOnlyNamedAndObligationGoals()
    {
        using var fixture = new Fixture();
        var repository = new CountingQueries(fixture.Kernel);

        var read = Run(["attention", "show"], repository, fixture.Workspace);

        Assert.True(read.Served);
        Assert.Null(Assert.Single(repository.QueryFilters));
        AssertIdSet([A, B, C, D, E], Assert.Single(repository.LoadCalls));
        Assert.Equal(0, repository.Probe.FullLoadAttempts);
        Assert.Contains("age=0s", read.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void UnscopedShowMatchesWriterBytesInStoreOrder()
    {
        using var fixture = new Fixture();
        var repository = new CountingQueries(fixture.Kernel);
        string[] args = ["attention", "show"];

        var read = Run(args, repository, fixture.Workspace);

        Assert.True(read.Served);
        fixture.AssertWriterEquivalent(args, read.Output);
        var evidenceLines = read.Output.Split('\n')
            .Where(line => line.Contains("Gate evidence:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, evidenceLines.Length);
        Assert.Contains($"goal={C}", evidenceLines[0], StringComparison.Ordinal);
        Assert.Contains($"goal={C}", evidenceLines[1], StringComparison.Ordinal);
        Assert.Contains($"goal={D}", evidenceLines[2], StringComparison.Ordinal);
        Assert.Equal(new[] { E, D, C }, repository.ReturnedGoalIds
            .Where(id => id.StartsWith('c')).ToArray());
    }

    [Theory]
    [InlineData("attention show c3000000")]
    [InlineData("attention show --goal c3000000")]
    [InlineData("ATTENTION SHOW C3000000")]
    public void ScopedShowLoadsOnlyTheNamedGoal(string command)
    {
        using var fixture = new Fixture();
        var repository = new CountingQueries(fixture.Kernel);
        var args = command.Split(' ');

        var read = Run(args, repository, fixture.Workspace);

        Assert.True(read.Served);
        Assert.Equal(new[] { new GoalId(C) }, Assert.Single(repository.QueryFilters));
        Assert.Equal(new[] { new GoalId(C) }, Assert.Single(repository.LoadCalls));
        fixture.AssertWriterEquivalent(args, read.Output);
    }

    [Fact]
    public void ScopedUnknownAndAmbiguousPrefixMatchWriterErrors()
    {
        using var fixture = new Fixture();
        foreach (var prefix in new[] { "zzzz", "c" })
        {
            var repository = new CountingQueries(fixture.Kernel);
            string[] args = ["attention", "show", prefix];
            var expected = Record.Exception(() => fixture.WriterOutput(args));

            var actual = Record.Exception(() => Run(args, repository, fixture.Workspace));

            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.Message, actual.Message);
            if (prefix == "zzzz")
            {
                Assert.IsType<KeyNotFoundException>(actual);
                Assert.Empty(repository.LoadCalls);
                Assert.Empty(Assert.Single(repository.QueryFilters)!);
            }
            else
            {
                Assert.IsType<InvalidOperationException>(actual);
                Assert.Equal("Goal prefix 'c' is ambiguous (3 matches).", actual.Message);
                AssertIdSet([C, D, E], Assert.Single(repository.LoadCalls));
                AssertIdSet([C, D, E], Assert.Single(repository.QueryFilters)!);
            }
        }
    }

    [Theory]
    [InlineData("attention show")]
    [InlineData("attention show c3000000")]
    public async Task ParkedGoalWithOpenRowDeclinesBeforeLoading(string command)
    {
        using var fixture = new Fixture();
        var item = await fixture.Store.RaiseAsync(CollaborationItemType.Decision, P, "Parked decision", "Needs sweep",
            cancellationToken: TestContext.Current.CancellationToken);
        var repository = new CountingQueries(fixture.Kernel);

        var read = Run(command.Split(' '), repository, fixture.Workspace);

        Assert.False(read.Served);
        Assert.Empty(read.Output);
        Assert.Empty(repository.QueryFilters);
        Assert.Empty(repository.LoadCalls);

        Assert.True(await fixture.Store.TryResolveByIdAsync(item.Id, "Resolved by owner",
            TestContext.Current.CancellationToken));
        var resolved = Run(command.Split(' '), repository, fixture.Workspace);
        Assert.True(resolved.Served);
        Assert.Single(repository.QueryFilters);
        Assert.Single(repository.LoadCalls);
    }

    [Fact]
    public void ArchivedObligationGoalDeclines()
    {
        using var fixture = new Fixture();
        var repository = new CountingQueries(fixture.Kernel);
        repository.ArchivedIds.Add(C);

        var read = Run(["attention", "show", "c3000000"], repository, fixture.Workspace);

        Assert.False(read.Served);
        Assert.Empty(read.Output);
        Assert.Equal(new[] { new GoalId(C) }, Assert.Single(repository.QueryFilters));
        Assert.Equal(new[] { new GoalId(C) }, Assert.Single(repository.LoadCalls));
    }

    private static void AssertIdSet(string[] expected, IReadOnlyList<GoalId> actual) =>
        Assert.Equal(expected.OrderBy(id => id, StringComparer.Ordinal),
            actual.Select(id => id.Value).OrderBy(id => id, StringComparer.Ordinal));

    private static (bool Served, string Output) Run(
        string[] args, IOrchestratorStateQueries repository, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var served = false;
        var output = CaptureConsole(() => served = CliAttentionQueryCommand.TryExecute(args, repository,
            workspace, new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal));
        return (served, output);
    }

    private sealed class CountingQueries(AgentOrchestratorKernel kernel) : IOrchestratorStateQueries
    {
        public ProbeStateRepository Probe { get; } = new(kernel) { ThrowOnOutbox = true };
        public List<GoalId[]> LoadCalls { get; } = [];
        public List<GoalId[]?> QueryFilters { get; } = [];
        public List<string> ReturnedGoalIds { get; } = [];
        public HashSet<string> ArchivedIds { get; } = new(StringComparer.Ordinal);

        public Task<AttentionReadProjection?> ReadAttentionProjectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AttentionReadProjection?>(new([], [], [], [], new(0, 0, 0, 0, 0),
                kernel.Goals.SelectMany(goal => goal.OutstandingPostLandingDuties
                    .Select(duty => new AttentionPostLandingDuty(goal.Id.Value, duty))).ToArray()));

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(
            CancellationToken cancellationToken = default) => Probe.ListGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) => Probe.ListOpenHumanInputRequestsAsync(cancellationToken);

        public Task<IReadOnlyList<GoalId>> ListGateTestObligationGoalIdsAsync(
            IReadOnlyCollection<GoalId>? goalIds = null, CancellationToken cancellationToken = default)
        {
            QueryFilters.Add(goalIds?.ToArray());
            return Task.FromResult<IReadOnlyList<GoalId>>(kernel.Goals
                .Where(goal => goal.GateTestObligations.Count > 0 && !ArchivedIds.Contains(goal.Id.Value) &&
                    (goalIds is null || goalIds.Contains(goal.Id)))
                .Select(goal => goal.Id).ToArray());
        }

        public async Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
        {
            LoadCalls.Add(goalIds.ToArray());
            var loaded = await Probe.LoadGoalsAsync(goalIds, cancellationToken);
            var snapshot = loaded.ExportSnapshot();
            var sorted = snapshot.Goals.OrderBy(goal => goal.Id, StringComparer.Ordinal).ToArray();
            ReturnedGoalIds.AddRange(sorted.Select(goal => goal.Id));
            return AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = sorted });
        }
    }

    private sealed class FutureClock : IClock
    {
        public DateTimeOffset UtcNow => new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        public OrchestratorWorkspace Workspace { get; }
        public AgentOrchestratorKernel Kernel { get; }
        public CollaborationItemStore Store { get; }

        public Fixture()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Kernel = new AgentOrchestratorKernel(new FutureClock());
            foreach (var id in new[] { A, B, C, D, E, P })
                CreateActive(id);
            for (var index = 0; index < 30; index++)
                CreateActive($"a{index:x7}aaaaaaaaaaaaaaaaaaaaaaaa");
            Kernel.RequestHumanInput(new GoalId(A), null, "Live question",
                kind: HumanWaitKind.ProspectiveAcceptanceEvidence);
            Kernel.ParkGoal(new GoalId(P), "No open rows");
            SeedObligations(C, GoalStatus.Completed, 2);
            SeedObligations(D, GoalStatus.Active, 1);
            SeedObligations(E, GoalStatus.Completed, 1, briefOffset: -1);
            Assert.Equal(36, Kernel.Goals.Count);
            Assert.Equal(GoalStatus.Active, Kernel.GetGoal(new GoalId(A)).Status);
            Assert.Single(Kernel.HumanInputRequests);
            Store = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            Store.RaiseAsync(CollaborationItemType.Decision, B, "Decision B", "Body").GetAwaiter().GetResult();
            Store.RaiseAsync(CollaborationItemType.Decision, null, "Cross-goal decision", "Body")
                .GetAwaiter().GetResult();
        }

        private void CreateActive(string id)
        {
            var goal = Kernel.CreateGoal(new GoalId(id), $"Goal {id[..8]}");
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            Assert.Equal(GoalStatus.Active, goal.Status);
        }

        private void SeedObligations(string id, GoalStatus status, int count, int briefOffset = 0)
        {
            var snapshot = Kernel.ExportGoalSnapshot(new GoalId(id));
            var version = Kernel.GetGoal(new GoalId(id)).AuthoritativeBrief.Version + briefOffset;
            Kernel.ReplaceGoalWithSnapshot(snapshot with
            {
                Status = status,
                GateTestObligations = Enumerable.Range(0, count).Select(index => new GateTestObligation(
                    $"{id[..8]}-{index}", version, ["RequiredTests"], null,
                    "Acceptance evidence", DateTimeOffset.UnixEpoch)).ToArray()
            });
        }

        public string WriterOutput(string[] args)
        {
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            return CaptureConsole(() => Assert.False(CliCommandDispatcher.ExecuteCommand(args, Kernel,
                Workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal)));
        }

        public void AssertWriterEquivalent(string[] args, string read) =>
            Assert.Equal(Encoding.UTF8.GetBytes(WriterOutput(args)), Encoding.UTF8.GetBytes(read));

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
