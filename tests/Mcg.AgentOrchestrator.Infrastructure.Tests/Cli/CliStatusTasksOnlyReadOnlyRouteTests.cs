using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns its workspace and stores; console capture is async-local.
public sealed class CliStatusTasksOnlyReadOnlyRouteTests : CliTaskQueryTestSupport
{
    private static readonly GoalId TargetId = new("abc10000aaaaaaaaaaaaaaaaaaaaaaaa");

    public static IEnumerable<object[]> TasksOnlyForms() =>
    [
        [new[] { "status", "abc10000", "--tasks-only" }],
        [new[] { "status", "--tasks-only", "abc10000" }],
        [new[] { "status", "--tasks-only" }],
        [new[] { "STATUS", "--TASKS-ONLY", "ABC10000" }]
    ];

    [Theory]
    [MemberData(nameof(TasksOnlyForms))]
    public void TasksOnlyForms_SelectReadOnlyRoute(string[] args)
    {
        Assert.True(CliStatusTasksOnlyForm.IsForm(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
    }

    [Theory]
    [InlineData("status", "abc10000", "--tasks-only", "--tasks-only")]
    [InlineData("status", "abc10000", "extra", "--tasks-only")]
    [InlineData("status", "--help")]
    [InlineData("status", "-h")]
    [InlineData("status", "--tasks-only", "--help")]
    [InlineData("status", "--tasks-only", "-h")]
    [InlineData("status", "abc10000", "--tasks-only", "--other")]
    [InlineData("status", "--tasks-only", "--other")]
    [InlineData("status", "--tasks-only", " ")]
    [InlineData("status", "--tasks-only", "")]
    [InlineData("monitor", "abc10000", "--tasks-only")]
    public void OtherForms_DeclineWithoutRepositoryEffects(params string[] args)
    {
        Assert.False(CliStatusTasksOnlyForm.IsForm(args));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var result = ExecuteReadOnly(args, repository, OrchestratorWorkspace.ForDirectory(root));

            Assert.False(result.Served);
            Assert.False(result.Changed);
            Assert.Empty(result.Output);
            Assert.Equal(0, repository.ListGoalMetadataCount);
            Assert.Equal(0, repository.LoadGoalsCount);
            Assert.Empty(repository.LoadedGoalIds);
            AssertNoWriterEffects(repository, expectReads: false);
        }
        finally
        {
            DeleteWorkspace(root);
        }
    }

    [Theory]
    [MemberData(nameof(TasksOnlyForms))]
    public async Task ReadOnlyDatabase_TasksOnlyForms_MatchWriterOutput(string[] args)
    {
        var root = CreateTempDirectory();
        var writerRoot = CreateTempDirectory();
        try
        {
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var workspace = await SeedWorkspaceAsync(root);
            CopyWorkspace(root, writerRoot);
            var writerWorkspace = OrchestratorWorkspace.ForDirectory(writerRoot);
            var writerRepository = new SqliteOrchestratorStateRepository(writerWorkspace.SqliteStatePath);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var writerOutput = CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
                args, writerRepository, writerWorkspace, ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
                skipReadOnlyRoute: true)));

            using var stateReader = OpenSidecarReader(workspace.SqliteStatePath);
            var collaborationPath = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
            using var collaborationReader = OpenSidecarReader(collaborationPath);
            MakeDatabasesReadOnly(workspace, collaborationPath);
            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var result = ExecuteReadOnly(args, repository, workspace);

