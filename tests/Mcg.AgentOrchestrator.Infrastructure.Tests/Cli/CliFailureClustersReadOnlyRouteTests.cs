using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

public sealed class CliFailureClustersReadOnlyRouteTests : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("failure-clusters")]
    [InlineData("FAILURE-CLUSTERS")]
    [InlineData("failure-clusters", "--since", "2026-09-21T00:00:00Z")]
    [InlineData("failure-clusters", "--until", "2026-10-05T00:00:00Z")]
    [InlineData("failure-clusters", "--top", "5")]
    [InlineData("failure-clusters", "--json")]
    public void EachFlagUsesTheReadOnlyRoute(params string[] args)
    {
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TableAndJsonAgreeWithoutLoadingWriterOrChangingInputs(bool realFixture)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Seed(workspace, realFixture);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? goal = null;
            var args = new[] { "failure-clusters", "--since", "2026-09-21T00:00:00Z", "--until", "2026-10-06T00:00:00Z" };
            string Run(string[] command) => CaptureConsole(() =>
            {
                Assert.True(CliReadOnlyCommandRunner.TryExecute(command, repository, workspace,
                    new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
                Assert.False(changed);
            });
            var table = Run(args);
            using var json = JsonDocument.Parse(Run([.. args, "--json"]));
            var jsonKeys = json.RootElement.EnumerateArray().Select(row => row.GetProperty("key").GetString()).ToArray();
            var tableKeys = table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(line => line.TrimEnd('\r').Split('\t')[1]).ToArray();
            Assert.Equal(jsonKeys, tableKeys);
            if (!realFixture) Assert.Equal(4, jsonKeys.Length);
            var permit = Assert.Single(json.RootElement.EnumerateArray(), row =>
                row.GetProperty("marker").GetString() == "reason=structural-coverage-permit-unavailable");
            Assert.Equal(realFixture ? (1650010d + 1496460d) / 60000 : 5,
                permit.GetProperty("gateMinutes").GetDouble(), 8);
            Assert.Contains(json.RootElement.EnumerateArray(), row => row.GetProperty("messageFamily").GetString()!.Contains("receipt was attached"));
            Assert.Equal(0, repository.FullLoadAttempts + repository.LoadGoalsCount + repository.SaveAttempts +
                repository.MergeSaveAttempts + repository.MutationAttempts + repository.ListOutboxMessagesCount +
                repository.OutboxClaimAttempts + repository.ListGoalMetadataCount);
            Assert.Null(goal);
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HelpIsDeclinedAndUnknownFlagsRemainReadOnly()
    {
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["failure-clusters", "--help"]));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["failure-clusters", "--unknown-flag"]));
    }

    private static void Seed(OrchestratorWorkspace workspace, bool realFixture)
    {
        var input = realFixture ? FailureClusterFixtureData.Read() : FailureClusterTestData.Create();
        Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
        Directory.CreateDirectory(workspace.LogDirectory);
        if (realFixture)
        {
            File.Copy(FailureClusterFixtureData.FilePath("goal-events.jsonl"), Path.Combine(workspace.GoalLifecycleEventsDirectory, "real.jsonl"));
            File.Copy(FailureClusterFixtureData.FilePath("conduct-events.jsonl"), Path.Combine(workspace.LogDirectory, "conduct-events-20261005.log"));
        }
        else
        {
            File.WriteAllLines(Path.Combine(workspace.GoalLifecycleEventsDirectory, "synthetic.jsonl"), input.GoalEvents.Select(e =>
                JsonSerializer.Serialize(new { eventType = e.EventType, message = e.Message, goalId = e.GoalId, taskId = e.TaskId, timestamp = e.Timestamp })));
            File.WriteAllLines(Path.Combine(workspace.LogDirectory, "conduct-events-20260921060000.log"), input.ConductEvents.Select(e =>
                JsonSerializer.Serialize(new { timestamp = e.Timestamp, eventKind = e.EventKind, goalId = e.GoalId, detail = e.Detail })));
        }
        File.WriteAllText(workspace.ConductEventsLogPath, "{torn-record\n");
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE operator_intents (goal_id TEXT, task_id TEXT, created_at TEXT)";
        command.ExecuteNonQuery();
        foreach (var touch in input.OperatorTouches)
        {
            command.CommandText = "INSERT INTO operator_intents VALUES ($goal, $task, $at)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$goal", touch.GoalId);
            command.Parameters.AddWithValue("$task", (object?)touch.TaskId ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", touch.At.ToString("O"));
            command.ExecuteNonQuery();
        }
    }
}
