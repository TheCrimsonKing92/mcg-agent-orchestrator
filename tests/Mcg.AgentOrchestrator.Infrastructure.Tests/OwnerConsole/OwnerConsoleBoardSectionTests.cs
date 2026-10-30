using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Data;
using System.Drawing;
using System.Reflection;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Parallel-safe: scenes own their databases, leases, instance GUI, input and screen buffer.
public sealed class OwnerConsoleBoardSectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Columns = ["GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE"];

    [Fact]
    public async Task AllProjects_Screen_DrawsOneHeaderAndGroupedFooter()
    {
        using var projects = await Projects.CreateAsync();
        projects.RemoveProject("alpha");
        projects.RemoveProject("beta");
        var leases = new List<IDisposable>();
        try
        {
            leases.Add(ConductorLoopLease.Acquire(projects.Home.OrchestratorDirectory));
            foreach (var name in new[] { "crosswalk", "necromancer", "net-health", "pz-item-searcher", "schema-drift" })
            {
                var workspace = projects.Register(name);
                await projects.SaveAsync(workspace, new AgentOrchestratorKernel());
                if (name != "net-health") leases.Add(ConductorLoopLease.Acquire(workspace.OrchestratorDirectory));
            }
            using var scene = new Scene(projects);
            await scene.RefreshAsync();

            var screen = scene.CaptureBoard();

            var header = Assert.Single(screen.Lines.Where(line => line.StartsWith("── ")));
            Assert.StartsWith("── home · conductor running · 1 active · 0 verified · last landed never ──", header);
            AssertHeaderLine(header, scene.View.BoardPane);
            var headerIndex = Array.IndexOf(screen.Lines, header);
            Assert.Contains("11111111", screen.Lines[headerIndex + 1]);
            Assert.Contains("home goal", screen.Lines[headerIndex + 1]);
            const string footer = "idle: crosswalk, necromancer, pz-item-searcher, schema-drift · stopped: net-health";
            Assert.Equal(footer, Assert.Single(screen.Lines.Where(line => line.StartsWith("idle:"))).TrimEnd());
            Assert.Equal(footer, screen.Lines[^1].TrimEnd());
            Assert.Equal(scene.View.BoardPane.GetScheme().Highlight, screen.Attributes[headerIndex, 0]);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    [Fact]
    public async Task TwoProjects_Screen_DrawsOrderedSectionsWithoutFooter()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects);
        Assert.True(scene.Source.HandleScope("home, alpha").Success);
        await scene.RefreshAsync();

        var screen = scene.CaptureBoard();

        var headers = screen.Lines.Select((text, index) => (text, index))
            .Where(line => line.text.StartsWith("── ")).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.StartsWith("── home · conductor stopped · 1 active", headers[0].text);
        Assert.StartsWith("── alpha · conductor stopped · 2 active", headers[1].text);
        AssertHeaderLine(headers[0].text, scene.View.BoardPane);
        AssertHeaderLine(headers[1].text, scene.View.BoardPane);
        Assert.Equal(headers[0].index + 2, headers[1].index);
        Assert.Contains("11111111", screen.Lines[headers[0].index + 1]);
        Assert.Contains("22222222", screen.Lines[headers[1].index + 1]);
        Assert.Contains("33333333", screen.Lines[headers[1].index + 2]);
        Assert.DoesNotContain(screen.Lines, IsFooterLine);
        Assert.Equal(scene.View.BoardPane.SuperView!.Viewport.Height, scene.View.BoardPane.Viewport.Height);
    }

    [Fact]
    public async Task Navigation_HeadersAndFooter_AlwaysSelectsProjectQualifiedGoals()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects);
        await scene.RefreshAsync();
        await scene.FocusBoardAsync();
        AssertSelectedGoal(scene, "home", "11111111-home");
        await scene.View.HandleKeyAsync(Key.CursorDown);
        AssertSelectedGoal(scene, "alpha", "22222222-verified");

        foreach (var key in new[] { Key.Home, Key.End, Key.CursorDown, Key.PageDown, Key.PageUp, Key.CursorUp })
        {
            await scene.View.HandleKeyAsync(key);
            AssertSelectedGoal(scene);
        }
        // Pointer/programmatic selection enters through the same ValueChanged event.
        foreach (var headerIndex in new[] { 0, 2 })
        {
            scene.View.BoardPane.SetSelection(0, headerIndex, false);
            AssertSelectedGoal(scene);
        }
        scene.View.BoardPane.SetSelection(0, scene.View.BoardTable.Rows.Count, false);
        AssertSelectedGoal(scene, "alpha", "33333333-active");
        await scene.View.HandleKeyAsync(Key.End);
        await scene.View.HandleKeyAsync(Key.CursorDown);
        AssertSelectedGoal(scene, "alpha", "33333333-active");

        // A refresh with the same id in both projects restores by (project, id).
        await projects.AddGoalAsync(projects.Home, "33333333-active", GoalStatus.Active);
        await scene.RefreshAsync();
        AssertSelectedGoal(scene, "alpha", "33333333-active");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleProject_Screen_MatchesLegacyTableAndColumnStyles(bool projectLaunch)
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects, projectLaunch ? projects.Alpha : projects.Home);
        if (!projectLaunch) Assert.True(scene.Source.HandleScope("alpha").Success);
        await scene.RefreshAsync();
        var actual = scene.CaptureBoard();
        var board = scene.View.BoardPane;
        Assert.Equal(Columns, scene.View.BoardTable.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal(new[] { "22222222", "", "alpha goal", "Normal", "waiting for gate (1st in queue)", "1m" },
            scene.View.BoardTable.Rows[0].ItemArray.Cast<string>());
        Assert.Equal(new[] { "33333333", "", "alpha goal", "Normal", "in progress: Developer", "1m" },
            scene.View.BoardTable.Rows[1].ItemArray.Cast<string>());
        Assert.Equal(new[] { 8, 4, 10, 8, 31, 3 }, Enumerable.Range(0, Columns.Length)
            .Select(column => board.Style.GetOrCreateColumnStyle(column).MaxWidth));
        Assert.Equal(new[] { true, true, true, false, true, true }, Enumerable.Range(0, Columns.Length)
            .Select(column => board.Style.GetOrCreateColumnStyle(column).Visible));
        Assert.DoesNotContain(actual.Lines, line => line.StartsWith("── ") || IsFooterLine(line));
        Assert.Equal(board.SuperView!.Viewport.Height, board.Viewport.Height);

        // Render the old plain TableView in the same window and geometry, without the subclass.
        var parent = board.SuperView;
        using var legacy = new TableView
        {
            Table = new DataTableSource(scene.View.BoardTable), Style = board.Style,
            FullRowSelect = board.FullRowSelect, MaxCellWidth = board.MaxCellWidth,
            Width = Dim.Fill(), Height = Dim.Fill()
        };
        legacy.SetSelection(0, board.Value!.SelectedCell.Y, false);
        parent.Remove(board);
        parent.Add(legacy);
        try
        {
            var expected = scene.CaptureBoard(legacy);
            Assert.Equal(actual.Bounds, expected.Bounds);
            Assert.Equal(actual.Lines, expected.Lines);
        }
        finally { parent.Remove(legacy); parent.Add(board); }
    }

    [Fact]
    public async Task EmptyProjects_Screen_ShowsDimmedDiagnosticFooterAndNoSelection()
    {
        using var projects = await Projects.CreateAsync();
        await File.WriteAllTextAsync(projects.Beta.SqliteStatePath, "invalid SQLite data", Token);
        using var scene = new Scene(projects);
        Assert.True(scene.Source.HandleScope("alpha, beta").Success);
        await scene.RefreshAsync();
        var model = scene.Model;
        scene.View.Render(model with { Board = [.. model.Board.Where(row => row.IsDiagnostic)] });

        var screen = scene.CaptureBoard();

        Assert.DoesNotContain(screen.Lines, line => line.StartsWith("── "));
        Assert.Empty(scene.View.BoardTable.Rows);
        Assert.Null(scene.View.SelectedGoalId);
        Assert.Null(scene.View.SelectedProject);
        Assert.StartsWith("stopped: alpha · unreadable: ", screen.Lines[^1]);
        var diagnostic = Assert.Single(scene.Model.Board.Where(row => row.Dimmed)).Title;
        var start = screen.Lines[^1].IndexOf("unreadable: ", StringComparison.Ordinal) + "unreadable: ".Length;
        Assert.StartsWith(diagnostic[..Math.Min(diagnostic.Length, screen.Bounds.Width - start)], screen.Lines[^1][start..]);
        Assert.Equal(scene.View.BoardPane.GetScheme().Disabled, screen.Attributes[screen.Lines.Length - 1, start]);
        await scene.FocusBoardAsync();
        foreach (var key in new[] { Key.Home, Key.End, Key.CursorDown, Key.PageDown })
        {
            await scene.View.HandleKeyAsync(key);
            Assert.Null(scene.View.SelectedGoalId);
            Assert.Null(scene.View.SelectedProject);
        }
    }

    [Fact]
    public async Task Scrolling_Screen_KeepsFooterFixedAndSelectionOnGoals()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects);
        await scene.RefreshAsync();
        var home = scene.Model.Board.First(row => row.Project == "home" && !row.IsDiagnostic);
        var alpha = scene.Model.Board.First(row => row.Project == "alpha" && !row.IsDiagnostic);
        var goals = new[] { home, alpha }.SelectMany((row, project) => Enumerable.Range(0, 12)
            .Select(index => row with
            {
                GoalId = $"{project + 7}{index:D7}-scroll", GoalPrefix = $"{project + 7}{index:D7}"
            }));
        scene.View.Render(scene.Model with
        {
            Board = [.. scene.Model.Board.Where(row => row.IsDiagnostic), .. goals]
        });
        await scene.FocusBoardAsync();
        var first = scene.CaptureBoard();
        Assert.Equal("stopped: beta", first.Lines[^1].TrimEnd());
        await scene.View.HandleKeyAsync(Key.End);
        AssertSelectedGoal(scene, "alpha", "80000011-scroll");
        Assert.True(scene.View.BoardPane.RowOffset > 0);
        var last = scene.CaptureBoard();
        Assert.Equal(first.Bounds, last.Bounds);
        Assert.Equal(first.Lines[^1], last.Lines[^1]);
        Assert.Contains(last.Lines, line => line.Contains("80000011", StringComparison.Ordinal));
        await scene.View.HandleKeyAsync(Key.PageUp);
        AssertSelectedGoal(scene);
        await scene.View.HandleKeyAsync(Key.PageDown);
        AssertSelectedGoal(scene);
    }

    private static bool IsFooterLine(string line) =>
        new[] { "running:", "idle:", "stopped:", "unreadable:" }.Any(line.StartsWith);

    private static void AssertHeaderLine(string line, TableView pane)
    {
        Assert.StartsWith("── ", line);
        Assert.DoesNotContain('│', line);
        Assert.DoesNotContain('┃', line);
        Assert.DoesNotContain('|', line);
        // Terminal.Gui defaults SeparatorSymbol to a space; header prose still contains spaces.
        if (!char.IsWhiteSpace(pane.SeparatorSymbol)) Assert.DoesNotContain(pane.SeparatorSymbol.ToString(), line);
    }

    private static void AssertSelectedGoal(Scene scene, string? project = null, string? id = null)
    {
        Assert.NotNull(scene.View.SelectedProject);
        Assert.NotNull(scene.View.SelectedGoalId);
        var index = scene.View.BoardPane.Value!.SelectedCell.Y;
        Assert.InRange(index, 0, scene.View.BoardTable.Rows.Count - 1);
        Assert.False(scene.Model.Board[index].IsDiagnostic);
        Assert.Equal(scene.Model.Board[index].Project, scene.View.SelectedProject);
        Assert.Equal(scene.Model.Board[index].GoalId, scene.View.SelectedGoalId);
        if (project is not null) Assert.Equal(project, scene.View.SelectedProject);
        if (id is not null) Assert.Equal(id, scene.View.SelectedGoalId);
    }

    [Fact]
    public async Task AllProjects_Refresh_RendersOrderedHeadersAndRankedGoals()
    {
        using var projects = await Projects.CreateAsync();
        using var lease = ConductorLoopLease.Acquire(projects.Alpha.OrchestratorDirectory);
        Assert.True(new ConductorLeaseLiveness(projects.Alpha.OrchestratorDirectory).IsRunning());
        using var scene = new Scene(projects);
        await scene.RefreshAsync();

        Assert.Equal(new[] { "home", "home", "alpha", "alpha", "alpha", "beta" },
            scene.Model.Board.Select(row => row.Project));
        var headers = scene.Model.Board.Where(row => row.IsDiagnostic).ToArray();
        Assert.Equal(new[] { "home", "alpha", "beta" }, headers.Select(row => row.Project));
        Assert.Equal("home · conductor stopped · 1 active · 0 verified · last landed never", headers[0].Title);
        Assert.Equal("alpha · conductor running · 2 active · 1 verified · last landed never", headers[1].Title);
        Assert.Equal("beta · conductor stopped · 0 active · 0 verified · last landed never", headers[2].Title);
        Assert.Equal(new[] { "11111111-home", "22222222-verified", "33333333-active" },
            scene.Model.Board.Where(row => !row.IsDiagnostic).Select(row => row.GoalId));
        Assert.Equal(Columns, scene.View.BoardTable.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal(Columns, scene.View.BoardPane.Table!.ColumnNames);
        Assert.Equal(5, scene.View.BoardTable.Rows.Count);
        var pane = Assert.IsType<OwnerConsoleBoardPane>(scene.View.BoardPane);
        Assert.Equal(new[] { 0, 2 }, pane.Headers.Keys.Order());
        Assert.Equal("── home · conductor stopped · 1 active · 0 verified · last landed never ──", pane.Headers[0]);
        Assert.Equal("stopped: beta", string.Concat(OwnerConsoleBoardSection.Footer(scene.Model).Select(segment => segment.Text)));
        for (var index = 0; index < scene.View.BoardTable.Rows.Count; index++)
        {
            if (scene.Model.Board[index].IsDiagnostic)
            {
                Assert.Empty(scene.Model.Board[index].GoalId);
                Assert.False(scene.Model.Board[index].Dimmed);
                Assert.All(scene.View.BoardTable.Rows[index].ItemArray, cell => Assert.Equal("", cell));
            }
            else Assert.Equal(scene.Model.Board[index].Title, scene.View.BoardTable.Rows[index]["TITLE"]);
        }
    }

    [Fact]
    public async Task DecisionComposition_ResortsGoals_RestoresProjectBoundaries()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects);
        var composed = await scene.Source.ComposeAsync(await scene.Builder.BuildBoardAsync(Token), Token);
        var sorted = scene.Builder.WithDecisions(composed, [], 0);
        Assert.NotEqual(composed.Board.Select(row => row.GoalId), sorted.Board.Select(row => row.GoalId));
        scene.View.Render(sorted);
        Assert.Equal(composed.Board, scene.Model.Board);
        scene.View.Render(scene.Model);
        Assert.Equal(composed.Board, scene.Model.Board);
    }

    [Fact]
    public async Task EmptyProject_LiveLeaseAndOldLanding_ShowsIdleAndLandingAge()
    {
        using var projects = await Projects.CreateAsync();
        await projects.AddGoalAsync(projects.Beta, "44444444-landed", GoalStatus.Completed);
        var state = new SqliteOrchestratorStateRepository(projects.Beta.SqliteStatePath);
        await state.RecordPromotionFactsAsync([new("44444444-landed", new('a', 40), new('b', 40),
            new('c', 40), projects.Clock.GetUtcNow().AddHours(-2), GoalPromotionFact.SoloProvenance)], Token);
        using var lease = ConductorLoopLease.Acquire(projects.Beta.OrchestratorDirectory);
        using var scene = new Scene(projects);
        await scene.RefreshAsync();

        var header = Assert.Single(scene.Model.Board.Where(row => row.Project == "beta"));
        Assert.True(header.IsDiagnostic);
        Assert.Equal("beta · conductor idle · 0 active · 0 verified · last landed 2h", header.Title);
        Assert.Equal("idle: beta", string.Concat(OwnerConsoleBoardSection.Footer(scene.Model).Select(segment => segment.Text)));
        Assert.DoesNotContain(scene.Model.Board, row => row.GoalId == "44444444-landed");
    }

    [Fact]
    public async Task Navigation_SectionBoundaries_SkipsHeadersAndRestoresGoalIdentity()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects);
        await scene.RefreshAsync();
        await scene.FocusBoardAsync();
        Assert.Equal("11111111-home", scene.View.SelectedGoalId);
        await scene.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal("alpha", scene.View.SelectedProject);
        Assert.Equal("22222222-verified", scene.View.SelectedGoalId);
        Assert.Equal(3, scene.View.BoardPane.Value!.SelectedCell.Y);
        await scene.View.HandleKeyAsync(Key.CursorUp);
        Assert.Equal("11111111-home", scene.View.SelectedGoalId);
        await scene.View.HandleKeyAsync(Key.End);
        Assert.Equal("33333333-active", scene.View.SelectedGoalId);
        await scene.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal("33333333-active", scene.View.SelectedGoalId);
        await scene.View.HandleKeyAsync(Key.Home);
        Assert.Equal("11111111-home", scene.View.SelectedGoalId);

        // The lost goal's display position now holds a header; restoration skips it.
        var rows = scene.Model.Board;
        scene.View.Render(scene.Model with { Board = [rows[0], rows[2], rows[3], rows[4], rows[5]] });
        Assert.Equal("22222222-verified", scene.View.SelectedGoalId);
        Assert.Equal("alpha", scene.View.SelectedProject);
        scene.View.Render(scene.Model with { Board = [.. scene.Model.Board.Reverse()] });
        Assert.Equal("22222222-verified", scene.View.SelectedGoalId);
    }

    [Theory]
    [InlineData("enter")]
    [InlineData("goal 11111111")]
    [InlineData("gate")]
    [InlineData("accept")]
    [InlineData("answer")]
    public async Task GoalCommands_CurrentHeader_RefusesWithoutGoalSideEffects(string command)
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects);
        await scene.RefreshAsync();
        scene.View.Render(scene.Model with { Board = [.. scene.Model.Board.Where(row => row.IsDiagnostic)] });
        await scene.FocusBoardAsync();
        Assert.Null(scene.View.SelectedGoalId);
        Assert.Null(scene.View.SelectedProject);
        Assert.Equal(scene.Model.Board[0].Title, scene.View.PreviewText);
        scene.Harness.State.Calls.Clear();
        if (command == "enter") await scene.View.HandleKeyAsync(Key.Enter);
        else await scene.CommandAsync(command);

        Assert.Contains(scene.Model.Board[0].Title, scene.View.NoticeText);
        Assert.Empty(scene.Dialogs.Texts);
        Assert.Empty(scene.Harness.State.Calls);
        Assert.Empty(scene.Harness.Answers.Calls);
        Assert.Empty(scene.Harness.Conductor.Calls);
        Assert.False(scene.View.GateView.IsOpen);
    }

    [Fact]
    public async Task UnreadableProject_Refresh_DimsOnlyItsDiagnosticHeader()
    {
        using var projects = await Projects.CreateAsync();
        await File.WriteAllTextAsync(projects.Beta.SqliteStatePath, "invalid SQLite data", Token);
        using var scene = new Scene(projects);
        await scene.RefreshAsync();

        var diagnostic = Assert.Single(scene.Model.Board.Where(row => row.Project == "beta"));
        Assert.True(diagnostic.IsDiagnostic);
        Assert.True(diagnostic.Dimmed);
        Assert.Empty(diagnostic.GoalId);
        Assert.Contains("beta: state read failed:", diagnostic.Title);
        Assert.Equal(2, scene.Model.Board.Count(row => row.IsDiagnostic && !row.Dimmed));
        Assert.Equal(3, scene.Model.Board.Count(row => !row.IsDiagnostic));
        var args = (RowColorGetterArgs)Activator.CreateInstance(typeof(RowColorGetterArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [scene.View.BoardPane.Table!, scene.Model.Board.IndexOf(diagnostic)], null)!;
        var color = scene.View.BoardPane.Style.RowColorGetter!(args);
        Assert.NotNull(color);
        Assert.Equal(scene.View.BoardPane.GetScheme().Disabled, color.Normal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleProject_ExplicitScopeOrProjectLaunch_PreservesGoldenCells(bool projectLaunch)
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects, projectLaunch ? projects.Alpha : projects.Home);
        if (!projectLaunch) Assert.True(scene.Source.HandleScope("alpha").Success);
        await scene.RefreshAsync();

        Assert.Equal(Columns, scene.View.BoardTable.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal(Columns, scene.View.BoardPane.Table!.ColumnNames);
        // Literal cells from the unchanged six-column projection and HEAD stage/age conventions.
        Assert.Equal(new[] { "22222222", "", "alpha goal", "Normal", "waiting for gate (1st in queue)", "1m" },
            scene.View.BoardTable.Rows[0].ItemArray.Cast<string>());
        Assert.Equal(new[] { "33333333", "", "alpha goal", "Normal", "in progress: Developer", "1m" },
            scene.View.BoardTable.Rows[1].ItemArray.Cast<string>());
        Assert.Equal(2, scene.Model.Board.Length);
        Assert.DoesNotContain(scene.Model.Board, row => row.IsDiagnostic);
    }

    [Fact]
    public async Task SingleProject_UnreadableState_PreservesSelectableDiagnostic()
    {
        using var projects = await Projects.CreateAsync();
        await File.WriteAllTextAsync(projects.Beta.SqliteStatePath, "invalid SQLite data", Token);
        using var scene = new Scene(projects);
        Assert.True(scene.Source.HandleScope("beta").Success);
        await scene.RefreshAsync();
        await scene.FocusBoardAsync();

        var row = Assert.Single(scene.Model.Board);
        Assert.True(row.IsDiagnostic);
        Assert.Equal("beta", scene.View.SelectedProject);
        Assert.Null(scene.View.SelectedGoalId);
        Assert.Equal("unknown", scene.View.BoardTable.Rows[0]["AGE"]);
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Contains(row.Title, scene.View.NoticeText);
    }

    [Fact]
    public async Task SectionOrder_ProjectLaunch_PutsLaunchBeforeRegistryOrder()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects, projects.Alpha);
        Assert.True(scene.Source.HandleScope("all").Success);
        await scene.RefreshAsync();
        Assert.Equal(new[] { "alpha", "home", "beta" },
            scene.Model.Board.Where(row => row.IsDiagnostic).Select(row => row.Project));
    }

    [Fact]
    public async Task ExplicitScope_RemovedForeignProject_RetainsDiagnosticAfterKnownSections()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects);
        Assert.True(scene.Source.HandleScope("home, alpha, beta").Success);
        await scene.RefreshAsync();
        Assert.Contains(scene.Model.Board, row => row.Project == "alpha" && !row.IsDiagnostic);
        projects.RemoveProject("alpha");
        Assert.DoesNotContain(projects.Registry.ListProjects(), project => project.Name == "alpha");

        await scene.RefreshAsync();

        Assert.Equal(3, scene.Model.BoardScope!.ProjectCount);
        Assert.Equal(new[] { "home", "beta", "alpha" }, scene.Model.BoardScope.Sections);
        Assert.Equal(new[] { "home", "beta", "alpha" },
            scene.Model.Board.Where(row => row.IsDiagnostic).Select(row => row.Project));
        var diagnostic = Assert.Single(scene.Model.Board.Where(row => row.Project == "alpha"));
        Assert.True(diagnostic.IsDiagnostic);
        Assert.True(diagnostic.Dimmed);
        Assert.Empty(diagnostic.GoalId);
        Assert.Contains("alpha: state read failed: state database was not found", diagnostic.Title);
        Assert.Equal("11111111-home", Assert.Single(scene.Model.Board.Where(row => !row.IsDiagnostic)).GoalId);
        Assert.Equal(scene.Model.Board, OwnerConsoleBoardSection.Arrange(scene.Model).Board);
    }

    [Fact]
    public async Task ExplicitScope_RemovedLaunchProject_RetainsOwnGoalsAndMetadata()
    {
        using var projects = await Projects.CreateAsync();
        using var lease = ConductorLoopLease.Acquire(projects.Alpha.OrchestratorDirectory);
        Assert.True(new ConductorLeaseLiveness(projects.Alpha.OrchestratorDirectory).IsRunning());
        var state = new SqliteOrchestratorStateRepository(projects.Alpha.SqliteStatePath);
        await state.RecordPromotionFactsAsync([new("22222222-verified", new('a', 40), new('b', 40),
            new('c', 40), projects.Clock.GetUtcNow().AddHours(-2), GoalPromotionFact.SoloProvenance)], Token);
        using var scene = new Scene(projects, projects.Alpha);
        Assert.True(scene.Source.HandleScope("home, alpha, beta").Success);
        await scene.RefreshAsync();
        var goals = scene.Model.Board.Where(row => row.Project == "alpha" && !row.IsDiagnostic).ToArray();
        Assert.Equal(2, goals.Length);
        projects.RemoveProject("alpha");
        Assert.DoesNotContain(projects.Registry.ListProjects(), project => project.Name == "alpha");

        await scene.RefreshAsync();

        Assert.Equal(3, scene.Model.BoardScope!.ProjectCount);
        Assert.Equal(new[] { "alpha", "home", "beta" }, scene.Model.BoardScope.Sections);
        Assert.Equal(new[] { "alpha", "home", "beta" },
            scene.Model.Board.Where(row => row.IsDiagnostic).Select(row => row.Project));
        Assert.Equal("alpha · conductor running · 2 active · 1 verified · last landed 2h", scene.Model.Board[0].Title);
        Assert.False(scene.Model.Board[0].Dimmed);
        Assert.Equal(goals, scene.Model.Board.Where(row => row.Project == "alpha" && !row.IsDiagnostic));
        Assert.All(scene.Model.Board.Where(row => row.Project == "alpha"), row => Assert.False(row.IsForeign));
    }

    [Fact]
    public async Task Arrange_UnlistedProject_PreservesItsHeaderAndGoals()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects);
        await scene.RefreshAsync();
        var model = scene.Model with { BoardScope = scene.Model.BoardScope! with { Sections = ["home", "beta"] } };

        var arranged = OwnerConsoleBoardSection.Arrange(model);

        Assert.Equal(new[] { "home", "home", "alpha", "alpha", "alpha", "beta" },
            arranged.Board.Select(row => row.Project));
        Assert.Equal(model.Board.Length, arranged.Board.Length);
        Assert.Equal(model.Board.Where(row => row.Project == "alpha"), arranged.Board.Skip(2).Take(3));
        Assert.Equal(arranged.Board, OwnerConsoleBoardSection.Arrange(arranged).Board);
    }

    [Fact]
    public void Selection_HeaderBetweenGoals_PrefersFollowingGoalOnRestoreTie()
    {
        OwnerConsoleBoardRow Goal(string id) => new(id, "", id, "Active", "", null, id);
        var header = new OwnerConsoleBoardRow("", "", "project", "", "", null, "") { IsDiagnostic = true };
        ImmutableArray<OwnerConsoleBoardRow> board = [Goal("a"), header, Goal("b")];
        var scope = new OwnerConsoleBoardScope("all", 2, "home");
        Assert.Equal(2, OwnerConsoleBoardSection.NearestGoal(board, scope, 1, 0));
        Assert.Equal(0, OwnerConsoleBoardSection.NearestGoal(board, scope, 1, -1));
        Assert.Equal(2, OwnerConsoleBoardSection.NearestGoal(board, scope, 1, 1));
    }

    private sealed class Projects : IDisposable
    {
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        internal readonly OrchestratorProjectRegistry Registry;
        internal readonly OrchestratorWorkspace Home;
        internal readonly OrchestratorWorkspace Alpha;
        internal readonly OrchestratorWorkspace Beta;
        internal readonly Dictionary<string, AgentOrchestratorKernel> Kernels = [];
        internal readonly TimeProvider Clock = new OwnerConsoleHarness().Clock;

        private Projects()
        {
            Registry = new(Path.Combine(_root, "registry"), Path.Combine(_root, "data"));
            var homeRoot = Path.Combine(_root, "repos", "home");
            Directory.CreateDirectory(homeRoot);
            Home = OrchestratorWorkspace.ForDirectory(homeRoot, null, null, Registry);
            Alpha = Register("alpha");
            Beta = Register("beta");
        }

        internal static async Task<Projects> CreateAsync()
        {
            var projects = new Projects();
            try
            {
                await projects.AddGoalAsync(projects.Home, "11111111-home", GoalStatus.Active);
                await projects.AddGoalAsync(projects.Alpha, "22222222-verified", GoalStatus.Verified);
                await projects.AddGoalAsync(projects.Alpha, "33333333-active", GoalStatus.Active);
                await projects.SaveAsync(projects.Beta, new AgentOrchestratorKernel());
                return projects;
            }
            catch { projects.Dispose(); throw; }
        }

        internal OrchestratorWorkspace Register(string name)
        {
            var root = Path.Combine(_root, "repos", name);
            Directory.CreateDirectory(root);
            var project = Registry.CreateProject(name, root, integrationBranch: "main");
            return OrchestratorWorkspace.ForProject(project.Name, root,
                integrationBranch: project.IntegrationBranch, dataRootDirectory: Registry.DataRootDirectory);
        }

        internal async Task AddGoalAsync(OrchestratorWorkspace workspace, string id, GoalStatus status)
        {
            var name = OwnerConsoleProjectLabel.ForWorkspace(workspace);
            if (!Kernels.TryGetValue(name, out var kernel)) Kernels[name] = kernel = new();
            kernel.CreateGoal(new GoalId(id), name + " goal", [new(TaskId.New(), "Build", AgentRole.Developer)]);
            var snapshot = kernel.ExportSnapshot();
            kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal.Id != id ? goal : goal with
            {
                Status = status,
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "fixture", Clock.GetUtcNow().AddMinutes(-1))],
                Tasks = goal.Tasks.Select(task => task with { Status = status == GoalStatus.Active
                    ? WorkTaskStatus.Running : WorkTaskStatus.Completed }).ToArray()
            }).ToArray() });
            await SaveAsync(workspace, kernel);
        }

        internal void RemoveProject(string name)
        {
            var registry = JsonNode.Parse(File.ReadAllText(Registry.RegistryPath))!;
            var entries = registry["Projects"]!.AsArray();
            var entry = Assert.Single(entries.Where(entry => entry!["Name"]!.GetValue<string>() == name));
            Assert.True(entries.Remove(entry));
            File.WriteAllText(Registry.RegistryPath, registry.ToJsonString());
        }

        internal async Task SaveAsync(OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workspace.SqliteStatePath)!);
            _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel, Token);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }

    private sealed class Scene : IDisposable
    {
        private readonly IApplication _app = Application.Create();
        internal readonly OwnerConsoleHarness Harness;
        internal readonly Dialogs Dialogs = new();
        internal readonly OwnerConsoleScreenController Controller;
        internal readonly OwnerConsoleViewModelBuilder Builder;
        internal readonly OwnerConsoleBoardSource Source;
        internal readonly OwnerConsoleFullScreenView View;
        internal OwnerConsoleViewModel Model => Controller.Model!;

        internal Scene(Projects projects, OrchestratorWorkspace? launch = null)
        {
            launch ??= projects.Home;
            Assert.False(_app.Initialized);
            Harness = new(projects.Clock);
            Harness.Kernel.ReplaceWithSnapshot(projects.Kernels[OwnerConsoleProjectLabel.ForWorkspace(launch)].ExportSnapshot());
            Builder = new(SqliteOrchestratorStateRepository.OpenReadOnly(launch.SqliteStatePath),
                Harness.Questions, Harness.Liveness, new Epics(), projects.Clock);
            Source = OwnerConsoleBoardSource.FromRegistry(launch, projects.Home.RootDirectory, projects.Registry, projects.Clock);
            Controller = OwnerConsoleControllerFactory.Create(Harness.Questions, Harness.Answers, Dialogs,
                Harness.State, Harness.Tail, Harness.Conductor, Harness.DigestReport, Harness.Digest, Harness.Clock);
            View = new(_app, Controller, RefreshAsync, token: Token, clock: projects.Clock,
                projectLabel: OwnerConsoleProjectLabel.ForWorkspace(launch), boardSource: Source);
        }

        internal async Task RefreshAsync()
        {
            var composed = await Source.ComposeAsync(await Builder.BuildBoardAsync(Token), Token);
            View.Render(Builder.WithDecisions(composed, [], 0));
            await TestHangGuard.WaitAsync(View.PreviewLoad, "section board preview publication");
        }

        internal async Task CommandAsync(string text)
        {
            await View.HandleKeyAsync(new Key(':'));
            View.CommandLine.Text = text;
            await View.HandleKeyAsync(Key.Enter);
        }

        internal async Task FocusBoardAsync()
        {
            View.FocusDecisions();
            await View.HandleKeyAsync(Key.Tab);
            Assert.Equal(OwnerConsolePane.Board, View.FocusedPane);
        }

        internal BoardScreen CaptureBoard(TableView? board = null)
        {
            board ??= View.BoardPane;
            using var app = Application.Create();
            OwnerConsoleFrameOutputInstaller.RequireFactoryMember(app.GetType()).SetValue(app, new ScreenFactory());
            app.Init();
            Assert.NotNull(app.Driver);
            Assert.Equal(new Size(160, 40), app.Driver.Screen.Size);
            // The in-memory terminal has fixed capabilities; complete discovery without a native reply or timeout.
            if (app.Driver.AnsiStartupGate is { } startup)
                foreach (var query in startup.PendingQueries.ToArray()) startup.MarkComplete(query);
            var session = app.Begin(View.Window);
            try
            {
                app.LayoutAndDraw(forceRedraw: true);
                var bounds = board.FrameToScreen();
                bounds.Size = new Size(board.Viewport.Width, board.SuperView!.Viewport.Height);
                Assert.True(bounds.Width > 100);
                Assert.True(bounds.Height > 5);
                var cells = app.Driver.Contents!;
                var lines = new string[bounds.Height];
                var attributes = new Terminal.Gui.Drawing.Attribute?[bounds.Height, bounds.Width];
                for (var y = 0; y < bounds.Height; y++)
                {
                    var text = new System.Text.StringBuilder();
                    for (var x = 0; x < bounds.Width; x++)
                    {
                        var cell = cells[bounds.Y + y, bounds.X + x];
                        text.Append(string.IsNullOrEmpty(cell.Grapheme) ? " " : cell.Grapheme);
                        attributes[y, x] = cell.Attribute;
                    }
                    lines[y] = text.ToString();
                }
                return new(bounds, lines, attributes);
            }
            finally { app.End(session); }
        }

        public void Dispose() { View.Dispose(); _app.Dispose(); }
    }

    private sealed record BoardScreen(Rectangle Bounds, string[] Lines, Terminal.Gui.Drawing.Attribute?[,] Attributes);

    // Use the pinned factory seam; never initialize native console input or infer rows from the model.
    private sealed class ScreenFactory() : AnsiComponentFactory(
        output: new OwnerConsoleFrameReproductionTests.RecordingOutput(160, 40) { RecordOnly = true },
        sizeMonitor: new FixedSizeMonitor())
    {
        public override IInput<char> CreateInput() => new NoInput();
    }

    private sealed class NoInput : IInput<char>
    {
        public CancellationTokenSource? ExternalCancellationTokenSource { get; set; }
        public void Initialize(ConcurrentQueue<char> inputBuffer) { }
        public void Run(CancellationToken cancellationToken) => cancellationToken.WaitHandle.WaitOne();
        public void Dispose() { }
    }

    private sealed class FixedSizeMonitor : ISizeMonitor
    {
        public bool InitialSizeReceived => true;
        public int InitialCursorRow => 0;
        public event EventHandler<SizeChangedEventArgs>? SizeChanged { add { } remove { } }
        public void Initialize(IDriver driver) => driver.SetScreenSize(160, 40);
        public bool Poll() => false;
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken) => Task.FromResult("");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected prompt");
    }
}
