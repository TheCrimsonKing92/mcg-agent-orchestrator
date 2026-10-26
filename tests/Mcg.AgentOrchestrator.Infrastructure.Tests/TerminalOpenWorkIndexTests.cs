using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every test owns a unique temporary database and all its connections.
public sealed class TerminalOpenWorkIndexTests
{
    [Fact]
    public async Task Ensure_MixedGoalsAndEveryWriter_MaintainsScanParity()
    {
        using var fixture = new Fixture();
        var repository = fixture.Repository;
        await repository.SaveAsync(fixture.Kernel);
        Assert.Equal(fixture.Ids(1, 2, 3, 4, 5), await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());

        Assert.True(await repository.EnsureTerminalOpenWorkIndexAsync());
        Assert.Equal(fixture.Ids(1, 2, 3, 4, 5), await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        await fixture.AssertParity();

        fixture.Change(7, GoalStatus.Cancelled, WorkTaskStatus.Assigned);
        await repository.SaveAsync(fixture.Kernel);
        Assert.Equal(fixture.Ids(1, 2, 3, 4, 5, 7), await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        await fixture.AssertParity();

        fixture.Change(1, GoalStatus.Completed, WorkTaskStatus.Cancelled);
        await repository.SaveAsync(fixture.Kernel);
        Assert.Equal(fixture.Ids(2, 3, 4, 5, 7), await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        await fixture.AssertParity();

        Assert.True(await repository.ArchiveTerminalGoalSnapshotsAsync(Fixture.At.AddDays(10)) > 0);
        Assert.Equal(fixture.Ids(2, 3, 7), await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        Assert.Equal("0", Assert.Single(fixture.Query(
            "SELECT json_array_length(snapshot_json, '$.Tasks') FROM goals WHERE id = $id", fixture.Id(4))));
        await fixture.AssertParity();

        // New post-ensure save exercises the INSERT trigger as well as the existing-row UPDATEs.
        var ninthKernel = new AgentOrchestratorKernel();
        var ninth = ninthKernel.CreateGoal("R9", [new TaskSpec(TaskId.New(), "Running", AgentRole.Developer)]);
        ninthKernel.ActivateGoal(ninth.Id, AgentCatalog.Default().Agents);
        ninthKernel.ReportTaskProgress(ninth.Id, ninth.Tasks[0].Id, WorkTaskStatus.Running, "running");
        await repository.SaveAsync(ninthKernel);
        Assert.Equal("0", Assert.Single(fixture.Query(
            "SELECT terminal_open_work FROM goal_open_work WHERE goal_id = $id", ninth.Id)));
        await fixture.AssertParity();
        fixture.Execute("UPDATE goals SET status = 'Completed' WHERE id = $id", ninth.Id);
        Assert.Equal(fixture.Ids(2, 3, 7).Append(ninth.Id).OrderBy(id => id.Value, StringComparer.Ordinal),
            await repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        await fixture.AssertParity();
    }

    [Fact]
    public async Task List_EnsuredStore_UsesPartialIndexAndEnsureDoesNotBackfillAgain()
    {
        using var fixture = new Fixture();
        await fixture.Repository.SaveAsync(fixture.Kernel);
        Assert.True(await fixture.Repository.EnsureTerminalOpenWorkIndexAsync());
        var plan = fixture.Query("EXPLAIN QUERY PLAN " + TerminalOpenWorkIndex.ListSql, column: 3);
        Assert.Contains(plan, detail => detail.Contains("ix_goal_open_work_terminal", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => Regex.IsMatch(detail, @"\bgoals\b", RegexOptions.IgnoreCase));

        fixture.Statements.Clear();
        Assert.Equal(fixture.Ids(1, 2, 3, 4, 5), await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        Assert.Single(fixture.Statements.Where(sql => sql == TerminalOpenWorkIndex.ListSql));
        Assert.DoesNotContain(fixture.Statements, HasOpenWorkPredicate);

        var before = fixture.Query("SELECT goal_id || ':' || terminal_open_work FROM goal_open_work ORDER BY goal_id");
        Assert.Equal(8, before.Length);
        fixture.Statements.Clear();
        Assert.False(await fixture.Repository.EnsureTerminalOpenWorkIndexAsync());
        Assert.DoesNotContain(fixture.Statements, HasOpenWorkPredicate);
        Assert.Equal(before, fixture.Query("SELECT goal_id || ':' || terminal_open_work FROM goal_open_work ORDER BY goal_id"));
        Assert.Contains("BEGIN IMMEDIATE", fixture.Statements);
        Assert.Contains(TerminalOpenWorkIndex.ExistsSql, fixture.Statements);
        Assert.Contains("COMMIT", fixture.Statements);
    }

    [Fact]
    public async Task List_UnensuredAndReadOnlyStores_UsesScanWithoutCreatingTable()
    {
        using var fixture = new Fixture();
        await fixture.Repository.SaveAsync(fixture.Kernel);
        Assert.Empty(fixture.Query("SELECT name FROM sqlite_schema WHERE name = 'goal_open_work'"));
        fixture.Statements.Clear();

        var expected = fixture.Query(TerminalOpenWorkIndex.ScanSql).Select(id => new GoalId(id)).ToArray();
        Assert.Equal(fixture.Ids(1, 2, 3, 4, 5), expected);
        Assert.Equal(expected, await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        Assert.Contains(TerminalOpenWorkIndex.ScanSql, fixture.Statements);
        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Path);
        Assert.Equal(expected, await readOnly.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        var diagnostics = new List<string>();
        Assert.Equal(expected, TerminalOpenWorkCandidates.Read(readOnly, diagnostics.Add));
        Assert.StartsWith("TERMINAL_OPEN_WORK_INDEX ensure-failed: SqliteException: ", Assert.Single(diagnostics));
        Assert.Empty(fixture.Query("SELECT name FROM sqlite_schema WHERE name = 'goal_open_work'"));
    }

    [Fact]
    public async Task Ensure_BackfillFailure_RollsBackSchemaAndCanBuildOnNextAttempt()
    {
        var failBackfill = true;
        using var fixture = new Fixture(sql =>
        {
            if (sql == TerminalOpenWorkIndex.BackfillSql && failBackfill)
                throw new IOException("injected backfill failure");
        });
        await fixture.Repository.SaveAsync(fixture.Kernel);

        await Assert.ThrowsAsync<IOException>(() => fixture.Repository.EnsureTerminalOpenWorkIndexAsync());

        Assert.Empty(fixture.Query("SELECT name FROM sqlite_schema WHERE name LIKE '%goal_open_work%'"));
        Assert.Contains("ROLLBACK", fixture.Statements);
        failBackfill = false;
        Assert.True(await fixture.Repository.EnsureTerminalOpenWorkIndexAsync());
        await fixture.AssertParity();
    }

    [Fact]
    public async Task List_InvalidSnapshot_ExcludesRowBeforeAndAfterTriggerMaintenance()
    {
        using var fixture = new Fixture();
        await fixture.Repository.SaveAsync(fixture.Kernel);
        fixture.Execute("UPDATE goals SET snapshot_json = 'invalid JSON' WHERE id = $id", fixture.Id(1));
        Assert.Equal(fixture.Ids(2, 3, 4, 5), await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        Assert.True(await fixture.Repository.EnsureTerminalOpenWorkIndexAsync());
        await fixture.AssertParity();
        fixture.Execute("UPDATE goals SET snapshot_json = 'invalid JSON' WHERE id = $id", fixture.Id(2));
        Assert.Equal(fixture.Ids(3, 4, 5), await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());
        await fixture.AssertParity();
    }

    private static bool HasOpenWorkPredicate(string sql) => sql.Contains("'$.WorkerBuildInvocations'", StringComparison.Ordinal);

    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        private readonly GoalId[] _ids;
        internal string Path { get; }
        internal ConcurrentBag<string> Statements { get; } = [];
        internal SqliteOrchestratorStateRepository Repository { get; }
        internal AgentOrchestratorKernel Kernel { get; private set; } = new();

        internal Fixture(Action<string>? observe = null)
        {
            Path = System.IO.Path.Combine(_root, "state.db");
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Path, statementObserver: sql =>
            {
                Statements.Add(sql);
                observe?.Invoke(sql);
            });
            _ids = Enumerable.Range(1, 8).Select(number =>
            {
                var goal = Kernel.CreateGoal($"R{number}", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
                Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
                return goal.Id;
            }).ToArray();
            var statuses = new[] { GoalStatus.Completed, GoalStatus.Cancelled, GoalStatus.Superseded,
                GoalStatus.Completed, GoalStatus.Completed, GoalStatus.Completed, GoalStatus.Active, GoalStatus.Cancelled };
            var taskStatuses = new[] { WorkTaskStatus.Assigned, WorkTaskStatus.Failed, WorkTaskStatus.Pending,
                WorkTaskStatus.Completed, WorkTaskStatus.Completed, WorkTaskStatus.Completed, WorkTaskStatus.Assigned, WorkTaskStatus.Cancelled };
            var snapshot = Kernel.ExportSnapshot();
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select((goal, index) => goal with
                {
                    Status = statuses[index],
                    Timeline = goal.Timeline.Select(entry => entry with { OccurredAt = At }).ToArray(),
                    Tasks = goal.Tasks.Select(task => task with
                    {
                        Status = taskStatuses[index],
                        WorkerBuildInvocations = index is >= 3 and <= 5 ? [Invocation(goal, task, index)] : null
                    }).ToArray()
                }).ToArray()
            });
        }

        private WorkerBuildInvocation Invocation(GoalSnapshot goal, TaskSnapshot task, int index)
        {
            var process = new WorkerBuildProcess(123, At, "fixture.exe");
            var observations = new List<WorkerBuildObservation> { new(WorkerBuildPhase.Requested, At) };
            if (index > 3) observations.Add(new(WorkerBuildPhase.Completed, At));
            return new WorkerBuildInvocation(Guid.NewGuid().ToString("N"), "request", goal.Id, task.Id,
                At, "dispatch", _root, "tree", "scope", ["App.csproj"], "Debug", At.AddHours(1),
                process, process, "admission.db", 1, 1, observations,
                Outcome: index > 3 ? WorkerBuildOutcome.Cancelled : null, CustodyReleased: index == 5);
        }

        internal GoalId Id(int number) => _ids[number - 1];
        internal GoalId[] Ids(params int[] numbers) => numbers.Select(Id).OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();

        internal void Change(int number, GoalStatus status, WorkTaskStatus taskStatus)
        {
            var goal = Kernel.ExportGoalSnapshot(Id(number));
            Kernel.ReplaceGoalWithSnapshot(goal with { Status = status,
                Tasks = goal.Tasks.Select(task => task with { Status = taskStatus }).ToArray() });
        }

        internal async Task AssertParity() => Assert.Equal(
            Query(TerminalOpenWorkIndex.ScanSql).Select(id => new GoalId(id)),
            await Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync());

        internal string[] Query(string sql, GoalId? id = null, int column = 0)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (id is not null) command.Parameters.AddWithValue("$id", id.Value);
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read()) rows.Add(reader.GetValue(column).ToString()!);
            return rows.ToArray();
        }

        internal void Execute(string sql, GoalId id)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id.Value);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={Path};Mode=ReadWrite;Pooling=False");
            connection.Open();
            return connection;
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
