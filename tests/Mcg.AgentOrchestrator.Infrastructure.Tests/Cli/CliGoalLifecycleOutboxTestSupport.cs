using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public abstract class CliGoalLifecycleOutboxTestSupport : CliGoalParkTestSupport
{
    private protected const string PendingSentence =
        "The lifecycle event delivery is pending and will be retried by the next writer-path command.";

    private protected static string[] Parts(Seed seed, string command) => command switch
    {
        "unpark" => ["unpark-goal", seed.GoalId.Value[..8], "Operator transition", "--confirm-goal-unpark"],
        "supersede" => ["supersede-goal", seed.GoalId.Value[..8], "Operator transition", "--confirm-goal-stop"],
        "abandon" => ["abandon-goal", seed.GoalId.Value[..8], "Operator transition", "--confirm-goal-abandon"],
        "stop-abandon" => ["stop", seed.GoalId.Value[..8], "Operator transition", "--as", "abandon", "--confirm-goal-abandon"],
        _ => throw new ArgumentException("Unknown transition", nameof(command))
    };

    private protected static GoalStatus PriorStatus(string command) =>
        command == "unpark" ? GoalStatus.Parked : GoalStatus.Active;

    private protected static GoalStatus CommittedStatus(string command) => command switch
    {
        "unpark" => GoalStatus.Active,
        "supersede" => GoalStatus.Superseded,
        _ => GoalStatus.Cancelled
    };

    private protected static string EventType(string command) => command switch
    {
        "unpark" => "GoalLifecycleDecision",
        "supersede" => "GoalSuperseded",
        _ => "GoalCancelled"
    };

    private protected static CommandResult Transition(Seed seed, string command) =>
        RunCommand(Parts(seed, command), seed.Repository, seed.Workspace);

    private protected static CommandResult ParkOther(Seed seed) => RunCommand(
        ["park-goal", seed.OtherGoalId.Value[..8], "Retry delivery", "--confirm-goal-park"],
        seed.Repository, seed.Workspace);

    private protected static Task<long?> Version(Seed seed) =>
        SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(seed.Workspace.SqliteStatePath, seed.GoalId.Value);

    private protected static async Task<long> CountOutboxRows(Seed seed)
    {
        await using var connection = new SqliteConnection($"Data Source={seed.Workspace.SqliteStatePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM state_outbox";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private protected static void AssertOneLine(Seed seed, string eventType, string? messageId = null)
    {
        var line = Xunit.Assert.Single(File.ReadAllLines(seed.EventPath));
        using var document = JsonDocument.Parse(line);
        Xunit.Assert.Equal(eventType, document.RootElement.GetProperty("eventType").GetString());
        Xunit.Assert.Equal(seed.GoalId.Value, document.RootElement.GetProperty("goalId").GetString());
        if (eventType == "GoalLifecycleDecision")
            Xunit.Assert.Equal("GoalPolicyDecision", document.RootElement.GetProperty("progressKind").GetString());
        var deliveryId = document.RootElement.GetProperty("deliveryId").GetString();
        Xunit.Assert.True(Guid.TryParseExact(deliveryId, "N", out _));
        if (messageId is not null)
            Xunit.Assert.Equal(messageId, deliveryId);
    }

    private protected sealed class Seed(string root, OrchestratorWorkspace workspace, SqliteOrchestratorStateRepository repository)
        : IDisposable
    {
        internal string Root { get; } = root;
        internal OrchestratorWorkspace Workspace { get; } = workspace;
        internal SqliteOrchestratorStateRepository Repository { get; } = repository;
        internal GoalId GoalId { get; } = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        internal GoalId OtherGoalId { get; } = new("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        internal string EventPath => CliGoalLifecycleOutboxTestSupport.EventPath(Workspace, GoalId);

        internal static async Task<Seed> Create(string command = "supersede", GoalStatus? status = null)
        {
            var root = CreateTempDirectory();
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var seed = new Seed(root, workspace, repository);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(seed.GoalId, "Lifecycle target",
                [new TaskSpec(new TaskId("dddddddddddddddddddddddddddddddd"), "Lifecycle task", AgentRole.Developer)]);
            kernel.CreateGoal(seed.OtherGoalId, "Other writer target",
                [new TaskSpec(new TaskId("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), "Other task", AgentRole.Developer)]);
            foreach (var snapshot in kernel.ExportSnapshot().Goals.ToArray())
                kernel.ReplaceGoalWithSnapshot(snapshot with
                {
                    Status = snapshot.Id == seed.GoalId.Value ? status ?? PriorStatus(command) : GoalStatus.Active
                });
            await repository.SaveAsync(kernel);
            return seed;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
