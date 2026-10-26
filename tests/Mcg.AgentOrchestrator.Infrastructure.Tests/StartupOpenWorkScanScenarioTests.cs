using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Unique temporary store; this collection protects the command's console and environment.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class StartupOpenWorkScanScenarioTests : CliCommandTestBase
{
    [Fact]
    public async Task ConductLoop_TwoStartups_ReadsMaintainedCandidatesOncePerPass()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var statements = new ConcurrentBag<string>();
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(
                workspace.SqliteStatePath, statementObserver: statements.Add);
            var kernel = new AgentOrchestratorKernel();
            var stale = kernel.CreateGoal("Stale terminal work", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            var closed = kernel.CreateGoal("Closed terminal work", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
            kernel.ActivateGoal(stale.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(closed.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(closed.Id, closed.Tasks[0].Id, WorkTaskStatus.Cancelled, "settled");
            kernel = WithGoalStatus(WithGoalStatus(kernel, stale.Id, GoalStatus.Completed), closed.Id, GoalStatus.Completed);
            await repository.SaveAsync(kernel);

            Assert.All((await repository.LoadAsync()).Goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            Assert.Empty(IndexObjects(workspace.SqliteStatePath));
            Assert.Equal([stale.Id], await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
            statements.Clear();

            var firstOutput = ExecutePass(repository, workspace);

            Assert.Equal(new[] { "goal_open_work", "goal_open_work_after_insert", "goal_open_work_after_update", "ix_goal_open_work_terminal" },
                IndexObjects(workspace.SqliteStatePath));
            Assert.Single(statements.Where(sql => sql == TerminalOpenWorkIndex.ListSql));
            Assert.Contains("TERMINAL_OPEN_WORK_INDEX built", firstOutput, StringComparison.Ordinal);
            Assert.Equal(WorkTaskStatus.Cancelled, (await repository.LoadGoalAsync(stale.Id))!.Tasks[0].Status);
            AssertFirstTickLine(firstOutput);
            Assert.Equal(1, FirstTickRecords(workspace.ConductEventsLogPath));
            Assert.Empty(await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
            statements.Clear();

            var secondOutput = ExecutePass(repository, workspace);

            Assert.DoesNotContain(statements, sql => sql.Contains("'$.WorkerBuildInvocations'", StringComparison.Ordinal));
            Assert.Single(statements.Where(sql => sql == TerminalOpenWorkIndex.ListSql));
            Assert.DoesNotContain("TERMINAL_OPEN_WORK_INDEX built", secondOutput, StringComparison.Ordinal);
            AssertFirstTickLine(secondOutput);
            Assert.Equal(2, FirstTickRecords(workspace.ConductEventsLogPath));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static string ExecutePass(ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => CaptureConsoleError(() => CliPersistentStateRunner.ExecuteCommand(
            ["conduct", "--loop", "--max-iterations", "1"], repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
    }

    private static void AssertFirstTickLine(string output) => Assert.Single(output.Split('\n')
        .Where(line => line.StartsWith("STARTUP_FIRST_TICK elapsed_ms=", StringComparison.Ordinal)));

    private static int FirstTickRecords(string path) => File.ReadLines(path).Count(line =>
    {
        using var record = JsonDocument.Parse(line);
        return record.RootElement.GetProperty("eventKind").GetString() == "startup-first-tick";
    });

    private static string[] IndexObjects(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE (type = 'table' AND name = 'goal_open_work') OR (type = 'index' AND name = 'ix_goal_open_work_terminal') OR (type = 'trigger' AND name IN ('goal_open_work_after_insert', 'goal_open_work_after_update')) ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }
}
