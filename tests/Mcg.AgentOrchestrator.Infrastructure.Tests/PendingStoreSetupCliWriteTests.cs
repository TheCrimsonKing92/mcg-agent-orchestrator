using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: isolated stores, lease handles and an explicit child-only environment.
public sealed class PendingStoreSetupCliWriteTests : CliCommandTestBase
{
    [Fact]
    public void EnsureState_PendingWithLiveOwner_NamesStoreAndSupportedRemedy()
    {
        using var fixture = new Fixture();
        var pending = RollBackState(fixture.Workspace);
        using var owner = ConductorLoopLease.Acquire(fixture.Workspace.OrchestratorDirectory);
        var before = fixture.Snapshot();

        var error = Assert.Throws<InvalidOperationException>(() => WorkspaceStoreSetup.EnsureState(fixture.Workspace));

        AssertPendingMessage(error.Message, pending);
        Assert.Contains($"pid {Environment.ProcessId}", error.Message);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Run_PendingPortfolioWithLiveOwner_RefusesWithoutWrites()
    {
        using var fixture = new Fixture();
        Execute(fixture.Workspace.PortfolioStorePath, "UPDATE store_schema_versions SET version = version - 1;");
        var pending = Pending(fixture.Workspace, StoreSchemaRegistry.Portfolio.StoreName);
        using var owner = ConductorLoopLease.Acquire(fixture.Workspace.OrchestratorDirectory);
        var before = fixture.Snapshot();

        var error = Assert.Throws<InvalidOperationException>(() => WorkspaceStoreSetup.Run(fixture.Workspace));

        AssertPendingMessage(error.Message, pending);
        Assert.Equal(before, fixture.Snapshot());
    }

    public static TheoryData<string[], bool> VerbCases => new()
    {
        { ["adjudicate"], true },
        { ["retry"], true },
        { ["progress"], true },
        { ["verify-manual"], true },
        { ["criterion-evidence-record"], true },
        { ["criterion-evidence-repair"], true },
        { ["backlog-add", "--depends-on", "x"], true },
        { ["goals"], false },
        { ["conduct", "--loop"], false },
        { ["acceptance"], false },
        { ["conductor", "setup"], false },
        { [], false }
    };

    [Theory]
    [MemberData(nameof(VerbCases))]
    public void AllowsWhilePending_VerbClassification_MatchesStoreIndependentCommands(string[] args, bool expected)
    {
        Assert.Equal(expected, PendingStoreSetupGate.AllowsWhilePending(args));
    }

    [Fact]
    public async Task Adjudicate_PendingStateWithLiveOwner_QueuesIntentWithoutOtherWrites()
    {
        // Run the actual Program entry with only explicitly inherited OS/runtime variables.
        using var fixture = new Fixture(CreateRefinedWorkspace(CreateTempDirectory()));
        var workspace = fixture.Workspace;
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Queue during schema upgrade", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, goal.Tasks.Single().Id, WorkTaskStatus.Failed, "failed");
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        var textFile = Path.Combine(workspace.ExecutionDirectory, "adjudication.txt");
        File.WriteAllText(textFile, "Close using operator evidence.");
        var pending = RollBackState(workspace);
        using var owner = ConductorLoopLease.Acquire(workspace.OrchestratorDirectory);
        Assert.True(ConductorLoopLease.IsActive(workspace.OrchestratorDirectory));
        var before = StoreSnapshot(workspace);

        var result = await RunCliAsync(workspace.ExecutionDirectory,
            ["adjudicate", "1", "close", "--text-file", textFile, "--evidence", "receipt-1"]);

        Assert.True(result.ExitCode == 0, $"CLI exit {result.ExitCode}: {result.StandardError}");
        Assert.DoesNotContain("state-migrations", result.StandardError);
        var intent = Assert.Single(await SqliteOperatorIntentStore.OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .ListForGoalAsync(goal.Id.Value));
        Assert.Equal(OperatorIntentVerbs.Adjudicate, intent.Verb);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal(before, StoreSnapshot(workspace));
        Assert.Equal(pending, Pending(workspace, "state-migrations"));
    }

    [Fact]
    public void WritableStartup_PendingStateWithLiveOwner_StillRefuses()
    {
        using var fixture = new Fixture();
        var pending = RollBackState(fixture.Workspace);
        using var owner = ConductorLoopLease.Acquire(fixture.Workspace.OrchestratorDirectory);
        var before = File.ReadAllBytes(fixture.Workspace.SqliteStatePath);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ProgramStartupLifecycle.EnsureStateDbInitialized(["goals"], fixture.Workspace));

        AssertPendingMessage(error.Message, pending);
        Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.SqliteStatePath));
    }

    [Fact]
    public void EnsureState_DeadOwner_TakesOverAndMigrates()
    {
        using var fixture = new Fixture();
        RollBackState(fixture.Workspace);
        var lockPath = Path.Combine(fixture.Workspace.OrchestratorDirectory, "conduct-loop.lock");
        File.WriteAllLines(lockPath, [int.MaxValue.ToString(CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)]);
        Assert.False(ConductorLoopLease.IsActive(fixture.Workspace.OrchestratorDirectory));

        WorkspaceStoreSetup.EnsureState(fixture.Workspace);

        Assert.True(StateDbMigrations.IsUpToDate(fixture.Workspace.SqliteStatePath));
        Assert.False(File.Exists(lockPath));
    }

    [Theory]
    [InlineData("operator-intents", "adjudicate")]
    [InlineData("backlog", "backlog-add")]
    [InlineData("portfolio", "backlog-add")]
    public void Deferral_OwnStorePending_RefusesInsteadOfBypassing(string storeName, string verb)
    {
        using var fixture = new Fixture();
        RollBackState(fixture.Workspace);
        var entry = StoreSetupRunner.Plan(fixture.Workspace.OrchestratorDirectory, includeState: true)
            .Single(item => item.StoreName == storeName);
        Execute(entry.DatabasePath, $"UPDATE store_schema_versions SET version = version - 1 WHERE store_name = '{storeName}';");
        using var owner = ConductorLoopLease.Acquire(fixture.Workspace.OrchestratorDirectory);
        Assert.False(WorkspaceStoreSetup.DefersStateUpgradeToLiveOwner(fixture.Workspace, [verb]));
        var before = fixture.Snapshot();

        var error = Assert.Throws<InvalidOperationException>(() => WorkspaceStoreSetup.EnsureState(fixture.Workspace));

        AssertPendingMessage(error.Message, Pending(fixture.Workspace, storeName));
        Assert.Contains("state-migrations", error.Message);
        Assert.Equal(before, fixture.Snapshot());
    }

    private static StoreSetupPlanEntry RollBackState(OrchestratorWorkspace workspace)
    {
        // Migration 14 introduces promotion facts; simulate the previous landed runtime.
        Execute(workspace.SqliteStatePath, "DROP TABLE goal_promotion_facts; DELETE FROM schema_migrations WHERE migration_number = 14;");
        return Pending(workspace, "state-migrations");
    }

    private static StoreSetupPlanEntry Pending(OrchestratorWorkspace workspace, string storeName)
    {
        var entry = StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true)
            .Single(item => item.StoreName == storeName);
        Assert.True(entry.RequiresMigration);
        Assert.Equal(entry.RequiredVersion - 1, entry.RecordedVersion);
        return entry;
    }

    private static void AssertPendingMessage(string message, StoreSetupPlanEntry entry)
    {
        Assert.Contains(entry.StoreName, message);
        Assert.Contains(entry.DatabasePath, message);
        Assert.Contains($"recorded {entry.RecordedVersion}", message);
        Assert.Contains($"required {entry.RequiredVersion}", message);
        Assert.Contains("conductor setup --plan", message);
        Assert.Contains("conductor stop", message);
        Assert.Contains("conductor setup", message);
        Assert.Contains("conductor start --clear-stop", message);
        Assert.DoesNotContain("to proceed", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remove the lock", message, StringComparison.OrdinalIgnoreCase);
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string[] StoreSnapshot(OrchestratorWorkspace workspace) =>
        StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true)
            .Where(entry => entry.StoreName != StoreSchemaRegistry.OperatorIntents.StoreName)
            .Select(entry => entry.DatabasePath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
            .Select(path => path + ":" + HashFile(path)).ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        internal OrchestratorWorkspace Workspace { get; }

        internal Fixture(OrchestratorWorkspace? workspace = null)
        {
            _root = workspace?.ExecutionDirectory ?? CreateTempDirectory();
            Workspace = workspace ?? OrchestratorWorkspace.ForDirectory(_root, null, null,
                new OrchestratorProjectRegistry(Path.Combine(_root, "registry"), Path.Combine(_root, "data")));
            WorkspaceStoreSetup.Run(Workspace);
        }

        internal string[] Snapshot() => Directory.EnumerateFiles(Workspace.OrchestratorDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "conduct-loop.lock" &&
                !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => path + ":" + HashFile(path)).ToArray();

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private static async Task<CliChildProcessResult> RunCliAsync(string root, string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var name in new[]
        {
            "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
            "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT(x86)"
        })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        }
        start.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = root;
        start.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, ".orchestrator", "test-dotnet");
        return await CliChildProcessRunner.RunAsync(start);
    }
}
