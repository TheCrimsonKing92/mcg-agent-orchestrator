using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Each test owns its workspace and stores; console capture is async-local.
public sealed class CliNextAutonomyReadOnlyRouteTests : CliTaskQueryTestSupport
{
    private static readonly GoalId TargetId = new("abc10000aaaaaaaaaaaaaaaaaaaaaaaa");

    [Theory]
    [InlineData("--autonomy")]
    [InlineData("--autonomy-policy")]
    public async Task ReadOnlyDatabase_AutonomyForms_MatchWriterOutput(string flag)
    {
        var root = CreateTempDirectory();
        var writerRoot = CreateTempDirectory();
        try
        {
            var workspace = await SeedWorkspaceAsync(root);
            CopyWorkspace(root, writerRoot);
            var writerWorkspace = OrchestratorWorkspace.ForDirectory(writerRoot);
            var args = new[] { "next", "abc10000", flag, "observe" };
            var writerRepository = new SqliteOrchestratorStateRepository(writerWorkspace.SqliteStatePath);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var writerOutput = CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
                args, writerRepository, writerWorkspace, ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
                skipReadOnlyRoute: true)));

            // A read-only connection materializes and retains the WAL sidecars before attributes change.
            using var sidecarReader = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = workspace.SqliteStatePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            sidecarReader.Open();
            using (var command = sidecarReader.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM goals";
                Assert.Equal(1L, command.ExecuteScalar());
            }
            var databaseFiles = Directory.GetFiles(workspace.OrchestratorDirectory, "state.db*");
            Assert.Contains(workspace.SqliteStatePath, databaseFiles);
            Assert.Contains(workspace.SqliteStatePath + "-wal", databaseFiles);
            Assert.Contains(workspace.SqliteStatePath + "-shm", databaseFiles);
            foreach (var file in databaseFiles)
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            Assert.All(databaseFiles, file => Assert.True(
                File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)));

            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var result = ExecuteReadOnly(args, repository, workspace);

            Assert.True(result.Served);
            Assert.False(result.Changed);
            Assert.Equal(TargetId, result.Goal?.Id);
            Assert.Contains("Goal abc10000 ", result.Output, StringComparison.Ordinal);
            Assert.Equal(NextActionsSection(writerOutput), NextActionsSection(result.Output));
        }
        finally
        {
            DeleteWorkspace(root);
            DeleteWorkspace(writerRoot);
        }
    }

    [Theory]
    [InlineData("--autonomy")]
    [InlineData("--autonomy-policy")]
    public async Task ServedAutonomyForm_CreatesAndChangesNoWorkspaceFile(string flag)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = await SeedWorkspaceAsync(root);
            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var databaseBytes = File.ReadAllBytes(workspace.SqliteStatePath);
            var collaborationDatabasePath = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
            var collaborationDatabaseBytes = File.ReadAllBytes(collaborationDatabasePath);
            var before = SnapshotFiles(root, workspace.SqliteStatePath, collaborationDatabasePath);

            var result = ExecuteReadOnly(["next", "abc10000", flag, "observe"], repository, workspace);

            Assert.True(result.Served);
            Assert.False(result.Changed);
            Assert.Equal(before, SnapshotFiles(root, workspace.SqliteStatePath, collaborationDatabasePath));
            Assert.Equal(databaseBytes, File.ReadAllBytes(workspace.SqliteStatePath));
            Assert.Equal(collaborationDatabaseBytes, File.ReadAllBytes(collaborationDatabasePath));
        }
        finally
        {
            DeleteWorkspace(root);
        }
    }

    [Theory]
    [InlineData("--autonomy")]
    [InlineData("--autonomy-policy")]
    public void AutonomyForm_LoadsOnlyTargetWithoutWriterEffects(string flag)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(TargetId, "Healthy autonomy query");
            kernel.CreateGoal(new GoalId("abc20000aaaaaaaaaaaaaaaaaaaaaaaa"), "Unrelated goal");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var result = ExecuteReadOnly(["next", "abc10000", flag, "observe"], repository, workspace);

            Assert.True(result.Served);
            Assert.False(result.Changed);
            Assert.Equal(TargetId, result.Goal?.Id);
            AssertTargetLoad(repository);
            AssertNoWriterEffects(repository);
        }
        finally
        {
            DeleteWorkspace(root);
        }
    }

    [Theory]
    [InlineData("--autonomy")]
    [InlineData("--autonomy-policy")]
    public void ConcreteSweepCommand_DeclinesWithoutWriterEffects(string flag)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Live dispatch", AgentRole.Developer);
            var goal = kernel.CreateGoal(TargetId, "Terminal live dispatch", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var startedAt = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("codex-cli", "codex exec prompt.md", root, startedAt));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                new TaskProcessRecord(Environment.ProcessId, "codex exec prompt.md", root,
                    Path.Combine(root, "stdout.log"), Path.Combine(root, "stderr.log"),
                    Path.Combine(root, "exit.txt"), startedAt, null, null));
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(item => item.Id == TargetId.Value
                    ? item with { Status = GoalStatus.Completed } : item).ToArray()
            });
            Assert.Contains(TerminalGoalSweep.Diagnose(kernel, root, workspace.IntegrationBranch, TargetId).Goals.Single().Blockers,
                blocker => blocker.Kind == "terminal-live-dispatch" &&
                    blocker.Command == "refresh-dispatch abc10000 1");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var result = ExecuteReadOnly(["next", "abc10000", flag, "observe"], repository, workspace);

            Assert.False(result.Served);
            Assert.False(result.Changed);
            Assert.Empty(result.Output);
            AssertTargetLoad(repository);
            AssertNoWriterEffects(repository);
        }
        finally
        {
            DeleteWorkspace(root);
        }
    }

    [Theory]
    [InlineData("--autonomy")]
    [InlineData("--autonomy-policy")]
    public async Task PersistedSweepBlocker_DeclinesWithoutWriterEffects(string flag)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(TargetId, "Persisted sweep blocker");
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await store.RaiseAsync(CollaborationItemType.Decision, TargetId.Value,
                "Stale sweep blocker", "Needs writer reconciliation",
                $"terminal-sweep-blocker:{TargetId.Value}:stale");
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var result = ExecuteReadOnly(["next", "abc10000", flag, "observe"], repository, workspace);

            Assert.False(result.Served);
            Assert.False(result.Changed);
            Assert.Empty(result.Output);
            AssertTargetLoad(repository);
            AssertNoWriterEffects(repository);
        }
        finally
        {
            DeleteWorkspace(root);
        }
    }

    [Theory]
    [InlineData("next", "abc10000", "--autonomy", "observe")]
    [InlineData("NEXT", "ABC10000", "--AUTONOMY-POLICY", "observe")]
    [InlineData("next", "abc10000", "--autonomy", "bogus")]
    public void ExactAutonomyForms_SelectReadOnlyRoute(params string[] args)
    {
        Assert.True(CliNextAutonomyQueryCommand.IsNextAutonomyQueryCommand(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    [Theory]
    [InlineData("next", "--autonomy", "observe")]
    [InlineData("next", "--autonomy", "observe", "abc10000")]
    [InlineData("next", "abc10000", "--autonomy=observe")]
    [InlineData("next", "abc10000", "--autonomy", "observe", "--full")]
    [InlineData("next", "abc10000", "--autonomy", "--help")]
    [InlineData("next", "abc10000", "--autonomy-policy", "-h")]
    [InlineData("next", "abc10000", "--autonomy", "")]
    [InlineData("next", "abc10000", "--autonomy-policy", " ")]
    [InlineData("next", "", "--autonomy", "observe")]
    [InlineData("next", " ", "--autonomy-policy", "observe")]
    [InlineData("next", "-prefix", "--autonomy", "observe")]
    [InlineData("next", "abc10000", "--other", "observe")]
    public void OtherAutonomyForms_KeepWriterRoute(params string[] args)
    {
        Assert.False(CliNextAutonomyQueryCommand.IsNextAutonomyQueryCommand(args));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    private static async Task<OrchestratorWorkspace> SeedWorkspaceAsync(string root)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(TargetId, "Healthy autonomy query");
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        return workspace;
    }

    private static (bool Served, bool Changed, string Output, Goal? Goal) ExecuteReadOnly(
        string[] args, ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace)
    {
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var served = false;
        var changed = false;
        var output = CaptureConsole(() => served = CliReadOnlyCommandRunner.TryExecute(
            args, repository, workspace, new InMemoryModelProviderRegistry([]), null,
            ref agents, ref profiles, ref currentGoal, out changed));
        return (served, changed, output, currentGoal);
    }

    private static void AssertTargetLoad(ProbeStateRepository repository)
    {
        Assert.Equal(1, repository.ListGoalMetadataCount);
        Assert.Equal(1, repository.LoadGoalsCount);
        Assert.Equal([TargetId.Value], repository.LoadedGoalIds);
    }

    private static void AssertNoWriterEffects(ProbeStateRepository repository)
    {
        Assert.Equal(0, repository.FullLoadAttempts);
        Assert.Equal(0, repository.MutationAttempts);
        Assert.Equal(0, repository.SaveAttempts);
        Assert.Equal(0, repository.MergeSaveAttempts);
        Assert.Equal(0, repository.ListOutboxMessagesCount);
        Assert.Equal(0, repository.OutboxClaimAttempts);
        Assert.NotEmpty(repository.ObservedWriteOperationTags);
        Assert.All(repository.ObservedWriteOperationTags, tag => Assert.Null(tag));
        Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }

    private static string NextActionsSection(string output)
    {
        Assert.Contains("Goal abc10000 ", output, StringComparison.Ordinal);
        var start = output.IndexOf($"Next actions:{Environment.NewLine}", StringComparison.Ordinal);
        Assert.True(start >= 0, "The next-actions section was not printed.");
        return output[start..];
    }

    private static (string Path, long Length, DateTime LastWriteTimeUtc)[] SnapshotFiles(
        string root, params string[] readOnlyDatabasePaths) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            // Read-only WAL opens may touch sidecars; main databases and all other files remain covered.
            .Where(path => !readOnlyDatabasePaths.Any(databasePath =>
                path.Equals(databasePath + "-wal", StringComparison.OrdinalIgnoreCase) ||
                path.Equals(databasePath + "-shm", StringComparison.OrdinalIgnoreCase)))
            .Select(path => new FileInfo(path))
            .Select(file => (Path.GetRelativePath(root, file.FullName), file.Length, file.LastWriteTimeUtc))
            .OrderBy(file => file.Item1, StringComparer.Ordinal).ToArray();

    private static void CopyWorkspace(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
    }

    private static void DeleteWorkspace(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, recursive: true);
    }
}
