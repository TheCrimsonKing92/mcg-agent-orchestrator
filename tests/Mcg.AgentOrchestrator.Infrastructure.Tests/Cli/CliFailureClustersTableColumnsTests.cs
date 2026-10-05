using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

public sealed class CliFailureClustersTableColumnsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TableShowsFamiliesBesideKeysAndNamesSkippedSources(bool tornConductRecord)
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-cluster-table-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Seed(workspace, tornConductRecord);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            string[] args = ["failure-clusters", "--since", "2026-09-21T00:00:00Z", "--until", "2026-10-06T00:00:00Z"];
            var table = "";
            var error = AsyncLocalConsoleRouter.CaptureError(() => table = AsyncLocalConsoleRouter.Capture(() =>
                CliFailureClustersQueryCommand.Execute(args, workspace)));
            using var json = JsonDocument.Parse(AsyncLocalConsoleRouter.Capture(() =>
                CliFailureClustersQueryCommand.Execute([.. args, "--json"], workspace)));
            var jsonRows = json.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!);
            var lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var header = lines[0].TrimEnd('\r').Split('\t');
            Assert.Equal("key", header[1]);
            Assert.Equal("family", header[2]);
            Assert.Equal(1, header.Count(c => c == "family"));
            Assert.Equal(jsonRows.Count, lines.Length - 1);
            var sawTruncation = false;
            var sawGoalSummary = false;
            foreach (var line in lines.Skip(1))
            {
                var cells = line.TrimEnd('\r').Split('\t');
                Assert.Equal(header.Length, cells.Length);
                Assert.True(jsonRows.TryGetValue(cells[1], out var row));
                var family = row.GetProperty("messageFamily").GetString()!
                    .Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                var expected = family.Length > 80 ? family[..80] + "..." : family;
                Assert.Equal(expected, cells[2]);
                Assert.InRange(cells[2].Length, 0, 83);
                sawTruncation |= family.Length > 80;
                var goals = row.GetProperty("goalsAffected").EnumerateArray().Select(g => g.GetString()).ToArray();
                Assert.Equal(string.Join(',', goals.Take(3)) + (goals.Length > 3 ? $" +{goals.Length - 3} more" : ""),
                    cells[Array.IndexOf(header, "goals")]);
                sawGoalSummary |= goals.Length > 3;
            }
            Assert.True(sawTruncation);
            Assert.True(sawGoalSummary);
            Assert.Equal(tornConductRecord ? "Warning: failure-clusters skipped 1 conduct-event record." + Environment.NewLine : "", error);
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Seed(OrchestratorWorkspace workspace, bool tornConductRecord)
    {
        Directory.CreateDirectory(workspace.GoalLifecycleEventsDirectory);
        Directory.CreateDirectory(workspace.LogDirectory);
        File.Copy(FailureClusterFixtureData.FilePath("goal-events.jsonl"), Path.Combine(workspace.GoalLifecycleEventsDirectory, "real.jsonl"));
        File.Copy(FailureClusterFixtureData.FilePath("conduct-events.jsonl"), Path.Combine(workspace.LogDirectory, "conduct-events-20261005.log"));
        if (tornConductRecord) File.WriteAllText(workspace.ConductEventsLogPath, "{torn-record\n");
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName),
            Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE operator_intents (goal_id TEXT, task_id TEXT, created_at TEXT, actor TEXT)";
        command.ExecuteNonQuery();
        foreach (var touch in FailureClusterFixtureData.Read().OperatorTouches)
        {
            command.CommandText = "INSERT INTO operator_intents VALUES ($goal, $task, $at, 'operator')";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$goal", touch.GoalId);
            command.Parameters.AddWithValue("$task", (object?)touch.TaskId ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", touch.At.ToString("O"));
            command.ExecuteNonQuery();
        }
    }
}
