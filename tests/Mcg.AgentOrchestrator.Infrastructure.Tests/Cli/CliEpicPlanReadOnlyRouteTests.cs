using System.Globalization;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns its stores and uses AsyncLocal console capture.
public sealed class CliEpicPlanReadOnlyRouteTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = CreateTempDirectory();
    private const string MemberId = "abc10000aaaaaaaaaaaaaaaaaaaaaaaa";
    // AgentReadEnvelope.Serialize stamps only this field; the payload has no clock-derived ages or durations.
    private const string GeneratedAtField = "generatedAt";

    [Theory]
    [InlineData("epic-plan", "Board")]
    [InlineData("epic-plan", "Board", "--json")]
    public async Task ExactShapes_ReadAndWriterRoutes_PrintIdenticalPlans(params string[] args)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var workspace = await SeedAsync();
        var probe = NewReadProbe();

        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var readOutput = ExecuteRead(args, workspace, probe, expectedHandled: true);
        AssertNoRepositoryAccess(probe);
        Assert.Contains("Planned slice", readOutput);
        Assert.Contains("Read the probe", readOutput);
        Assert.Contains("Keep the query read-only", readOutput);
        Assert.Contains(MemberId, readOutput);

        var operations = new List<string>();
        var writerOutput = ExecuteWriter(args, workspace, operations);
        var endedAt = DateTimeOffset.UtcNow;

        if (args.Length == 3)
        {
            Assert.Equal(
                NormalizeClockFields(writerOutput, startedAt, endedAt),
                NormalizeClockFields(readOutput, startedAt, endedAt));
        }
        else
        {
            Assert.Equal(writerOutput, readOutput);
        }
        Assert.Contains("cli:epic-plan/TransactWithOutboxAsync", operations);
        Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }

    private static string NormalizeClockFields(string output, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var document = Assert.IsType<JsonObject>(JsonNode.Parse(output));
        var generatedAt = document[GeneratedAtField]!.GetValue<string>();
        Assert.True(DateTimeOffset.TryParseExact(generatedAt, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp), $"Invalid {GeneratedAtField}: {generatedAt}");
        Assert.InRange(timestamp, startedAt, endedAt);
        document[GeneratedAtField] = "<generated-at>";
        return document.ToJsonString();
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    [InlineData("ambiguous", false)]
    [InlineData("ambiguous", true)]
    [InlineData("missing-portfolio", false)]
    [InlineData("missing-portfolio", true)]
    public async Task EpicErrors_ReadAndWriterRoutes_PropagateIdenticalExceptions(
        string scenario, bool json)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var reference = "Board";
        if (scenario != "missing-portfolio")
        {
            var store = new PortfolioStore(workspace.PortfolioStorePath);
            await store.AddEpicAsync("Board");
            if (scenario == "ambiguous")
                await store.AddEpicAsync("Board");
            else
                reference = "missing";
        }
        string[] args = json ? ["epic-plan", reference, "--json"] : ["epic-plan", reference];
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        var probe = NewReadProbe();
        var readError = Assert.Throws<InvalidOperationException>(() =>
            ExecuteRead(args, workspace, probe, expectedHandled: true));
        var operations = new List<string>();
        var writerError = Assert.Throws<InvalidOperationException>(() =>
            ExecuteWriter(args, workspace, operations));

        Assert.Equal(writerError.GetType(), readError.GetType());
        Assert.Equal(writerError.Message, readError.Message);
        Assert.Contains(scenario == "ambiguous" ? "is ambiguous" : "was not found", readError.Message);
        AssertNoRepositoryAccess(probe);
        Assert.Contains("cli:epic-plan/TransactWithOutboxAsync", operations);
    }

    [Theory]
    [InlineData("epic-plan")]
    [InlineData("epic-plan", "Board", "extra")]
    [InlineData("epic-plan", "Board", "--json", "--json")]
    [InlineData("epic-plan", "Board", "--JSON")]
    [InlineData("EPIC-PLAN", "Board")]
    [InlineData("epic-plan", "Board", "--help")]
    [InlineData("epic-plan", "--json")]
    [InlineData("epic-plan-add", "Board", "--backlog", "abc")]
    [InlineData("epic-plan", "")]
    [InlineData("epic-plan", " ")]
    [InlineData("epic-plan", "--help")]
    [InlineData("epic-plan", "--other", "--json")]
    [InlineData("epic-plan", "Board", "--other")]
    public void OtherShapes_QueryRoute_DeclinesWithoutRepositoryAccess(params string[] args)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var probe = NewReadProbe();

        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Empty(ExecuteRead(args, workspace, probe, expectedHandled: false));
        AssertNoRepositoryAccess(probe);
    }

    [Fact]
    public void PlanAdd_Capability_RemainsExecution() =>
        Assert.Equal(CliCommandCapability.Execution,
            CliCommandCapabilities.Classify(["epic-plan-add", "Board", "--backlog", "abc"]));

    private async Task<OrchestratorWorkspace> SeedAsync()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Board");
        var slice = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Planned slice");
        var plan = new EpicPlanStore(workspace.PortfolioStorePath);
        await plan.AddSliceAsync(epic.Id, slice.Id);
        await plan.AddStepAsync(epic.Id, "Read the probe");
        await plan.AddDecisionAsync(epic.Id, "Keep the query read-only", "operator");
        await portfolio.AssignGoalToEpicAsync(MemberId, epic.Id);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId(MemberId), "Unplanned member");
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        var view = EpicProgressReadModel.LoadEpic(workspace, "Board", includeReasons: true).Plan!;
        Assert.Equal(2, view.Items.Count);
        Assert.Single(view.Plan.Decisions);
        Assert.Equal(MemberId, Assert.Single(view.UnplannedMembers).MemberId);
        return workspace;
    }

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
        Goal? current = null;
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles,
            ref current, skipReadOnlyRoute: true)));
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
