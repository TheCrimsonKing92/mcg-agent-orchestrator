using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalCancelLifecycleOutboxTests : CliGoalParkTestSupport
{
    private const string PendingSentence =
        "The lifecycle event delivery is pending and will be retried by the next writer-path command.";
    // Derived from main 5d57c263a's unchanged PrintGoal and cancel renderer for this fixed seed.
    private const string MainCancelOutput =
        "\nGoal aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nObjective: Cancel target\nStatus: Cancelled\nTasks:\n" +
        "  1. [Pending] Developer: Cancel task (unassigned)\n\n";

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ProjectionFailure_CommitsPendingDelivery_AnotherGoalsWriterRetries(bool stopAlias)
    {
        using var seed = await Seed.Create();
        Directory.CreateDirectory(seed.EventPath);

        var result = Cancel(seed, stopAlias);

        var error = Xunit.Assert.IsType<CliCommandHandlers.GoalLifecycleProjectionException>(result.Error);
        Xunit.Assert.Equal(
            $"Goal '{seed.GoalId.Value[..8]}' is committed Cancelled, but lifecycle event projection failed: " +
            error.InnerException!.Message + " " + PendingSentence, error.Message);
        Xunit.Assert.Equal(GoalStatus.Cancelled, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        var message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
        Xunit.Assert.NotEqual(OrchestratorStateOutboxStatus.Quarantined,
            (await seed.Repository.GetOutboxStateAsync(message.Id))!.Status);

        Directory.Delete(seed.EventPath);
        var retry = ParkOther(seed);

        Xunit.Assert.Null(retry.Error);
        Xunit.Assert.True(retry.Changed);
        AssertOneCancellation(seed, message.Id);
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
    }

    [Xunit.Fact]
    public async Task FaultBeforeCommit_RollsBackGoalVersionAndOutbox()
    {
        using var seed = await Seed.Create();
        var beforeVersion = await Version(seed);
        var fault = new InvalidOperationException("fault after goal and outbox writes");
        var repository = new SqliteOrchestratorStateRepository(seed.Workspace.SqliteStatePath)
        {
            BeforeGoalStateOutboxCommit = () => throw fault
        };

        var result = RunCommand(CancelParts(seed), repository, seed.Workspace);

        Xunit.Assert.Same(fault, result.Error);
        Xunit.Assert.Equal(string.Empty, result.Output);
        Xunit.Assert.Equal(GoalStatus.Active, (await seed.Repository.LoadGoalAsync(seed.GoalId))!.Status);
        Xunit.Assert.Equal(beforeVersion, await Version(seed));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
        Xunit.Assert.False(File.Exists(seed.EventPath));
    }

    [Xunit.Fact]
    public async Task CrashAfterAppendBeforeMark_WriterRetiresWithoutDuplicateLine()
    {
        using var seed = await Seed.Create();
        Directory.CreateDirectory(seed.EventPath);
        Xunit.Assert.IsType<CliCommandHandlers.GoalLifecycleProjectionException>(Cancel(seed).Error);
        var message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
        Directory.Delete(seed.EventPath);

        // Simulate the external append completing, with no outbox finalization.
        GoalLifecycleEventOutbox.AppendForMessage(seed.Workspace, message);
        AssertOneCancellation(seed, message.Id);
        Xunit.Assert.NotNull(await seed.Repository.GetOutboxStateAsync(message.Id));

        Xunit.Assert.Null(ParkOther(seed).Error);

        AssertOneCancellation(seed, message.Id);
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
    }

    [Xunit.Fact]
    public async Task SuccessfulCancel_MatchesMainStdoutAndInMemoryRoute_RetiresDelivery()
    {
        using var seed = await Seed.Create();
        var kernel = await seed.Repository.LoadGoalsAsync([seed.GoalId]);
        var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(seed.Root, "in-memory"));
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var memoryOutput = CaptureConsole(() => Xunit.Assert.True(CliCommandDispatcher.ExecuteCommand(
            CancelParts(seed), kernel, workspace, ref agents, new InMemoryModelProviderRegistry([]),
            ref profiles, ref currentGoal)));

        var result = Cancel(seed);

        Xunit.Assert.Null(result.Error);
        Xunit.Assert.True(result.Changed);
        Xunit.Assert.Equal(MainCancelOutput.ReplaceLineEndings(Environment.NewLine), result.Output);
        Xunit.Assert.Equal(memoryOutput, result.Output);
        AssertOneCancellation(seed);
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
    }

    [Xunit.Theory]
    [Xunit.InlineData("malformed")]
    [Xunit.InlineData("missing-goal")]
    [Xunit.InlineData("invalid-id")]
    public async Task PoisonMessage_IsQuarantinedWithWarning_WithoutEventsLine(string poison)
    {
        using var seed = await Seed.Create();
        var missingId = new GoalId("cccccccccccccccccccccccccccccccc");
        var message = GoalLifecycleEventOutbox.CreateMessage(new ProgressEvent(
            missingId, null, ProgressKind.GoalCancelled, "Poison", DateTimeOffset.UnixEpoch));
        message = poison switch
        {
            "malformed" => message with { PayloadJson = "{" },
            "invalid-id" => message with { PayloadJson = message.PayloadJson.Replace(missingId.Value, "../invalid") },
            _ => message
        };
        await seed.Repository.EnsureOutboxMessageAsync(message);
        CommandResult? result = null;

        var stderr = AsyncLocalConsoleRouter.CaptureError(() => result = ParkOther(seed));

        Xunit.Assert.Null(result!.Error);
        Xunit.Assert.True(result.Changed);
        Xunit.Assert.Contains($"Warning: goal lifecycle event outbox message '{message.Id}' was quarantined:", stderr);
        Xunit.Assert.Equal(OrchestratorStateOutboxStatus.Quarantined,
            (await seed.Repository.GetOutboxStateAsync(message.Id))!.Status);
        Xunit.Assert.Empty(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
        Xunit.Assert.False(File.Exists(EventPath(seed.Workspace, missingId)));
        Xunit.Assert.False(File.Exists(seed.EventPath));
    }

    [Xunit.Fact]
    public async Task TransientDrainFailure_StaysPendingWithWarning_NextWriterDelivers()
    {
        using var seed = await Seed.Create();
        Directory.CreateDirectory(seed.EventPath);
        Xunit.Assert.IsType<CliCommandHandlers.GoalLifecycleProjectionException>(Cancel(seed).Error);
        var message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
        CommandResult? result = null;

        var stderr = AsyncLocalConsoleRouter.CaptureError(() => result = ParkOther(seed));

        Xunit.Assert.Null(result!.Error);
        Xunit.Assert.True(result.Changed);
        Xunit.Assert.Contains($"message '{message.Id}' remains pending", stderr);
        Xunit.Assert.NotEqual(OrchestratorStateOutboxStatus.Quarantined,
            (await seed.Repository.GetOutboxStateAsync(message.Id))!.Status);
        Directory.Delete(seed.EventPath);

        // A rejected writer command still drains, because delivery precedes application.
        _ = ParkOther(seed);

        AssertOneCancellation(seed, message.Id);
        Xunit.Assert.Null(await seed.Repository.GetOutboxStateAsync(message.Id));
    }

    [Xunit.Fact]
    public async Task ReadOnlyRoute_DoesNotDrainPendingDelivery()
    {
        using var seed = await Seed.Create();
        Directory.CreateDirectory(seed.EventPath);
        Xunit.Assert.IsType<CliCommandHandlers.GoalLifecycleProjectionException>(Cancel(seed).Error);
        var message = Xunit.Assert.Single(await seed.Repository.ListOutboxMessagesAsync(GoalLifecycleEventOutbox.Kind));
        Directory.Delete(seed.EventPath);

        var result = RunCommand(["status", seed.GoalId.Value[..8]], seed.Repository, seed.Workspace);

        Xunit.Assert.Null(result.Error);
        Xunit.Assert.False(File.Exists(seed.EventPath));
        Xunit.Assert.NotNull(await seed.Repository.GetOutboxStateAsync(message.Id));
        Xunit.Assert.Null(ParkOther(seed).Error);
        AssertOneCancellation(seed, message.Id);
    }

    [Xunit.Fact]
    public async Task RejectedCancel_DoesNotWriteGoalOrOutbox()
    {
        using var seed = await Seed.Create();
        var beforeVersion = await Version(seed);

        var result = RunCommand(["cancel-goal", seed.GoalId.Value[..8], "Operator cancel"],
            seed.Repository, seed.Workspace);

        Xunit.Assert.IsType<InvalidOperationException>(result.Error);
        Xunit.Assert.Equal(beforeVersion, await Version(seed));
        Xunit.Assert.Equal(0L, await CountOutboxRows(seed));
        Xunit.Assert.False(File.Exists(seed.EventPath));
    }

    private static CommandResult Cancel(Seed seed, bool stopAlias = false) =>
        RunCommand(stopAlias
            ? ["stop", seed.GoalId.Value[..8], "Operator cancel", "--as", "cancel", "--confirm-goal-stop"]
            : CancelParts(seed), seed.Repository, seed.Workspace);

    private static string[] CancelParts(Seed seed) =>
        ["cancel-goal", seed.GoalId.Value[..8], "Operator cancel", "--confirm-goal-stop"];

    private static CommandResult ParkOther(Seed seed) => RunCommand(
        ["park-goal", seed.OtherGoalId.Value[..8], "Retry delivery", "--confirm-goal-park"],
        seed.Repository, seed.Workspace);

    private static Task<long?> Version(Seed seed) =>
        SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(seed.Workspace.SqliteStatePath, seed.GoalId.Value);

    private static async Task<long> CountOutboxRows(Seed seed)
    {
        await using var connection = new SqliteConnection($"Data Source={seed.Workspace.SqliteStatePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM state_outbox";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void AssertOneCancellation(Seed seed, string? messageId = null)
    {
        var line = Xunit.Assert.Single(File.ReadAllLines(seed.EventPath));
        using var document = JsonDocument.Parse(line);
        Xunit.Assert.Equal("GoalCancelled", document.RootElement.GetProperty("eventType").GetString());
        Xunit.Assert.Equal(seed.GoalId.Value, document.RootElement.GetProperty("goalId").GetString());
        if (messageId is not null)
            Xunit.Assert.Equal(messageId, document.RootElement.GetProperty("deliveryId").GetString());
    }

    private sealed class Seed(string root, OrchestratorWorkspace workspace, SqliteOrchestratorStateRepository repository)
        : IDisposable
    {
        internal string Root { get; } = root;
        internal OrchestratorWorkspace Workspace { get; } = workspace;
        internal SqliteOrchestratorStateRepository Repository { get; } = repository;
        internal GoalId GoalId { get; } = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        internal GoalId OtherGoalId { get; } = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        internal string EventPath => CliGoalCancelLifecycleOutboxTests.EventPath(Workspace, GoalId);

        internal static async Task<Seed> Create()
        {
            var root = CreateTempDirectory();
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var seed = new Seed(root, workspace, repository);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(seed.GoalId, "Cancel target",
                [new TaskSpec(new TaskId("dddddddddddddddddddddddddddddddd"), "Cancel task", AgentRole.Developer)]);
            kernel.CreateGoal(seed.OtherGoalId, "Other writer target",
                [new TaskSpec(new TaskId("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), "Other task", AgentRole.Developer)]);
            foreach (var snapshot in kernel.ExportSnapshot().Goals.ToArray())
                kernel.ReplaceGoalWithSnapshot(snapshot with { Status = GoalStatus.Active });
            await repository.SaveAsync(kernel);
            return seed;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
