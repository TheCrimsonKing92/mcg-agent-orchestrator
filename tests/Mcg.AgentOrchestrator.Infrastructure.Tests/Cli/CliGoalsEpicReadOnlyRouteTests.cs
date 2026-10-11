using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: every case owns its temp stores and uses AsyncLocal console capture.
public sealed class CliGoalsEpicReadOnlyRouteTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();
    private static readonly string[] MemberIds =
        ["abc10000aaaaaaaaaaaaaaaaaaaaaaaa", "abc20000bbbbbbbbbbbbbbbbbbbbbbbb"];

    [Theory]
    [InlineData("goals", "--epic", "Board")]
    [InlineData("goals", "--epic=Board")]
    [InlineData("GOALS", "--EPIC", "Board")]
    [InlineData("GoAlS", "--EpIc=Board")]
    public void ExactShapes_QueryRoute_PrintsMembersWithoutRepositoryAccess(params string[] args)
    {
        var workspace = Seed();
        var probe = NewReadProbe();

        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var output = ExecuteRead(args, workspace, probe, expectedHandled: true);

        Assert.Contains("First member", output);
        Assert.Contains("Second member", output);
        Assert.DoesNotContain("Unrelated goal", output);
        AssertNoRepositoryAccess(probe);
    }

    [Theory]
    [InlineData("goals", "--epic")]
    [InlineData("goals", "--epic=")]
    [InlineData("goals", "--epic", "--json")]
    [InlineData("goals", "--epic", "Board", "extra")]
    [InlineData("goals", "--json", "--epic", "Board")]
    [InlineData("goals", "--epic", "Board", "--help")]
    [InlineData("goals", "subscribe")]
    [InlineData("goals", "--epic", "")]
    [InlineData("goals", "--epic", " ")]
    [InlineData("goals", "--epic= ")]
    [InlineData("goals", "--epic=--json")]
    [InlineData("goals", "--epic=Board", "--help")]
    public void OtherShapes_QueryRoute_DeclinesWithoutRepositoryAccess(params string[] args)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var probe = NewReadProbe();

        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Empty(ExecuteRead(args, workspace, probe, expectedHandled: false));
        AssertNoRepositoryAccess(probe);
    }

    [Theory]
    [InlineData("goals", "--epic", "-x")]
    [InlineData("goals", "--epic=-x")]
    public void SingleDashReference_QueryRoute_AcceptsShape(params string[] args) =>
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

    [Fact]
    public void WriterForm_PersistentRunner_RetainsWriterScope()
    {
        var workspace = Seed();
        string[] args = ["epic-assign", "Board", "abc10000"];
        var readProbe = NewReadProbe();

        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(args));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Empty(ExecuteRead(args, workspace, readProbe, expectedHandled: false));
        AssertNoRepositoryAccess(readProbe);

        var writerProbe = new ProbeStateRepository(new AgentOrchestratorKernel());
        Assert.Throws<InvalidOperationException>(() => ExecutePersistent(args, workspace, writerProbe));
        Assert.True(writerProbe.ListOutboxMessagesCount > 0);
        Assert.Contains("cli:epic-assign", writerProbe.ObservedWriteOperationTags);
        Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }

    [Fact]
    public void ExactShapes_ReadAndWriterRoutes_PrintIdenticalMemberSummaries()
    {
        var workspace = Seed();
        var members = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            .ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(goal => MemberIds.Contains(goal.Id)).ToArray();
        Assert.Equal(2, members.Length);
        var expected = CaptureConsole(() => ConsoleViews.PrintGoals(members));
        Assert.NotEmpty(expected);

        string[][] forms = [["goals", "--epic", "Board"], ["goals", "--epic=Board"]];
        foreach (var args in forms)
        {
            var readProbe = NewReadProbe();
            var readOutput = ExecuteRead(args, workspace, readProbe, expectedHandled: true);
            var writerProbe = new ProbeStateRepository(new AgentOrchestratorKernel());
            var writerOutput = ExecutePersistent(args, workspace, writerProbe, skipReadOnlyRoute: true);

            Assert.Equal(expected, readOutput);
            Assert.Equal(readOutput, writerOutput);
            AssertNoRepositoryAccess(readProbe);
            Assert.True(writerProbe.ListOutboxMessagesCount > 0);
            Assert.Contains("cli:goals", writerProbe.ObservedWriteOperationTags);
        }
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("ambiguous", false)]
    [InlineData("ambiguous", true)]
    [InlineData("unversioned", false)]
    [InlineData("unversioned", true)]
    public void EpicErrors_ReadAndWriterRoutes_PropagateIdenticalExceptions(
        string scenario, bool inlineReference)
    {
        var workspace = Seed();
        var reference = "Board";
        switch (scenario)
        {
            case "unknown":
                reference = "missing";
                break;
            case "ambiguous":
                new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Board").GetAwaiter().GetResult();
                break;
            case "unversioned":
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = workspace.PortfolioStorePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false
                }.ToString()))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "DROP TABLE store_schema_versions";
                    command.ExecuteNonQuery();
                }
                break;
        }
        string[] args = inlineReference ? ["goals", $"--epic={reference}"] : ["goals", "--epic", reference];
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var readProbe = NewReadProbe();
        var readError = Assert.Throws<InvalidOperationException>(() =>
            ExecuteRead(args, workspace, readProbe, expectedHandled: true));
        var writerProbe = new ProbeStateRepository(new AgentOrchestratorKernel());
        var writerError = Assert.Throws<InvalidOperationException>(() =>
            ExecutePersistent(args, workspace, writerProbe, skipReadOnlyRoute: true));

        Assert.Equal(writerError.GetType(), readError.GetType());
        Assert.Equal(writerError.Message, readError.Message);
        Assert.Contains(scenario switch
        {
            "unknown" => "was not found",
            "ambiguous" => "is ambiguous",
            _ => "run setup"
        }, readError.Message);
        AssertNoRepositoryAccess(readProbe);
        Assert.True(writerProbe.ListOutboxMessagesCount > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueryRoute_ExistingOrAbsentState_PreservesAllStoreAndSiblingFiles(bool seedState)
    {
        var workspace = Seed(seedState);
        File.WriteAllText(Path.Combine(root, "unrelated.txt"), "Preserve this sibling.");
        // Only sidecars belonging to these already-existing stores may be omitted.
        var existingStores = new[] { workspace.PortfolioStorePath, workspace.SqliteStatePath }
            .Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(seedState, File.Exists(workspace.SqliteStatePath));
        var before = Snapshot(existingStores);
        var probe = NewReadProbe();

        var output = ExecuteRead(["goals", "--epic", "Board"], workspace, probe, expectedHandled: true);

        var after = Snapshot(existingStores);
        Assert.Equal(before.Keys.OrderBy(path => path, StringComparer.Ordinal),
            after.Keys.OrderBy(path => path, StringComparer.Ordinal));
        foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
        AssertNoRepositoryAccess(probe);
        if (seedState)
            Assert.NotEmpty(output);
        else
        {
            Assert.Empty(output);
            Assert.False(File.Exists(workspace.SqliteStatePath));
            Assert.False(File.Exists(workspace.SqliteStatePath + "-wal"));
            Assert.False(File.Exists(workspace.SqliteStatePath + "-shm"));
        }
    }

    private OrchestratorWorkspace Seed(bool seedState = true)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.AddEpicAsync("Board").GetAwaiter().GetResult();
        foreach (var id in MemberIds)
            store.AssignGoalToEpicAsync(id, epic.Id).GetAwaiter().GetResult();
        if (seedState)
        {
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId(MemberIds[0]), "First member");
            kernel.CreateGoal(new GoalId(MemberIds[1]), "Second member");
            kernel.CreateGoal(new GoalId("eeeeeeee111111111111111111111111"), "Unrelated goal");
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
                .SaveAsync(kernel).GetAwaiter().GetResult();
        }
        return workspace;
    }

    private Dictionary<string, string> Snapshot(HashSet<string> existingStores) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !((path.EndsWith("-wal", StringComparison.Ordinal) ||
                path.EndsWith("-shm", StringComparison.Ordinal)) && existingStores.Contains(path[..^4])))
            .ToDictionary(path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static ProbeStateRepository NewReadProbe() =>
        new(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };

    private static string ExecuteRead(string[] args, OrchestratorWorkspace workspace,
        ProbeStateRepository probe, bool expectedHandled)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() =>
        {
            var handled = CliReadOnlyCommandRunner.TryExecute(args, probe, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref current, out var changed);
            Assert.Equal(expectedHandled, handled);
            Assert.False(changed);
        });
    }

    private static string ExecutePersistent(string[] args, OrchestratorWorkspace workspace,
        ProbeStateRepository probe, bool skipReadOnlyRoute = false)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, probe, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles,
            ref current, skipReadOnlyRoute: skipReadOnlyRoute)));
    }

    private static void AssertNoRepositoryAccess(ProbeStateRepository probe)
    {
        Assert.Empty(probe.LoadedGoalIds);
        Assert.Empty(probe.ObservedWriteOperationTags);
        Assert.Equal(0, probe.FullLoadAttempts);
        Assert.Equal(0, probe.LoadGoalsCount);
        Assert.Equal(0, probe.LoadGoalCount);
        Assert.Equal(0, probe.ListGoalMetadataCount);
        Assert.Equal(0, probe.MutationAttempts);
        Assert.Equal(0, probe.SaveAttempts);
        Assert.Equal(0, probe.MergeSaveAttempts);
        Assert.Equal(0, probe.ListOutboxMessagesCount);
        Assert.Equal(0, probe.OutboxClaimAttempts);
        Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
