using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its stores and console capture; ordering uses status groups, never elapsed time.
public sealed class CliCommandTestsEpicProgressViews : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();

    [Theory]
    [InlineData("epic-list")]
    [InlineData("epic-show", "Board")]
    [InlineData("goals", "--epic", "Board")]
    [InlineData("goals", "--epic=Board")]
    public void ProgressViews_SkipKernelState(params string[] args) =>
        Assert.True(CliPersistentStateRunner.SkipsKernelState(args));

    [Fact]
    public void List_PreservesExistingFieldOrderAndAppendsCompleteProgressCounts()
    {
        var workspace = Seed();
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.ResolveEpicAsync("Board").GetAwaiter().GetResult()!;
        var before = Metadata(workspace);
        var newest = before.Where(goal => goal.Objective != "Unrelated title")
            .Select(goal => DateTimeOffset.Parse(goal.UpdatedAt!)).Max();
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());

        var output = Execute(["epic-list"], workspace, probe);

        Assert.Equal($"Board ({epic.Id[..8]}) project=unassigned goals=12 backlog=4 active=3 verified=1 parked=1 landed=1 newest={newest:O}"
            + $" verifying=1 failed=2 closed=2 missing=1 backlog-open=2 backlog-done=2{Environment.NewLine}", output);
        AssertUnchanged(workspace, before, probe);
        Assert.Equal(16, store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult().Count);
    }

    [Fact]
    public void Show_PrintsEachMemberStatusUpdateAndFirstLineTitleWithInFlightFirst()
    {
        var workspace = Seed();
        var before = Metadata(workspace);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());

        var output = Execute(["epic-show", "Board"], workspace, probe);

        Assert.Contains($"Epic: Board{Environment.NewLine}", output);
        Assert.Contains($"Description:{Environment.NewLine}  Progress scope{Environment.NewLine}", output);
        var lines = output.Split(Environment.NewLine).Where(line => line.StartsWith("  - ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(12, lines.Length);
        foreach (var goal in before.Where(goal => goal.Objective != "Unrelated title"))
        {
            var line = Assert.Single(lines.Where(line => line.StartsWith($"  - {goal.Id[..8]} ", StringComparison.Ordinal)));
            var title = goal.Status == "Draft" ? new string('x', 100) : $"{goal.Status} title";
            Assert.Equal($"  - {goal.Id[..8]} {goal.Status} updated={DateTimeOffset.Parse(goal.UpdatedAt!):O} {title}", line);
        }
        Assert.Contains("  - ffffffff Missing updated=none ", lines);
        Assert.DoesNotContain("Second line", output);
        Assert.DoesNotContain(new string('x', 101), output);
        var parked = Array.FindIndex(lines, line => line.Contains(" Parked updated=", StringComparison.Ordinal));
        var inFlight = new[] { "Draft", "Active", "WaitingForHuman", "Verifying", "Verified", "Failed", "AcceptanceFailed" };
        Assert.Equal(inFlight.Length, parked);
        foreach (var status in inFlight)
            Assert.True(Array.FindIndex(lines, line => line.Contains($" {status} updated=", StringComparison.Ordinal)) < parked);
        foreach (var status in new[] { "Completed", "Cancelled", "Superseded", "Missing" })
            Assert.True(Array.FindIndex(lines, line => line.Contains($" {status} updated=", StringComparison.Ordinal)) > parked);
        Assert.Single(output.Split(Environment.NewLine).Where(line => line.Contains(" goals=", StringComparison.Ordinal)));
        AssertUnchanged(workspace, before, probe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Goals_EpicTitleAndInlineValue_ListOnlyMemberMetadataInExistingFormat(bool inline)
    {
        var workspace = Seed();
        var before = Metadata(workspace);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());

        var output = Execute(inline ? ["goals", "--epic=Board"] : ["goals", "--epic", "Board"], workspace, probe);

        var members = before.Where(goal => goal.Objective != "Unrelated title").ToArray();
        Assert.Equal(CaptureConsole(() => ConsoleViews.PrintGoals(members)), output);
        Assert.Equal(11, output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("eeeeeeee", output);
        Assert.DoesNotContain("ffffffff", output);
        AssertUnchanged(workspace, before, probe);
    }

    [Fact]
    public void Goals_UnknownAndAmbiguousEpic_PreserveResolutionErrorsWithoutHydration()
    {
        var workspace = Seed();
        var before = Metadata(workspace);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
        var error = Assert.Throws<InvalidOperationException>(() => Execute(["goals", "--epic", "unknown"], workspace, probe));
        Assert.Equal("Epic 'unknown' was not found.", error.Message);
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        store.AddEpicAsync("Duplicate").GetAwaiter().GetResult();
        store.AddEpicAsync("Duplicate").GetAwaiter().GetResult();
        var expected = Assert.Throws<InvalidOperationException>(() => store.ResolveEpicAsync("Duplicate").GetAwaiter().GetResult());
        error = Assert.Throws<InvalidOperationException>(() => Execute(["goals", "--epic", "Duplicate"], workspace, probe));
        Assert.Equal(expected.Message, error.Message);
        AssertUnchanged(workspace, before, probe);
    }

    [Theory]
    [InlineData("goals", "--epic")]
    [InlineData("goals", "--epic=")]
    public void Goals_MissingEpicValue_ReportsUsage(params string[] args)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
        var error = Assert.Throws<ArgumentException>(() => Execute(args, workspace, probe));
        Assert.Equal("Usage: goals [--epic <id-or-title>]", error.Message);
        AssertNoKernelAccess(probe);
        Assert.False(File.Exists(workspace.SqliteStatePath));
        Assert.False(File.Exists(workspace.PortfolioStorePath));
    }

    [Fact]
    public void Views_AbsentGoalAndBacklogStores_AccountForMissingMembersWithoutCreatingStores()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.AddEpicAsync("Board").GetAwaiter().GetResult();
        store.AssignGoalToEpicAsync("ffffffff111111111111111111111111", epic.Id).GetAwaiter().GetResult();
        store.AssignBacklogItemToEpicAsync("absent", epic.Id).GetAwaiter().GetResult();
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());

        Assert.Contains("missing=1 backlog-open=1 backlog-done=0", Execute(["epic-list"], workspace, probe));
        Assert.Contains("  - ffffffff Missing updated=none ", Execute(["epic-show", "Board"], workspace, probe));
        Assert.Equal(string.Empty, Execute(["goals", "--epic", epic.Id[..8]], workspace, probe));
        AssertNoKernelAccess(probe);
        Assert.False(File.Exists(workspace.SqliteStatePath));
        Assert.False(File.Exists(workspace.BacklogStorePath));
    }

    [Fact]
    public void GoalsHelp_AdvertisesEpicFilterWithoutExecuting()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
        Assert.Contains("Usage: goals [--epic <id-or-title>]", Execute(["goals", "--help"], workspace, probe));
        AssertNoKernelAccess(probe);
    }

    private OrchestratorWorkspace Seed()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var statuses = Enum.GetValues<GoalStatus>();
        for (var index = 0; index < statuses.Length; index++)
            kernel.CreateGoal(new GoalId((index + 1).ToString("x8") + new string('1', 24)),
                statuses[index] == GoalStatus.Draft ? new string('x', 120) + "\nSecond line" : $"{statuses[index]} title\nSecond line");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select((goal, index) => goal with { Status = statuses[index] }).ToArray()
        });
        kernel.CreateGoal(new GoalId("eeeeeeee111111111111111111111111"), "Unrelated title");
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.AddEpicAsync("Board", description: "Progress scope").GetAwaiter().GetResult();
        foreach (var goal in snapshot.Goals)
            store.AssignGoalToEpicAsync(goal.Id, epic.Id).GetAwaiter().GetResult();
        store.AssignGoalToEpicAsync("ffffffff111111111111111111111111", epic.Id).GetAwaiter().GetResult();
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var open = backlog.AddAsync("Open member").GetAwaiter().GetResult();
        var done = backlog.AddAsync("Done member").GetAwaiter().GetResult();
        backlog.CloseAsync(done.Id).GetAwaiter().GetResult();
        var superseded = backlog.AddAsync("Superseded member").GetAwaiter().GetResult();
        backlog.SupersedeAsync(superseded.Id, open.Id).GetAwaiter().GetResult();
        foreach (var id in new[] { open.Id, done.Id, superseded.Id, "absent" })
            store.AssignBacklogItemToEpicAsync(id, epic.Id).GetAwaiter().GetResult();
        return workspace;
    }

    private static IReadOnlyList<GoalSummary> Metadata(OrchestratorWorkspace workspace) =>
        SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath).ListGoalMetadataAsync().GetAwaiter().GetResult();

    private static string Execute(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, probe, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
    }

    private static void AssertUnchanged(OrchestratorWorkspace workspace, IReadOnlyList<GoalSummary> before, ProbeStateRepository probe)
    {
        AssertNoKernelAccess(probe);
        Assert.Equal(before, Metadata(workspace));
    }

    private static void AssertNoKernelAccess(ProbeStateRepository probe)
    {
        Assert.Equal(0, probe.FullLoadAttempts);
        Assert.Equal(0, probe.LoadGoalsCount);
        Assert.Equal(0, probe.MutationAttempts);
        Assert.Equal(0, probe.SaveAttempts);
        Assert.Equal(0, probe.MergeSaveAttempts);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
