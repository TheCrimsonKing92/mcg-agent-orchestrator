using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each fixture owns its database directory; console capture uses the existing CLI collection.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliAttentionSelectiveReadRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("attention", true)]
    [InlineData("attention", false)]
    [InlineData("attention list", true)]
    [InlineData("attention list", false)]
    [InlineData("ATTENTION LIST", true)]
    [InlineData("ATTENTION LIST", false)]
    public void BareAndList_LoadOnlyReferencedLiveGoalsAndMatchWriter(string command, bool withWait)
    {
        using var fixture = new Fixture(withWait);
        var repository = new ProbeStateRepository(fixture.Kernel) { ThrowOnOutbox = true };

        var read = Run(command.Split(' '), repository, fixture.Workspace);

        Assert.True(read.Served);
        Assert.False(read.Changed);
        Assert.Equal(0, repository.FullLoadAttempts);
        Assert.Equal(1, repository.LoadGoalsCount);
        Assert.Equal(withWait ? [Id(1), Id(2)] : new[] { Id(2) }, repository.LoadedGoalIds);
        if (withWait)
            Assert.Contains("age=0s", read.Output, StringComparison.Ordinal);
        fixture.AssertWriterEquivalent(read.Output);
    }

    [Fact]
    public void NoReferences_ServesEmptyOutputWithoutLoadingGoals()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel(new FutureClock());
            for (var index = 1; index <= 21; index++)
                CreateActive(kernel, index);
            kernel.ParkGoal(new GoalId(Id(21)), "No open rows");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var read = Run(["attention"], repository, workspace);

            Assert.True(read.Served);
            Assert.False(read.Changed);
            Assert.Equal(CliAttentionNextGoldenFixtures.EmptyAttention, read.Output);
            Assert.Equal(0, repository.LoadGoalsCount);
            Assert.Empty(repository.LoadedGoalIds);
            Assert.Equal(0, repository.FullLoadAttempts);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(CollaborationItemType.Decision)]
    [InlineData(CollaborationItemType.Clarification)]
    [InlineData(CollaborationItemType.Verify)]
    public async Task ParkedOpenItem_DeclinesUntilResolved(CollaborationItemType type)
    {
        using var fixture = new Fixture(withWait: false);
        var item = await fixture.Store!.RaiseAsync(type, Id(4), "Parked item", "Needs sweep");
        var repository = new ProbeStateRepository(fixture.Kernel);

        var declined = Run(["attention"], repository, fixture.Workspace, direct: true);

        Assert.False(declined.Served);
        Assert.Empty(declined.Output);
        Assert.Equal(0, repository.LoadGoalsCount);
        Assert.True(await fixture.Store.TryResolveByIdAsync(item.Id, "Resolved by owner"));
        var resolved = Run(["attention"], repository, fixture.Workspace, direct: true);
        Assert.True(resolved.Served);
        fixture.AssertWriterEquivalent(resolved.Output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedGoalsUnavailable_DeclinesWithoutOutput(bool disappearAll)
    {
        using var fixture = new Fixture(withWait: true);
        var repository = new ProbeStateRepository(fixture.Kernel) { DisappearSelectedGoals = disappearAll };
        if (!disappearAll)
            repository.UnavailableGoalIds.Add(Id(1));

        var read = Run(["attention"], repository, fixture.Workspace);

        Assert.False(read.Served);
        Assert.Empty(read.Output);
        Assert.Equal(1, repository.LoadGoalsCount);
        Assert.Equal(0, repository.FullLoadAttempts);
    }

    [Fact]
    public void MissingStore_DeclinesBeforeMetadataOrHydration()
    {
        using var fixture = new Fixture(withWait: true, createStore: false);
        Assert.False(File.Exists(Path.Combine(fixture.Workspace.OrchestratorDirectory, "collaboration-items.db")));
        var repository = new ProbeStateRepository(fixture.Kernel);

        var read = Run(["attention"], repository, fixture.Workspace);

        Assert.False(read.Served);
        Assert.Empty(read.Output);
        Assert.Equal(0, repository.ListGoalMetadataCount);
        Assert.Equal(0, repository.LoadGoalsCount);
        Assert.Equal(0, repository.FullLoadAttempts);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("--json")]
    public void UnchangedForms_LoadAllGoalsWhenEligible(string form)
    {
        // show still declines on any Parked metadata; only its count control leaves D Active.
        using var fixture = new Fixture(withWait: true, parkD: form != "show");
        var repository = new ProbeStateRepository(fixture.Kernel) { ThrowOnOutbox = true };

        // JSON's full-hydration contract belongs to the unsupported-projection fallback.
        // The duty-only projection used by the text fixtures has no JSON goal facts.
        var read = Run(["attention", form], repository, fixture.Workspace,
            supportsAttentionProjection: form != "--json");

        Assert.True(read.Served);
        Assert.False(read.Changed);
        Assert.Equal(30, repository.LoadedGoalIds.Count);
        Assert.Equal(1, repository.LoadGoalsCount);
        Assert.Equal(0, repository.FullLoadAttempts);
        if (form == "--json")
        {
            using var json = JsonDocument.Parse(read.Output);
            var payload = json.RootElement.GetProperty("payload");
            var wait = Assert.Single(payload.GetProperty("waits").EnumerateArray());
            Assert.Equal(Id(1), wait.GetProperty("goalId").GetString());
            Assert.Equal("Live question", wait.GetProperty("question").GetString());
            var items = payload.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Contains(items, item => item.GetProperty("subject").GetString() == "Decision 2");
            Assert.Contains(items, item => item.GetProperty("subject").GetString() == "Cross-goal decision");
        }
    }

    [Fact]
    public void ShowWithParkedGoal_StillDeclinesBeforeHydration()
    {
        using var fixture = new Fixture(withWait: true);
        var repository = new ProbeStateRepository(fixture.Kernel);

        var read = Run(["attention", "show"], repository, fixture.Workspace, direct: true);

        Assert.False(read.Served);
        Assert.Empty(read.Output);
        Assert.Equal(0, repository.LoadGoalsCount);
    }

    [Fact]
    public void TerminalOpenWait_IsFilteredAndMatchesWriter()
    {
        using var fixture = new Fixture(withWait: false);
        var snapshot = fixture.Kernel.ExportSnapshot();
        var terminalWait = new HumanInputRequestSnapshot("dead0000aaaaaaaaaaaaaaaaaaaaaaaa", Id(3), null,
            "Stale terminal question", new FutureClock().UtcNow);
        fixture.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            HumanInputRequests = [.. snapshot.HumanInputRequests, terminalWait]
        });
        Assert.Contains(fixture.Kernel.HumanInputRequests,
            wait => wait.GoalId.Value == Id(3) && !wait.IsCompleted);
        var repository = new ProbeStateRepository(fixture.Kernel);

        var read = Run(["attention"], repository, fixture.Workspace);

        Assert.True(read.Served);
        Assert.False(read.Changed);
        Assert.Equal(new[] { Id(2) }, repository.LoadedGoalIds);
        Assert.DoesNotContain("Stale terminal question", read.Output, StringComparison.Ordinal);
        fixture.AssertWriterEquivalent(read.Output);
    }

    private static (bool Served, bool Changed, string Output) Run(
        string[] args, ProbeStateRepository repository, OrchestratorWorkspace workspace, bool direct = false,
        bool supportsAttentionProjection = true)
    {
        repository.SupportsAttentionProjection = supportsAttentionProjection;
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var providers = new InMemoryModelProviderRegistry([]);
        var served = false;
        var changed = false;
        var output = CaptureConsole(() =>
        {
            served = direct
                ? CliAttentionQueryCommand.TryExecute(args, repository, workspace, providers, null,
                    ref agents, ref profiles, ref currentGoal)
                : CliReadOnlyCommandRunner.TryExecute(args, repository, workspace, providers, null,
                    ref agents, ref profiles, ref currentGoal, out changed);
        });
        return (served, changed, output);
    }

    private static string Id(int index) => $"{index:x8}aaaaaaaaaaaaaaaaaaaaaaaa";

    private static Goal CreateActive(AgentOrchestratorKernel kernel, int index)
    {
        var goal = kernel.CreateGoal(new GoalId(Id(index)), $"Goal {index}");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        Assert.Equal(GoalStatus.Active, goal.Status);
        return goal;
    }

    private sealed class FutureClock : IClock
    {
        public DateTimeOffset UtcNow => new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        public OrchestratorWorkspace Workspace { get; }
        public AgentOrchestratorKernel Kernel { get; set; }
        public CollaborationItemStore? Store { get; }

        public Fixture(bool withWait, bool parkD = true, bool createStore = true)
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Kernel = new AgentOrchestratorKernel(new FutureClock());
            for (var index = 1; index <= 30; index++)
                CreateActive(Kernel, index);
            if (withWait)
            {
                // This wait kind leaves A Active while still appearing in the human attention view.
                Kernel.RequestHumanInput(new GoalId(Id(1)), null, "Live question",
                    kind: HumanWaitKind.ProspectiveAcceptanceEvidence);
                Assert.Equal(GoalStatus.Active, Kernel.Goals.Single(goal => goal.Id.Value == Id(1)).Status);
            }
            Kernel.CancelGoal(new GoalId(Id(3)), "Stopped C");
            Kernel.SupersedeGoal(new GoalId(Id(5)), "Replaced E");
            if (parkD)
                Kernel.ParkGoal(new GoalId(Id(4)), "No open rows");
            Assert.Equal(30, Kernel.Goals.Count);
            if (!createStore)
                return;
            Store = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            foreach (var index in new[] { 2, 3, 5 })
                Store.RaiseAsync(CollaborationItemType.Decision, Id(index),
                    $"Decision {index}", "Body").GetAwaiter().GetResult();
            Store.RaiseAsync(CollaborationItemType.Decision, null, "Cross-goal decision", "Body")
                .GetAwaiter().GetResult();
        }

        public void AssertWriterEquivalent(string read)
        {
            var beforeItems = JsonSerializer.Serialize(Store!.ListAsync().GetAwaiter().GetResult());
            var beforeWaits = JsonSerializer.Serialize(Kernel.ExportSnapshot().HumanInputRequests);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var providers = new InMemoryModelProviderRegistry([]);

            var writer = CaptureConsole(() => Assert.False(CliCommandDispatcher.ExecuteCommand(
                ["attention"], Kernel, Workspace, ref agents, providers, ref profiles, ref currentGoal)));

            Assert.Equal(Encoding.UTF8.GetBytes(writer), Encoding.UTF8.GetBytes(read));
            Assert.Equal(beforeItems, JsonSerializer.Serialize(Store.ListAsync().GetAwaiter().GetResult()));
            Assert.Equal(beforeWaits, JsonSerializer.Serialize(Kernel.ExportSnapshot().HumanInputRequests));
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
