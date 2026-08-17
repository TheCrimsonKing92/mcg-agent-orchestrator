using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("GoalWorktreeCleanupHooks")]
public sealed class CliCommandTestsGoalBoard : CliCommandTestBase
{
    [Xunit.Fact]
    public void GoalsBoardPrintsOperationalRowsAndSummary()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Operational board title");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["goals", "--board", "--all"],
                repository,
                workspace,
                ref agents,
                new InMemoryModelProviderRegistry([]),
                ref profiles,
                ref currentGoal));

            Xunit.Assert.Contains("shown=1 omitted=0", output, StringComparison.Ordinal);
            Xunit.Assert.Contains($"Operational board title [{goal.Id.Value[..8]}]", output, StringComparison.Ordinal);
            Xunit.Assert.Equal(0, repository.TransactAsyncCount);
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db")));
            Xunit.Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalBoardCommandCapturesProcessSnapshotOnceForMultipleGoals()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "First goal");
            _ = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Second goal");
            var calls = 0;

            _ = CaptureConsole(() => GoalBoardCommand.Run(
                ["goals", "--board", "--all"],
                new InMemoryTransactionalStateRepository(kernel),
                workspace,
                processSnapshotFactory: () =>
                {
                    calls++;
                    return ProcessCommandLineSnapshot.Empty;
                }));

            Xunit.Assert.Equal(1, calls);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void GoalBoardArgumentsEnforceLimitsAndMutualExclusion()
    {
        Xunit.Assert.Equal(50, GoalBoardOptions.Parse(["goals", "--board"]).Limit);
        Xunit.Assert.Equal(1, GoalBoardOptions.Parse(["goals", "--board", "--limit", "1"]).Limit);
        Xunit.Assert.Equal(500, GoalBoardOptions.Parse(["goals", "--board", "--limit", "500"]).Limit);
        Xunit.Assert.Null(GoalBoardOptions.Parse(["goals", "--board", "--all"]).Limit);
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--limit", "0"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--limit", "501"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--limit"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--limit", "x"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--all", "--limit", "5"]));
        Xunit.Assert.Throws<ArgumentException>(() => GoalBoardOptions.Parse(["goals", "--board", "--unknown"]));
    }
}

public sealed class GoalBoardProjectorTests
{
    [Xunit.Fact]
    public void MixedStatusesIncludeExactlyOperationalLifecycleSet()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var facts = Enum.GetValues<GoalStatus>()
            .Select((status, index) => Fact(
                id: index.ToString("x32"),
                status: status,
                title: $"{status} title",
                stage: $"stage-{status}"))
            .ToArray();

        var projection = GoalBoardProjector.Project(facts, new GoalBoardOptions(null), now);

        foreach (var included in new[]
                 {
                     GoalStatus.Draft, GoalStatus.Active, GoalStatus.WaitingForHuman, GoalStatus.Parked,
                     GoalStatus.Verifying, GoalStatus.Verified, GoalStatus.AcceptanceFailed, GoalStatus.Failed
                 })
        {
            var row = Xunit.Assert.Single(projection.Rows.Where(candidate => candidate.StartsWith($"{included} title [", StringComparison.Ordinal)));
            Xunit.Assert.Contains($"status={included}", row, StringComparison.Ordinal);
            Xunit.Assert.Contains($"stage=stage-{included}", row, StringComparison.Ordinal);
        }

