using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: console capture uses AsyncLocal and each fixture owns its input records.
public sealed class PortfolioTextViewCharacterizationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private const string AssignedLine = "Board (epic0001) project=Platform (p1) goals=2 backlog=3 active=4 verified=5 parked=6 landed=7 newest=2026-10-09T12:00:00.0000000+00:00 verifying=8 failed=9 closed=10 missing=11 backlog-open=12 backlog-done=13";
    private const string UnassignedLine = "Board (epic0001) project=unassigned goals=2 backlog=3 active=4 verified=5 parked=6 landed=7 newest=2026-10-09T12:00:00.0000000+00:00 verifying=8 failed=9 closed=10 missing=11 backlog-open=12 backlog-done=13";
    private const string WindowColumns = " window-created=14 window-transitioned=15 window-failed=16 window-landed=17";
    private const string EmptyLine = "Empty (epic0002) project=unassigned goals=0 backlog=0 active=0 verified=0 parked=0 landed=0 newest=none verifying=0 failed=0 closed=0 missing=0 backlog-open=0 backlog-done=0";

    // Derived from ConsoleViews.Portfolio.cs:27-32 at e6e1daca9, before extraction.
    [Theory]
    [InlineData(true, true, AssignedLine + WindowColumns)]
    [InlineData(true, false, AssignedLine)]
    [InlineData(false, true, UnassignedLine + WindowColumns)]
    [InlineData(false, false, UnassignedLine)]
    public void FormatRollup_ProjectAndWindowPermutations_PreserveExactText(
        bool assigned, bool window, string expected)
    {
        var row = DetailedRow(assigned, window);

        Assert.Equal(expected, PortfolioTextView.FormatEpicRollupLine(row));
    }

    // Derived from ConsoleViews.Portfolio.cs:8-24 at e6e1daca9, before extraction.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrintRollups_ProjectAndUnassigned_OnlyPrintsPresentSummary(bool window)
    {
        var rows = new[] { DetailedRow(window: window), EmptyRow() };
        var summaries = new Dictionary<string, string> { [rows[0].Epic.Id] = "1 of 2 slices landed; next: render" };
        var expected = (window ? "Window since 2026-10-09T12:00:00.0000000Z (UTC)" + Environment.NewLine : "")
            + Lines(AssignedLine + (window ? WindowColumns : ""),
                "  1 of 2 slices landed; next: render", EmptyLine);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            PortfolioTextView.PrintEpicRollups(rows, window ? Timestamp : null, summaries));

        Assert.Equal(expected, actual);
    }

    // Derived from ConsoleViews.Portfolio.cs:11-16 at e6e1daca9, before extraction.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrintRollups_Empty_PreservesWindowHeaderBeforeEmptyMessage(bool window)
    {
        var expected = window
            ? Lines("Window since 2026-10-09T12:00:00.0000000Z (UTC)", "No epics.")
            : Lines("No epics.");

        Assert.Equal(expected, AsyncLocalConsoleRouter.Capture(() =>
            PortfolioTextView.PrintEpicRollups([], window ? Timestamp : null)));
    }

    // Derived from ConsoleViews.Portfolio.cs:35-54 at e6e1daca9, before extraction.
    [Fact]
    public void PrintShow_MultilineDescriptionAndMemberGoals_PreservesExactText()
    {
        var row = DetailedRow() with
        {
            Epic = Epic() with { Description = "First line\r\nSecond line\n" },
            Members = Members(),
            MemberGoals =
            [
                new("goal0001-extra", EpicProgressBucket.Active, "Active", Timestamp, "Implement renderer"),
                new("goal0002-extra", EpicProgressBucket.Missing, "Missing", null, "Missing goal")
            ]
        };
        var expected = Lines(
            "Epic: Board",
            "Id: epic0001-extra",
            "Project: Platform (p1)",
            "Description:",
            "  First line",
            "  Second line",
            "Epic Board (epic0001) members:",
            "  Goal: goal0001-extra",
            "  BacklogItem: backlog01-extra",
            AssignedLine,
            "  1 of 2 slices landed; next: render",
            "Member goals:",
            "  - goal0001 Active updated=2026-10-09T12:00:00.0000000+00:00 Implement renderer",
            "  - goal0002 Missing updated=none Missing goal");

        Assert.Equal(expected, AsyncLocalConsoleRouter.Capture(() =>
            PortfolioTextView.PrintEpicShow(row, "1 of 2 slices landed; next: render")));
    }

    // Derived from ConsoleViews.Portfolio.cs:35-54 at e6e1daca9, before extraction.
    [Fact]
    public void PrintShow_MissingDescriptionAndMembers_PreservesExactText()
    {
        var expected = Lines(
            "Epic: Empty",
            "Id: epic0002-extra",
            "Project: unassigned",
            "Description: (no description)",
            "Epic Empty (epic0002) has no members.",
            EmptyLine,
            "Member goals:");

        Assert.Equal(expected, AsyncLocalConsoleRouter.Capture(() =>
            PortfolioTextView.PrintEpicShow(EmptyRow())));
    }

    // Derived from ConsoleViews.Portfolio.cs:57-66 at e6e1daca9, before extraction.
    [Fact]
    public void PrintMembers_Empty_PreservesExactText()
    {
        Assert.Equal(Lines("Epic Board (epic0001) has no members."),
            AsyncLocalConsoleRouter.Capture(() => PortfolioTextView.PrintEpicMembers(Epic(), [])));
    }

    // Derived from ConsoleViews.Portfolio.cs:57-66 at e6e1daca9, before extraction.
    [Fact]
    public void PrintMembers_TwoKinds_PreservesFullMemberIds()
    {
        var expected = Lines("Epic Board (epic0001) members:",
            "  Goal: goal0001-extra", "  BacklogItem: backlog01-extra");

        Assert.Equal(expected, AsyncLocalConsoleRouter.Capture(() =>
            PortfolioTextView.PrintEpicMembers(Epic(), Members())));
    }

    // Derived from ConsoleViews.Portfolio.cs:70-89 at e6e1daca9, before extraction.
    [Fact]
    public void PrintPortfolio_NoEpics_PreservesExactText()
    {
        Assert.Equal(Lines("Portfolio: no epics."),
            AsyncLocalConsoleRouter.Capture(() => PortfolioTextView.PrintPortfolio([], [])));
    }

    // Derived from ConsoleViews.Portfolio.cs:79-87 at e6e1daca9, before extraction.
    [Fact]
    public void PrintPortfolio_ProjectEpicAndGoal_PreservesExactHierarchy()
    {
        var epic = Epic();
        var project = Project();
        var goal = new Goal(new GoalId("goal0001-extra"), "Implement renderer",
            [new TaskSpec(new TaskId("task0001"), "Render portfolio", AgentRole.Developer)]);
        PortfolioEpicRollup[] rollups = [new(epic, project, 2, 3, 4, 5, 6, 7, Timestamp)];
        PortfolioGoalRow[] rows = [new(goal, epic, project, null)];
        var expected = Lines(
            "Project: Platform (p1)",
            "  Epic: Board (epic0001) goals=2 active=4 verified=5 parked=6 landed=7 newest=2026-10-09T12:00:00.0000000+00:00",
            "    - goal0001 [Draft] newest=none Implement renderer");

        Assert.Equal(expected, AsyncLocalConsoleRouter.Capture(() =>
            PortfolioTextView.PrintPortfolio(rollups, rows)));
    }

    // Derived from ConsoleViews.Portfolio.cs:93-107 at e6e1daca9, before extraction.
    [Fact]
    public void PrintSuggestions_Empty_PreservesExactText()
    {
        Assert.Equal(Lines("No cluster suggestions."),
            AsyncLocalConsoleRouter.Capture(() => PortfolioTextView.PrintClusterSuggestions([])));
    }

    // Derived from ConsoleViews.Portfolio.cs:101-107 at e6e1daca9, before extraction.
    [Fact]
    public void PrintSuggestions_WithAndWithoutIds_PreservesExactText()
    {
        PortfolioClusterSuggestion[] suggestions =
        [
            new("cluster1-extra", "shared-path", "Renderer work", "Same source path",
                ["goal0001-extra", "g2"], ["backlog01-extra", "backlog02-extra"], Timestamp),
            new("cluster2-extra", "empty", "Empty cluster", "No members", [], [], Timestamp)
        ];
        var expected = Lines(
            "cluster1 signal=shared-path goals=goal0001,g2 backlog=backlog0,backlog0 title=Renderer work",
            "  evidence: Same source path",
            "cluster2 signal=empty goals=none backlog=none title=Empty cluster",
            "  evidence: No members");

        Assert.Equal(expected, AsyncLocalConsoleRouter.Capture(() =>
            PortfolioTextView.PrintClusterSuggestions(suggestions)));
    }

    [Fact]
    public void ExtractedOwners_LeaveNoPortfolioSurfaceOnConsoleViews()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.Portfolio.cs")));
        foreach (var name in new[] { nameof(PortfolioTextView), nameof(CliIdentifierText) })
        {
            var source = File.ReadAllText(Path.Combine(cliRoot, name + ".cs"));
            Assert.Contains("internal static class " + name, source);
            Assert.DoesNotMatch(@"\bpartial\s+class\b", source);
        }
        var oldMethods = typeof(ConsoleViews).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        foreach (var name in new[] { "PrintEpicRollups", "FormatEpicRollupLine", "PrintEpicShow",
                     "PrintEpicMembers", "PrintPortfolio", "PrintClusterSuggestions", "ShortId" })
            Assert.DoesNotContain(oldMethods, method => method.Name == name);
    }

    [Fact]
    public void ConsoleViewsRatchet_UsesReducedAtOrUnderBudget()
    {
        var ceiling = Assert.Single(SourceSizeRatchet.SeededClassCeilings,
            row => row.ClassName == "ConsoleViews");
        Assert.True(ceiling.MaximumTotalLineCount <= 3102);
        Assert.True(ceiling.MaximumPartialFileCount <= 38);
        Assert.Empty(SourceSizeRatchet.EvaluateClasses(VerifiedRepositoryRoot.Find(), [ceiling]));
    }

    private static PortfolioEpic Epic() =>
        new("epic0001-extra", "Board", "p1", Timestamp, "operator", Timestamp, "operator");

    private static PortfolioProject Project() =>
        new("p1", "Platform", null, Timestamp, "operator", Timestamp, "operator");

    private static PortfolioEpicMember[] Members() =>
    [
        new("epic0001-extra", PortfolioMemberKind.Goal, "goal0001-extra", Timestamp, "operator"),
        new("epic0001-extra", PortfolioMemberKind.BacklogItem, "backlog01-extra", Timestamp, "operator")
    ];

    private static EpicProgressRollup DetailedRow(bool assigned = true, bool window = false) =>
        new(Epic(), assigned ? Project() : null, [], [], 2, 3, 4, 5, 6, 7, Timestamp,
            8, 9, 10, 11, 12, 13, window ? 14 : null, window ? 15 : null,
            window ? 16 : null, window ? 17 : null);

    private static EpicProgressRollup EmptyRow() =>
        new(Epic() with { Id = "epic0002-extra", Title = "Empty", ProjectId = null },
            null, [], [], 0, 0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0);

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
