using System.Collections.Immutable;
using System.Data;
using System.Reflection;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

// Parallel-safe: every scene owns its SQLite files, registry, lease and driverless GUI.
public sealed class OwnerConsoleBoardSectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Columns = ["GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE"];

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

        private OrchestratorWorkspace Register(string name)
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

        private async Task SaveAsync(OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel)
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

        public void Dispose() { View.Dispose(); _app.Dispose(); }
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