            Assert.True(result.Served);
            Assert.False(result.Changed);
            Assert.Equal(TargetId, result.Goal?.Id);
            Assert.Equal(TasksOnlySection(writerOutput), TasksOnlySection(result.Output));
            Assert.Contains("Status task", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteWorkspace(root);
            DeleteWorkspace(writerRoot);
        }
    }

    [Theory]
    [MemberData(nameof(TasksOnlyForms))]
    public async Task ServedTasksOnlyForm_CreatesAndChangesNoWorkspaceFile(string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var workspace = await SeedWorkspaceAsync(root);
            var collaborationPath = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
            using var stateReader = OpenSidecarReader(workspace.SqliteStatePath);
            using var collaborationReader = OpenSidecarReader(collaborationPath);
            MakeDatabasesReadOnly(workspace, collaborationPath);
            var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var databaseBytes = File.ReadAllBytes(workspace.SqliteStatePath);
            var collaborationBytes = File.ReadAllBytes(collaborationPath);
            var before = SnapshotFiles(root, workspace.SqliteStatePath, collaborationPath);

            var result = ExecuteReadOnly(args, repository, workspace);

            Assert.True(result.Served);
            Assert.False(result.Changed);
            Assert.Equal(TargetId, result.Goal?.Id);
            Assert.Equal(before, SnapshotFiles(root, workspace.SqliteStatePath, collaborationPath));
            Assert.Equal(databaseBytes, File.ReadAllBytes(workspace.SqliteStatePath));
            Assert.Equal(collaborationBytes, File.ReadAllBytes(collaborationPath));
        }
        finally
        {
            DeleteWorkspace(root);
        }
    }

    [Theory]
    [MemberData(nameof(TasksOnlyForms))]
    public void TasksOnlyForm_LoadsOnlyTargetWithoutWriterEffects(string[] args)
    {
        var root = CreateTempDirectory();
        try
        {
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(TargetId, "Tasks-only query", [new TaskSpec(TaskId.New(), "Status task", AgentRole.Developer)]);
            kernel.CreateGoal(new GoalId("abc20000aaaaaaaaaaaaaaaaaaaaaaaa"), "Unrelated older goal");
            var created = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal =>
                {
                    var creationTime = created.AddDays(goal.Id == TargetId.Value ? 1 : 0);
                    return goal with
                    {
                        CreatedAt = creationTime,
                        Timeline = [goal.Timeline[0] with { OccurredAt = creationTime }]
                    };
                }).ToArray()
            });
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

            var result = ExecuteReadOnly(args, repository, workspace);

            Assert.True(result.Served);
            Assert.False(result.Changed);
            Assert.Equal(TargetId, result.Goal?.Id);
            Assert.Equal(1, repository.ListGoalMetadataCount);
            Assert.Equal(1, repository.LoadGoalsCount);
            Assert.Equal([TargetId.Value], repository.LoadedGoalIds);
            AssertNoWriterEffects(repository);
        }
        finally
        {
            DeleteWorkspace(root);
        }
    }

    private static async Task<OrchestratorWorkspace> SeedWorkspaceAsync(string root)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(TargetId, "Tasks-only query", [new TaskSpec(TaskId.New(), "Status task", AgentRole.Developer)]);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        return workspace;
    }

    private static (bool Served, bool Changed, string Output, Goal? Goal) ExecuteReadOnly(
        string[] args, ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace)
    {
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

    private static void AssertNoWriterEffects(ProbeStateRepository repository, bool expectReads = true)
    {
        Assert.Equal(0, repository.FullLoadAttempts);
        Assert.Equal(0, repository.MutationAttempts);
        Assert.Equal(0, repository.SaveAttempts);
        Assert.Equal(0, repository.MergeSaveAttempts);
        Assert.Equal(0, repository.ListOutboxMessagesCount);
        Assert.Equal(0, repository.OutboxClaimAttempts);
        if (expectReads)
            Assert.NotEmpty(repository.ObservedWriteOperationTags);
        else
            Assert.Empty(repository.ObservedWriteOperationTags);
        Assert.All(repository.ObservedWriteOperationTags, tag => Assert.Null(tag));
        Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }

    private static SqliteConnection OpenSidecarReader(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
            Assert.True((long)command.ExecuteScalar()! > 0);
            Assert.True(File.Exists(databasePath + "-wal"));
            Assert.True(File.Exists(databasePath + "-shm"));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void MakeDatabasesReadOnly(OrchestratorWorkspace workspace, string collaborationPath)
    {
        string[] files = [workspace.SqliteStatePath, workspace.SqliteStatePath + "-wal",
            workspace.SqliteStatePath + "-shm", collaborationPath];
        foreach (var file in files)
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        Assert.All(files, file => Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)));
    }

    private static string TasksOnlySection(string output)
    {
        var header = $"Goal {TargetId.Value}";
        Assert.Contains(header, output, StringComparison.Ordinal);
        var start = output.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start == 0 || output[start - 1] == '\n', "The goal header must start a line.");
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
