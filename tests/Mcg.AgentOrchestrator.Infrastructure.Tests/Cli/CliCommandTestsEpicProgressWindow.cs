using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private temporary stores and console capture; cutoff assertions use fixed timestamps.
public sealed class CliCommandTestsEpicProgressWindow : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("24h", -24)]
    [InlineData("7d", -168)]
    [InlineData("2H", -2)]
    [InlineData("1D", -24)]
    public void SinceParser_Durations_UseTheSuppliedClock(string value, int hours)
    {
        Assert.True(CliSinceArgument.TryParse(value, Cutoff, out var parsed));
        Assert.Equal(Cutoff.AddHours(hours), parsed);
    }

    [Theory]
    [InlineData("2026-10-01T12:00:00Z")]
    [InlineData("2026-10-01T14:00:00+02:00")]
    [InlineData("2026-10-01T12:00:00")]
    public void SinceParser_Timestamps_NormalizeToUtc(string value)
    {
        Assert.True(CliSinceArgument.TryParse(value, Cutoff.AddYears(1), out var parsed));
        Assert.Equal(Cutoff, parsed);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(Cutoff, CliSinceArgument.ParseOptional(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("0h")]
    [InlineData("0d")]
    [InlineData("-1h")]
    [InlineData("1.5h")]
    [InlineData("99999999d")]
    [InlineData("2147483647h")]
    public void SinceParser_InvalidAndOverflowValues_ReturnFalse(string? value) =>
        Assert.False(CliSinceArgument.TryParse(value, Cutoff, out _));

    [Fact]
    public void Build_WindowCounts_IncludeBoundariesAndOnlyMemberGoals()
    {
        var epic = new PortfolioEpic("epic", "Board", null, Cutoff, "test", Cutoff, "test");
        GoalSummary Goal(string id, string status, int created, int updated) =>
            new(id, status, id, Cutoff.AddSeconds(updated).ToString("O"), CreatedAt: Cutoff.AddSeconds(created));
        var goals = new GoalSummary[]
        {
            Goal("active-before", "Active", -1, -1),
            Goal("active-at", "Active", 0, 0),
            Goal("active-after", "Active", 1, 1),
            Goal("failed-before", "Failed", -1, -1),
            Goal("failed-at", "Failed", -1, 0),
            Goal("failed-after", "AcceptanceFailed", -1, 1),
            Goal("landed-before", "Completed", -1, -1),
            Goal("landed-at", "Completed", 0, 0),
            Goal("landed-after", "Completed", 1, 1),
            new("closed", "Cancelled", "Closed", Cutoff.AddSeconds(1).ToString("O")),
            new("invalid", "Verifying", "Invalid", "invalid"),
            Goal("nonmember", "Failed", 1, 1)
        };
        var members = goals.Where(goal => goal.Id != "nonmember")
            .Select(goal => new PortfolioEpicMember(epic.Id, PortfolioMemberKind.Goal, goal.Id, Cutoff, "test"))
            .Append(new(epic.Id, PortfolioMemberKind.Goal, "missing", Cutoff, "test"))
            .Append(new(epic.Id, PortfolioMemberKind.BacklogItem, "backlog", Cutoff, "test")).ToArray();
        var backlog = new BacklogItem("backlog", "Backlog", "Body", BacklogItemStatus.Done, Cutoff, Cutoff, null);

        var row = Assert.Single(EpicProgressReadModel.Build([epic], [], members, goals, [backlog], Cutoff));

        Assert.Equal(4, row.WindowCreatedCount);
        Assert.Equal(7, row.WindowTransitionedCount);
        Assert.Equal(2, row.WindowFailedCount);
        Assert.Equal(2, row.WindowLandedCount);
        Assert.Equal(Cutoff, row.MemberGoals.Single(goal => goal.Id == "landed-at").CreatedAt);
        Assert.Null(row.MemberGoals.Single(goal => goal.Id == "missing").CreatedAt);
        var allTime = Assert.Single(EpicProgressReadModel.Build([epic], [], members, goals, [backlog]));
        Assert.Null(allTime.WindowCreatedCount);
        Assert.Null(allTime.WindowTransitionedCount);
        Assert.Null(allTime.WindowFailedCount);
        Assert.Null(allTime.WindowLandedCount);
    }

    [Fact]
    public void List_Window_ReadsTerminalCreationAndAppendsFieldsToEveryEpic()
    {
        var workspace = Seed();
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        store.AddEpicAsync("Empty").GetAwaiter().GetResult();
        var terminal = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            .ListGoalMetadataAsync().GetAwaiter().GetResult().Single(goal => goal.Status == "Completed");
        Assert.Null(terminal.CreatedAt); // Proves the default metadata path would miss a terminal creation.
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
        var baseline = Execute(["epic-list"], workspace, probe).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        var output = Execute(["epic-list", "--since", "2000-01-01T00:00:00Z"], workspace, probe);

        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Window since 2000-01-01T00:00:00.0000000Z (UTC)", lines[0]);
        Assert.Equal(baseline[0] + " window-created=11 window-transitioned=11 window-failed=2 window-landed=1", lines[1]);
        Assert.Equal(baseline[1], lines[2]);
        Assert.Equal(baseline[2] + " window-created=0 window-transitioned=0 window-failed=0 window-landed=0", lines[3]);
        Assert.Equal(baseline[3], lines[4]);
        Assert.Equal(baseline.Length + 1, lines.Length);
        Assert.DoesNotContain("window-", Execute(["epic-show", "Board"], workspace, probe));
        AssertNoKernelAccess(probe);
    }

    [Fact]
    public void List_AbsoluteCutoff_IncludesGoalUpdatedExactlyAtCutoff()
    {
        var workspace = Seed();
        var terminal = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            .ListGoalMetadataAsync(includeTerminalCreatedAt: true).GetAwaiter().GetResult()
            .Single(goal => goal.Status == "Completed");
        Assert.NotNull(terminal.CreatedAt);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());

        var output = Execute(["epic-list", "--since", terminal.UpdatedAt], workspace, probe);

        Assert.Contains(" window-landed=1", output);
        AssertNoKernelAccess(probe);
    }

    [Theory]
    [InlineData("24h", false)]
    [InlineData("7d", false)]
    [InlineData("2026-10-01T14:00:00+02:00", false)]
    [InlineData("24h", true)]
    [InlineData("7d", true)]
    [InlineData("2026-10-01T14:00:00+02:00", true)]
    public void List_AcceptedForms_ShowUtcHeaderWithoutWritingStores(string value, bool inline)
    {
        var workspace = Seed();
        var before = SnapshotStores(workspace);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
        var baseline = Execute(["epic-list"], workspace, probe).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        string[] args = inline ? ["epic-list", $"--since={value}"] : ["epic-list", "--since", value];
        var output = Execute(args, workspace, probe);

        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        var header = lines[0];
        Assert.StartsWith("Window since ", header);
        Assert.EndsWith("Z (UTC)", header);
        var timestamp = header["Window since ".Length..^" (UTC)".Length];
        Assert.True(DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.StartsWith(baseline[0], lines[1]);
        Assert.Matches(@"^ window-created=\d+ window-transitioned=\d+ window-failed=\d+ window-landed=\d+$",
            lines[1][baseline[0].Length..]);
        Assert.Equal(baseline[1], lines[2]);
        AssertStoresUnchanged(before);
        AssertNoKernelAccess(probe);
        Assert.True(CliPersistentStateRunner.SkipsKernelState(args));
    }

    [Theory]
    [InlineData("--since", "abc")]
    [InlineData("--since", "0h")]
    [InlineData("--since", "0d")]
    [InlineData("--since", "")]
    [InlineData("--since=-1h")]
    [InlineData("--since=99999999d")]
    [InlineData("--since=")]
    [InlineData("--since")]
    public void List_InvalidWindow_ReportsUsageAndLeavesExistingStoresUnchanged(params string[] flags)
    {
        var workspace = Seed();
        var before = SnapshotStores(workspace);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());

        var error = Assert.Throws<ArgumentException>(() => Execute(["epic-list", .. flags], workspace, probe));

        Assert.Equal(CliCommandHelp.EpicListUsage, error.Message);
        AssertStoresUnchanged(before);
        AssertNoKernelAccess(probe);
    }

    [Theory]
    [InlineData("--since", "abc")]
    [InlineData("--since", "0h")]
    [InlineData("--since", "")]
    [InlineData("--since=")]
    [InlineData("--since")]
    public void List_InvalidWindow_DoesNotCreateMissingStores(params string[] flags)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());

        var error = Assert.Throws<ArgumentException>(() => Execute(["epic-list", .. flags], workspace, probe));

        Assert.Equal(CliCommandHelp.EpicListUsage, error.Message);
        Assert.False(File.Exists(workspace.SqliteStatePath));
        Assert.False(File.Exists(workspace.BacklogStorePath));
        Assert.False(File.Exists(workspace.PortfolioStorePath));
        AssertNoKernelAccess(probe);
    }

    [Theory]
    [InlineData("--since=2000-01-01T00:00:00Z")]
    [InlineData("--SINCE", "2000-01-01T00:00:00Z")]
    public void List_MissingStores_PrintsHeaderAndNoEpicsWithoutCreatingStores(params string[] flags)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());

        Assert.Equal($"Window since 2000-01-01T00:00:00.0000000Z (UTC){Environment.NewLine}No epics.{Environment.NewLine}",
            Execute(["epic-list", .. flags], workspace, probe));

        Assert.False(File.Exists(workspace.SqliteStatePath));
        Assert.False(File.Exists(workspace.BacklogStorePath));
        Assert.False(File.Exists(workspace.PortfolioStorePath));
        AssertNoKernelAccess(probe);
    }

    [Fact]
    public void List_Help_DescribesWindowFormsAndLandedUpdateStandIn()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel());

        var output = Execute(["epic-list", "--help"], workspace, probe);

        Assert.Contains("Usage: epic-list [--since <Nh|Nd|timestamp>]", output);
        foreach (var text in new[] { "--since", "24h", "7d", "absolute timestamp", "window-landed uses the goal update time until a landed-at timestamp exists" })
            Assert.Contains(text, output);
        AssertNoKernelAccess(probe);
    }

    private OrchestratorWorkspace Seed()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var statuses = Enum.GetValues<GoalStatus>();
        for (var index = 0; index < statuses.Length; index++)
            kernel.CreateGoal(new GoalId((index + 1).ToString("x8") + new string('1', 24)), $"{statuses[index]} title");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select((goal, index) => goal with { Status = statuses[index] }).ToArray()
        });
        kernel.CreateGoal(new GoalId("eeeeeeee111111111111111111111111"), "Unrelated title");
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.AddEpicAsync("Board").GetAwaiter().GetResult();
        foreach (var goal in snapshot.Goals)
            store.AssignGoalToEpicAsync(goal.Id, epic.Id).GetAwaiter().GetResult();
        store.AssignGoalToEpicAsync("ffffffff111111111111111111111111", epic.Id).GetAwaiter().GetResult();
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var open = backlog.AddAsync("Open member").GetAwaiter().GetResult();
        var done = backlog.AddAsync("Done member").GetAwaiter().GetResult();
        backlog.CloseAsync(done.Id).GetAwaiter().GetResult();
        foreach (var id in new[] { open.Id, done.Id })
            store.AssignBacklogItemToEpicAsync(id, epic.Id).GetAwaiter().GetResult();
        return workspace;
    }

    private static (string Path, byte[] Bytes, DateTime Modified)[] SnapshotStores(OrchestratorWorkspace workspace) =>
        new[] { workspace.SqliteStatePath, workspace.BacklogStorePath, workspace.PortfolioStorePath }
            .Select(path => (path, File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path))).ToArray();

    private static void AssertStoresUnchanged((string Path, byte[] Bytes, DateTime Modified)[] before)
    {
        foreach (var file in before)
        {
            Assert.Equal(file.Bytes, File.ReadAllBytes(file.Path));
            Assert.Equal(file.Modified, File.GetLastWriteTimeUtc(file.Path));
        }
    }

    private static string Execute(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, probe, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
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
