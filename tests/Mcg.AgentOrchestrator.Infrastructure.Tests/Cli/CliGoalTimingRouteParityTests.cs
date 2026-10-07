using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliGoalTimingRouteParityTests : CliTaskQueryTestSupport
{
    [Xunit.Fact]
    public void FixedClock_ActiveDispatch_OutputMatchesWriterBytes()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 24, 1, 0, 0, TimeSpan.Zero));
            var kernel = CliReportDiagnosticsClockTests.CreateActiveDispatchSeed(
                root, new FixedClock(clock.UtcNow.AddHours(-1)));
            var goal = kernel.Goals.Single();
            Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
            Xunit.Assert.NotNull(goal.Tasks.Single().LastDispatch);
            string[] args = ["goal-timing", "abc10000"];
            var providers = new InMemoryModelProviderRegistry([]);
            IReadOnlyList<AgentDefinition> writerAgents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
            var writerProfiles = WorkerProfileCatalog.Default();
            Goal? writerGoal = goal;
            var writer = CaptureConsole(() => Xunit.Assert.False(CliCommandDispatcher.ExecuteCommand(
                args, kernel, workspace, ref writerAgents, providers, ref writerProfiles, ref writerGoal,
                diagnosticsClock: clock)));

            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            IReadOnlyList<AgentDefinition> readAgents = CliSingleGoalReportReadOnlyRouteTests.ReportAgents();
            var readProfiles = WorkerProfileCatalog.Default();
            Goal? readGoal = null;
            var read = CaptureConsole(() =>
            {
                Xunit.Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                    providers, null, ref readAgents, ref readProfiles, ref readGoal, out var changed, clock));
                Xunit.Assert.False(changed);
            });

            Xunit.Assert.Contains("Goal timing abc10000", writer);
            Xunit.Assert.Contains("e2eEndedAt=2026-09-24 01:00:00Z e2eEndSource=sample-time", writer);
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(writer), Encoding.UTF8.GetBytes(read));
            Xunit.Assert.Equal(goal.Id, readGoal!.Id);
            Xunit.Assert.Equal(1, repository.ListGoalMetadataCount);
            Xunit.Assert.Equal(1, repository.LoadGoalsCount);
            Xunit.Assert.Equal([goal.Id.Value], repository.LoadedGoalIds);
            Xunit.Assert.Equal(0, repository.FullLoadAttempts);
            Xunit.Assert.Equal(0, repository.ListOutboxMessagesCount);
            Xunit.Assert.Equal(0, repository.OutboxClaimAttempts);
            Xunit.Assert.Equal(0, repository.MutationAttempts);
            Xunit.Assert.Equal(0, repository.SaveAttempts);
            Xunit.Assert.Equal(0, repository.MergeSaveAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing", false)]
    [Xunit.InlineData("abc10000", true)]
    public async Task UnresolvedPrefix_CliErrorsMatchRetentionPlan(string prefix, bool ambiguous)
    {
        // Each child uses this fixture's private database and workspace.
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
            var kernel = new AgentOrchestratorKernel(clock);
            kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "First goal");
            if (ambiguous)
                kernel.CreateGoal(new GoalId("abc10000bbbbbbbbbbbbbbbbbbbbbbbb"), "Second goal");
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);

            var timing = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(root, ["goal-timing", prefix]);
            var retention = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(root, ["retention-plan", prefix]);

            Xunit.Assert.NotEqual(0, retention.ExitCode);
            Xunit.Assert.Contains(ambiguous ? "ambiguous" : "was not found", retention.Error);
            Xunit.Assert.Equal(retention.ExitCode, timing.ExitCode);
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(retention.Output), Encoding.UTF8.GetBytes(timing.Output));
            Xunit.Assert.Equal(Encoding.UTF8.GetBytes(retention.Error), Encoding.UTF8.GetBytes(timing.Error));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ExternalWriterHeld_ReadsTimingWithoutChangingGoalOrOutboxRows()
    {
        // The database, pending row and held writer all belong to this fixture.
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
            var kernel = CliReportDiagnosticsClockTests.CreateActiveDispatchSeed(root, clock);
            var goal = kernel.Goals.Single();
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            await using (var seed = OpenStateConnection(workspace.SqliteStatePath))
            {
                await seed.OpenAsync();
                await using var insert = seed.CreateCommand();
                insert.CommandText = "INSERT INTO state_outbox (id, kind, payload_json, created_at) VALUES ($id, $kind, '{}', $created)";
                insert.Parameters.AddWithValue("$id", "held-writer-timing-outbox");
                insert.Parameters.AddWithValue("$kind", GoalOperationJournal.AcceptanceRetryAuditOutboxKind);
                insert.Parameters.AddWithValue("$created", "2026-09-24T00:00:00.0000000+00:00");
                Xunit.Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            var before = await ReadStateRowsAsync(workspace.SqliteStatePath);
            Xunit.Assert.Equal(2, before.Length);
            Xunit.Assert.Contains(before, row => row.StartsWith("goal:" + goal.Id.Value + ":", StringComparison.Ordinal));
            Xunit.Assert.Contains(before, row => row.StartsWith("outbox:held-writer-timing-outbox:", StringComparison.Ordinal));
            await using var writer = OpenStateConnection(workspace.SqliteStatePath);
            await writer.OpenAsync();
            await using (var begin = writer.CreateCommand())
            {
                // This awaited statement is the gate proving the lock is held before launch.
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync();
            }
            try
            {
                var result = await CliAttentionNextReadOnlyWriterHeldTests.RunQueryAsync(
                    root, ["goal-timing", goal.Id.Value[..8]]);
                Xunit.Assert.Equal(0, result.ExitCode);
                Xunit.Assert.Equal(string.Empty, result.Error);
                Xunit.Assert.Contains(result.Output.Split(Environment.NewLine),
                    line => line.StartsWith("Goal timing abc10000", StringComparison.Ordinal));
            }
            finally
            {
                await using var rollback = writer.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                await rollback.ExecuteNonQueryAsync();
            }
            Xunit.Assert.Equal(before, await ReadStateRowsAsync(workspace.SqliteStatePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteConnection OpenStateConnection(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());

    private static async Task<string[]> ReadStateRowsAsync(string path)
    {
        await using var connection = OpenStateConnection(path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 'goal:' || id || ':' || version || ':' || snapshot_json FROM goals UNION ALL SELECT 'outbox:' || id || ':' || kind || ':' || payload_json || ':' || created_at FROM state_outbox ORDER BY 1";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
