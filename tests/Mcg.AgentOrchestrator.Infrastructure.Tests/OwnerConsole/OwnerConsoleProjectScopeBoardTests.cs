using System.Data;
using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

// Parallel-safe: each fixture owns its registry and SQLite files; GUI instances have no driver.
public sealed class OwnerConsoleProjectScopeBoardTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] LegacyColumns = ["GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE"];

    [Fact]
    public async Task UnscopedLaunch_ThreeWorkspaces_MergesRankedRowsAndShowsProject()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects, projects.Home);
        await scene.RefreshAsync();

        Assert.Equal(new[] { "home", "home", "alpha", "alpha", "beta", "beta" }, scene.Model.Board.Select(row => row.Project));
        var goals = scene.Model.Board.Where(row => !row.IsDiagnostic).ToArray();
        Assert.Equal(new[] { OwnerConsoleAttention.WorkerRunning, OwnerConsoleAttention.WaitingForGate,
            OwnerConsoleAttention.Held }, goals.Select(row => row.SortKey.Rank));
        Assert.Equal(new[] { "11111111-home", "22222222-alpha", "33333333-beta" }, goals.Select(row => row.GoalId));
        Assert.All(goals, row => Assert.Equal(row.Project != "home", row.IsForeign));
        Assert.Equal(LegacyColumns, Columns(scene.View));
        Assert.All(scene.Model.Board.Where(row => row.IsDiagnostic), row => Assert.StartsWith(row.Project, row.Title));
        Assert.Equal(Columns(scene.View), scene.View.BoardPane.Table!.ColumnNames);
        Assert.True(scene.View.BoardPane.Style.GetOrCreateColumnStyle(0).Visible);
        Assert.Equal("Owner console - all projects | decisions and activity: home", scene.View.Window.Title);
        Assert.Equal(1, scene.Model.Status.ActiveGoals); // Other panes still describe the launch workspace.
    }

    [Fact]
    public async Task ScopeCommands_NarrowInspectClearAndReject_UpdateBoardAtomically()
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects, projects.Home);
        await scene.RefreshAsync();

        await scene.CommandAsync("scope beta");
        Assert.Equal("beta", Assert.Single(scene.Model.Board).Project);
        Assert.Equal(LegacyColumns, Columns(scene.View));
        Assert.Equal(LegacyColumns, scene.View.BoardPane.Table!.ColumnNames);
        Assert.Equal("Owner console - beta | decisions and activity: home", scene.View.Window.Title);
        var refreshes = scene.Refreshes;
        await scene.CommandAsync("scope");
        Assert.Contains("scope: beta", scene.View.NoticeText);
        Assert.Equal(refreshes, scene.Refreshes);

        await scene.CommandAsync("scope all");
        Assert.Equal(6, scene.Model.Board.Length);
        Assert.Equal(LegacyColumns, Columns(scene.View));
        Assert.Equal(Columns(scene.View), scene.View.BoardPane.Table!.ColumnNames);
        Assert.Equal("Owner console - all projects | decisions and activity: home", scene.View.Window.Title);
        var previous = scene.Model;
        refreshes = scene.Refreshes;
        await scene.CommandAsync("scope alpha,zeta");
        Assert.Contains("scope: unknown project 'zeta'; known projects: home, alpha, beta", scene.View.NoticeText);
        Assert.Same(previous, scene.Model);
        Assert.Equal(refreshes, scene.Refreshes);
        await scene.CommandAsync("scope");
        Assert.Contains("scope: all projects", scene.View.NoticeText);
    }

    [Fact]
    public async Task CorruptProject_Refresh_KeepsOtherRowsAndDimsOneDiagnostic()
    {
        using var projects = await Projects.CreateAsync();
        await File.WriteAllTextAsync(projects.Beta.SqliteStatePath, "invalid SQLite data", Token);
        using var scene = new Scene(projects, projects.Home);
        await scene.RefreshAsync();

        Assert.Equal(new[] { "home", "home", "alpha", "alpha", "beta" }, scene.Model.Board.Select(row => row.Project));
        var diagnostic = Assert.Single(scene.Model.Board.Where(row => row.IsDiagnostic && row.Dimmed));
        Assert.Equal("beta", diagnostic.Project);
        Assert.True(diagnostic.Dimmed);
        Assert.Empty(diagnostic.GoalId);
        Assert.Contains("beta: state read failed:", diagnostic.Title);
        Assert.Equal(diagnostic, scene.Model.Board[^1]);
        Assert.DoesNotContain(scene.Model.Board, row => row.Project == "beta" && !row.IsDiagnostic);
        var colorArgs = (RowColorGetterArgs)Activator.CreateInstance(typeof(RowColorGetterArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [scene.View.BoardPane.Table!, 4], null)!;
        var color = scene.View.BoardPane.Style.RowColorGetter!(colorArgs);
        Assert.NotNull(color);
        Assert.Equal(scene.View.BoardPane.GetScheme().Disabled, color.Normal);

        await scene.FocusBoardAsync();
        await scene.View.HandleKeyAsync(Key.End);
        Assert.Equal("alpha", scene.View.SelectedProject);
        Assert.Equal("22222222-alpha", scene.View.SelectedGoalId);
        Assert.Equal(OwnerConsoleBoardSource.ForeignRowMessage(scene.Model.Board[3]), scene.View.PreviewText);
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Contains("belongs to alpha", scene.View.NoticeText);
        Assert.Empty(scene.Dialogs.Texts);
    }

    [Fact]
    public async Task ProjectLaunch_SingleScope_PreservesLegacyCellsAndHeader()
    {
        using var projects = await Projects.CreateAsync();
        Assert.True(projects.Alpha.IsProjectScoped);
        Assert.Equal("alpha", OwnerConsoleProjectScope.ForLaunch(projects.Alpha).Label);
        using var scene = new Scene(projects, projects.Alpha);
        var original = await scene.Builder.BuildBoardAsync(Token);
        scene.View.Render(original);
        await TestHangGuard.WaitAsync(scene.View.PreviewLoad, "legacy project preview publication");
        var originalCells = Cells(scene.View);
        Assert.Equal(LegacyColumns, Columns(scene.View));
        Assert.Equal("Owner console - alpha", scene.View.Window.Title);

        await scene.RefreshAsync();

        var row = Assert.Single(scene.Model.Board);
        Assert.Equal("alpha", row.Project);
        Assert.False(row.IsForeign);
        Assert.Equal(LegacyColumns, Columns(scene.View));
        Assert.Equal(LegacyColumns, scene.View.BoardPane.Table!.ColumnNames);
        Assert.Equal(originalCells, Cells(scene.View));
        Assert.Equal("Owner console - alpha", scene.View.Window.Title);
        Assert.Null(scene.Model.BoardScope!.DisclosureWorkspace);
    }

    [Theory]
    [InlineData("enter")]
    [InlineData("goal 11111111")]
    [InlineData("gate")]
    [InlineData("accept")]
    [InlineData("answer")]
    public async Task CollidingGoalIds_ForeignSelection_RefusesDetailAndActions(string command)
    {
        using var projects = await Projects.CreateAsync(collision: true);
        using var scene = new Scene(projects, projects.Home);
        await scene.RefreshAsync();
        Assert.Equal(2, scene.Model.Board.Count(row => row.GoalId == "11111111-home"));
        await scene.FocusBoardAsync();
        await scene.View.HandleKeyAsync(Key.CursorDown); // home -> alpha, skipping alpha's header.
        Assert.Equal("alpha", scene.View.SelectedProject);
        Assert.Equal("11111111-home", scene.View.SelectedGoalId);
        const string refusal = "11111111 belongs to alpha. Detail and actions for other projects arrive in a later slice; open it with --project=alpha console.";
        Assert.Equal(refusal, scene.View.PreviewText);
        scene.Harness.State.Calls.Clear();

        // Simulate the post-compose re-sort: restoration must retain both identity keys.
        var board = scene.Model.Board;
        scene.View.Render(scene.Model with { Board = [.. board.Reverse()] });
        Assert.Equal("alpha", scene.View.SelectedProject);
        Assert.Equal("11111111-home", scene.View.SelectedGoalId);
        Assert.Equal(3, scene.View.BoardPane.Value!.SelectedCell.Y);
        if (command == "enter") await scene.View.HandleKeyAsync(Key.Enter);
        else await scene.CommandAsync(command);

        Assert.Contains(refusal, scene.View.NoticeText);
        Assert.Empty(scene.Dialogs.Texts);
        Assert.Empty(scene.Harness.State.Calls);
        Assert.Empty(scene.Harness.Answers.Calls);
        Assert.Empty(scene.Harness.Conductor.Calls);
        Assert.False(scene.View.GateView.IsOpen);
    }

    [Theory]
    [InlineData(" BETA,alpha,beta ", "alpha, beta", 2)]
    [InlineData("beta,,HOME", "home, beta", 2)]
    public async Task ScopeSet_CaseWhitespaceAndDuplicates_UsesCanonicalNames(
        string argument, string label, int projectCount)
    {
        using var projects = await Projects.CreateAsync();
        using var scene = new Scene(projects, projects.Home);
        await scene.RefreshAsync();
        await scene.CommandAsync("scope " + argument);

        Assert.Equal(label, scene.Model.BoardScope!.Label);
        Assert.Equal(projectCount, scene.Model.BoardScope.ProjectCount);
        Assert.Equal(projectCount * 2, scene.Model.Board.Length);
        Assert.DoesNotContain("PROJECT", Columns(scene.View));
        Assert.Equal($"Owner console - {label} | decisions and activity: home", scene.View.Window.Title);
    }

    [Fact]
    public async Task MissingProject_AllSkipsAndExplicitScopeReports_RetriesAfterRecovery()
    {
        using var projects = await Projects.CreateAsync();
        File.Delete(projects.Beta.SqliteStatePath);
        using var scene = new Scene(projects, projects.Home);
        await scene.RefreshAsync();
        Assert.Equal(2, scene.Model.BoardScope!.ProjectCount);
        Assert.DoesNotContain(scene.Model.Board, row => row.Project == "beta");
        Assert.False(File.Exists(projects.Beta.SqliteStatePath));

        await scene.CommandAsync("scope beta");
        var diagnostic = Assert.Single(scene.Model.Board);
        Assert.True(diagnostic.IsDiagnostic);
        Assert.Contains("state database was not found", diagnostic.Title);
        Assert.Equal(LegacyColumns, Columns(scene.View));
        Assert.False(File.Exists(projects.Beta.SqliteStatePath));

        await projects.SaveAsync(projects.Beta, projects.Kernels["beta"]);
        await scene.RefreshAsync();
        var recovered = Assert.Single(scene.Model.Board);
        Assert.Equal("beta", recovered.Project);
        Assert.False(recovered.IsDiagnostic);
        Assert.Equal("33333333-beta", recovered.GoalId);
    }

    private static string[] Columns(OwnerConsoleFullScreenView view) =>
        view.BoardTable.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToArray();

    private static string[] Cells(OwnerConsoleFullScreenView view) =>
        view.BoardTable.Rows.Cast<DataRow>().SelectMany(row => row.ItemArray.Cast<string>()).ToArray();

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

        internal static async Task<Projects> CreateAsync(bool collision = false)
        {
            var projects = new Projects();
            try
            {
                await projects.SeedAsync(projects.Home, "11111111-home", GoalStatus.Active);
                await projects.SeedAsync(projects.Alpha, collision ? "11111111-home" : "22222222-alpha", GoalStatus.Verified);
                await projects.SeedAsync(projects.Beta, "33333333-beta", GoalStatus.WaitingForHuman);
                return projects;
            }
            catch { projects.Dispose(); throw; }
        }

        private OrchestratorWorkspace Register(string name)
        {
            var root = Path.Combine(_root, "repos", name);
            Directory.CreateDirectory(root);
            var project = Registry.CreateProject(name, root, integrationBranch: "main");
            var workspace = OrchestratorWorkspace.ForProject(project.Name, root,
                integrationBranch: project.IntegrationBranch, dataRootDirectory: Registry.DataRootDirectory);
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "project-model.json"),
                ProjectModelJson.Serialize(new(ProjectModel.CurrentSchemaVersion, ".", [], [], [], [], [], [], [], [])));
            File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "acceptance-manifest.json"), "{}");
            new ConductEventLogWriter(workspace.ConductEventsLogPath).Append("loop-start", null, "fixture conductor started");
            return workspace;
        }

        private async Task SeedAsync(OrchestratorWorkspace workspace, string id, GoalStatus status)
        {
            var kernel = new AgentOrchestratorKernel();
            var name = OwnerConsoleProjectLabel.ForWorkspace(workspace);
            kernel.CreateGoal(new GoalId(id), name + " goal", [new(TaskId.New(), "Build", AgentRole.Developer)]);
            var snapshot = kernel.ExportSnapshot();
            kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = status,
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "fixture", Clock.GetUtcNow().AddMinutes(-1))],
                Tasks = goal.Tasks.Select(task => task with { Status = status == GoalStatus.Active
                    ? WorkTaskStatus.Running : WorkTaskStatus.Completed }).ToArray()
            }).ToArray() });
            Kernels.Add(name, kernel);
            await SaveAsync(workspace, kernel);
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
        internal int Refreshes;
        internal OwnerConsoleViewModel Model => Controller.Model!;

        internal Scene(Projects projects, OrchestratorWorkspace launch)
        {
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
            Refreshes++;
            View.Render(await Source.ComposeAsync(await Builder.BuildBoardAsync(Token), Token));
            await TestHangGuard.WaitAsync(View.PreviewLoad, "scope board preview publication");
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
