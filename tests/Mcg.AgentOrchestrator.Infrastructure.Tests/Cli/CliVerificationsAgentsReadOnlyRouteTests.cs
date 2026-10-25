using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns its stores and uses AsyncLocal console capture.
public sealed class CliVerificationsAgentsReadOnlyRouteTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();
    private const string GoalIdValue = "abc10000aaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("verifications", "abc10000", "1")]
    [InlineData("verifications", "--goal", "abc10000", "1")]
    [InlineData("verifications", "1")]
    [InlineData("agents")]
    [InlineData("VERIFICATIONS", "--goal", "abc10000", "1")]
    [InlineData("AGENTS")]
    public async Task ServedShapes_ReadAndWriterRoutes_PrintIdenticalOutput(params string[] args)
    {
        var (workspace, kernel) = await SeedAsync();
        var probe = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };

        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var readOutput = ExecuteRead(args, workspace, probe, expectedHandled: true);
        AssertNoWriterEffects(probe);
        if (args[0].Equals("agents", StringComparison.OrdinalIgnoreCase))
        {
            AssertNoRepositoryAccess(probe);
            Assert.Contains("Agents:", readOutput);
        }
        else
        {
            Assert.Contains(GoalIdValue, probe.LoadedGoalIds);
            Assert.Contains("command: review", readOutput);
        }

        var operations = new List<string>();
        var writerOutput = ExecuteWriter(args, workspace, operations);

        Assert.Equal(writerOutput, readOutput);
        Assert.Contains($"cli:{args[0].ToLowerInvariant()}/TransactWithOutboxAsync", operations);
        Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }

    [Theory]
    [InlineData("verifications")]
    [InlineData("verifications", "--goal", "abc10000")]
    [InlineData("verifications", "abc10000", "9")]
    [InlineData("verifications", "--goal", "zzz99999", "1")]
    public async Task VerificationsErrors_ReadAndWriterRoutes_PropagateIdenticalExceptions(
        params string[] args)
    {
        var (workspace, kernel) = await SeedAsync();
        var probe = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var readError = Assert.ThrowsAny<Exception>(() =>
            ExecuteRead(args, workspace, probe, expectedHandled: true));
        var operations = new List<string>();
        var writerError = Assert.ThrowsAny<Exception>(() => ExecuteWriter(args, workspace, operations));

        Assert.Equal(writerError.GetType(), readError.GetType());
        Assert.Equal(writerError.Message, readError.Message);
        AssertNoWriterEffects(probe);
        Assert.Contains("cli:verifications/TransactWithOutboxAsync", operations);
    }

    [Theory]
    [InlineData("agents", "extra")]
    [InlineData("agents", "--help")]
    [InlineData("agents", "--json")]
    [InlineData("agent-add", "Developer", "codex", "gpt-5")]
    public void OtherShapes_QueryRoute_DeclinesWithoutRepositoryAccess(params string[] args)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var probe = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };

        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Empty(ExecuteRead(args, workspace, probe, expectedHandled: false));
        AssertNoRepositoryAccess(probe);
    }

    [Fact]
    public void AgentAdd_Capability_RemainsExecution() =>
        Assert.Equal(CliCommandCapability.Execution,
            CliCommandCapabilities.Classify(["agent-add", "Developer", "codex", "gpt-5"]));

    private async Task<(OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel)> SeedAsync()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(new TaskId("abc10000dddddddddddddddddddddddd"),
            "Read route task", AgentRole.Developer);
        kernel.CreateGoal(new GoalId(GoalIdValue), "Read route goal", [task]);
        var snapshot = kernel.ExportSnapshot();
        var goal = snapshot.Goals.Single();
        var verification = new TaskVerificationSnapshot("review", "", 0, "", "",
            new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goal with { Tasks = [goal.Tasks.Single() with { VerificationHistory = [verification] }] }]
        });
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        return (workspace, kernel);
    }

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

    private static string ExecuteWriter(string[] args, OrchestratorWorkspace workspace, List<string> operations)
    {
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath, null,
            new SqliteWriteTelemetryOptions
            {
                HoldDurationSource = operation =>
                {
                    operations.Add(operation);
                    return () => TimeSpan.Zero;
                },
                MirrorToConductEventStream = false
            });
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        // Match the writer fallback's startup hydration and current-goal selection.
        var current = OrchestratorEntityResolver.GetLatestGoal(repository.LoadAsync().GetAwaiter().GetResult());
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles,
            ref current, skipReadOnlyRoute: true)));
    }

    private static void AssertNoWriterEffects(ProbeStateRepository probe)
    {
        Assert.Equal(0, probe.FullLoadAttempts);
        Assert.Equal(0, probe.MutationAttempts);
        Assert.Equal(0, probe.SaveAttempts);
        Assert.Equal(0, probe.MergeSaveAttempts);
        Assert.Equal(0, probe.ListOutboxMessagesCount);
        Assert.Equal(0, probe.OutboxClaimAttempts);
        Assert.All(probe.ObservedWriteOperationTags, tag => Assert.Null(tag));
        Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }

    private static void AssertNoRepositoryAccess(ProbeStateRepository probe)
    {
        AssertNoWriterEffects(probe);
        Assert.Empty(probe.LoadedGoalIds);
        Assert.Empty(probe.ObservedWriteOperationTags);
        Assert.Equal(0, probe.LoadGoalsCount);
        Assert.Equal(0, probe.LoadGoalCount);
        Assert.Equal(0, probe.ListGoalMetadataCount);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