        Xunit.Assert.DoesNotContain(projection.Rows, row => row.StartsWith("Completed title", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(projection.Rows, row => row.StartsWith("Cancelled title", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(projection.Rows, row => row.StartsWith("Superseded title", StringComparison.Ordinal));
        Xunit.Assert.Equal(8, projection.Shown);
    }

    [Xunit.Fact]
    public void DistinctOperationalFactsChooseRequiredControlAndSignal()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var facts = new[]
        {
            Fact("10000000000000000000000000000000", attention: 1, signals: [new("attention", now.AddHours(-5))]),
            Fact("20000000000000000000000000000000", intents: 2, signals: [new("intent", now.AddMinutes(-4))]),
            Fact("30000000000000000000000000000000", dead: true, recovery: "refresh-dispatch 3", signals: [new("dispatch", now.AddMinutes(-3))]),
            Fact("40000000000000000000000000000000", liveAcceptance: true, work: "gate:live", signals: [new("acceptance", now.AddMinutes(-2))]),
            Fact("50000000000000000000000000000000", liveWorker: true, work: "developer:running", signals: [new("dispatch", now.AddMinutes(-1))]),
            Fact("60000000000000000000000000000000", status: GoalStatus.Parked),
            Fact("70000000000000000000000000000000", status: GoalStatus.Failed)
        };

        var rows = GoalBoardProjector.Project(facts, new GoalBoardOptions(null), now).Rows;

        AssertRow(rows, "10000000", "signal=attention:5h", "next=attention show 10000000");
        AssertRow(rows, "20000000", "signal=intent:4m", "held=operator intent pending");
        AssertRow(rows, "30000000", "signal=dispatch:3m", "next=refresh-dispatch 3");
        AssertRow(rows, "40000000", "work=gate:live", "held=acceptance live");
        AssertRow(rows, "50000000", "work=developer:running", "held=worker live");
        AssertRow(rows, "60000000", "held=parked");
        AssertRow(rows, "70000000", "next=next 70000000 --full");
        Xunit.Assert.All(rows, row =>
            Xunit.Assert.True(row.Contains("next=", StringComparison.Ordinal) ^ row.Contains("held=", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public void LimitsSummaryOneLineBoundsAndUnknownFactsAreExplicit()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var longTitle = new string('x', 200) + "\r\nfull objective must not render";
        var facts = Enumerable.Range(0, 55)
            .Select(index => Fact(index.ToString("x32"), title: longTitle))
            .ToArray();

        var projection = GoalBoardProjector.Project(facts, new GoalBoardOptions(50), now);

        Xunit.Assert.Equal(50, projection.Shown);
        Xunit.Assert.Equal(5, projection.Omitted);
        Xunit.Assert.Equal("rerun goals --board --all or --limit 55", projection.RerunInstruction);
        Xunit.Assert.All(projection.Rows, row =>
        {
            Xunit.Assert.DoesNotContain('\r', row);
            Xunit.Assert.DoesNotContain('\n', row);
            Xunit.Assert.True(row.Length <= 360, $"row length was {row.Length}");
            Xunit.Assert.DoesNotContain("full objective must not render", row, StringComparison.Ordinal);
            Xunit.Assert.Contains("attention=unknown intents=unknown", row, StringComparison.Ordinal);
            Xunit.Assert.Contains("worktree=unknown ahead=? behind=?", row, StringComparison.Ordinal);
        });
    }

    [Xunit.Fact]
    public void BacklogAndWorktreeFactsRenderWithoutTreatingAbsenceAsClean()
    {
        var now = DateTimeOffset.Parse("2026-08-17T12:00:00Z");
        var facts = new[]
        {
            Fact("10000000000000000000000000000000", backlog: "abcdef12/full", worktree: new("clean", 2, 1)),
            Fact("20000000000000000000000000000000", worktree: GoalBoardWorktreeFact.Absent),
            Fact("30000000000000000000000000000000", worktree: GoalBoardWorktreeFact.Unknown),
            Fact("40000000000000000000000000000000", worktree: new("dirty", 0, 0))
        };

        var rows = GoalBoardProjector.Project(facts, new GoalBoardOptions(null), now).Rows;

        AssertRow(rows, "10000000", "backlog=abcdef12/full", "worktree=clean ahead=2 behind=1");
        AssertRow(rows, "20000000", "worktree=- ahead=? behind=?");
        AssertRow(rows, "30000000", "worktree=unknown ahead=? behind=?");
        AssertRow(rows, "40000000", "worktree=dirty ahead=0 behind=0");
    }

    private static GoalBoardGoalFact Fact(
        string id,
        GoalStatus status = GoalStatus.Active,
        string title = "Board goal",
        string stage = "developing",
        string work = "none",
        IReadOnlyList<GoalBoardSignalFact>? signals = null,
        int? attention = null,
        int? intents = null,
        string backlog = "-",
        GoalBoardWorktreeFact? worktree = null,
        bool dead = false,
        string? recovery = null,
        bool liveAcceptance = false,
        bool liveWorker = false) =>
        new(
            id,
            title,
            status,
            stage,
            work,
            signals ?? [],
            attention,
            intents,
            backlog,
            worktree ?? GoalBoardWorktreeFact.Unknown,
            dead,
            recovery,
            liveAcceptance,
            liveWorker,
            $"next {id[..8]} --full");

    private static void AssertRow(IReadOnlyList<string> rows, string id, params string[] expected)
    {
        var row = Xunit.Assert.Single(rows.Where(candidate => candidate.Contains($"[{id}]", StringComparison.Ordinal)));
        foreach (var value in expected)
            Xunit.Assert.Contains(value, row, StringComparison.Ordinal);
    }
}
