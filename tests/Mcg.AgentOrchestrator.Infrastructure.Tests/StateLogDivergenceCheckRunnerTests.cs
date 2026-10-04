using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every fixture owns its database, logs, queries and clock.
public sealed class StateLogDivergenceCheckRunnerTests
{
    [Fact]
    public async Task FixtureReportsOnlyDivergentGoalsWithTagsAndSuppressesUnchangedResults()
    {
        using var fixture = await Fixture.CreateAsync();
        var runner = fixture.Runner();
        Assert.True(runner.OnTick());
        var first = await TestHangGuard.WaitAsync(runner.CurrentRun!, "three-goal check completion");
        Assert.Equal(3, first.CheckedGoals);
        Assert.Equal(2, first.EmittedEvents);
        Assert.Empty(first.SkippedReasons);
        var events = fixture.Events("state-log-divergence");
        Assert.Equal(2, events.Length);
        var lost = Assert.Single(events, entry => entry.GetProperty("goalId").GetString() == fixture.LostGoal.Id.Value);
        Assert.StartsWith($"STATE_LOG_DIVERGENCE goal={fixture.LostGoal.Id.Value[..8]} lost=1 repeated=0 stored_only=0 kinds=TaskRetried:1 first_log_cursor=", lost.GetProperty("detail").GetString());
        Assert.Equal("decision", lost.GetProperty("operator").GetString());
        var stored = Assert.Single(events, entry => entry.GetProperty("goalId").GetString() == fixture.StoredGoal.Id.Value);
        Assert.StartsWith($"STATE_LOG_DIVERGENCE goal={fixture.StoredGoal.Id.Value[..8]} lost=0 repeated=0 stored_only=1 kinds=GoalPolicyDecision:1 first_log_cursor=0", stored.GetProperty("detail").GetString());
        Assert.Equal("outcome", stored.GetProperty("operator").GetString());

        Assert.False(runner.OnTick());
        fixture.Now += StateLogDivergenceCheckRunner.Interval;
        Assert.True(runner.OnTick());
        var second = await TestHangGuard.WaitAsync(runner.CurrentRun!, "unchanged fixture check completion");
        Assert.Equal(3, second.CheckedGoals);
        Assert.Equal(0, second.EmittedEvents);
        Assert.Equal(2, fixture.Events("state-log-divergence").Length);
    }

    [Fact]
    public async Task OnlyByDesignTelemetryProducesNoConductEvent()
    {
        using var fixture = await Fixture.CreateAsync(cleanOnly: true);
        fixture.Writer.AppendTimelineEvent(new(fixture.CleanGoal.Id, null, ProgressKind.GoalPolicyDecision,
            "Batch loop tick 7: held at WorkspaceReady — waiting", fixture.Now));
        var runner = fixture.Runner();
        Assert.True(runner.OnTick());
        var result = await TestHangGuard.WaitAsync(runner.CurrentRun!, "held-only check completion");
        Assert.Equal(1, result.CheckedGoals);
        Assert.Equal(0, result.EmittedEvents);
        Assert.Empty(fixture.Events("state-log-divergence"));
    }

    [Fact]
    public async Task TickReturnsWhileBlockedAndCommandExitJoinsUntilSignalReleased()
    {
        using var fixture = await Fixture.CreateAsync(cleanOnly: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new ControlledQueries([], async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var runner = fixture.Runner(queries);
        try
        {
            Assert.True(await TestHangGuard.WaitAsync(Task.Run(runner.OnTick, TestContext.Current.CancellationToken), "scheduling call returned"));
            await TestHangGuard.WaitAsync(entered.Task, "background metadata query entered");
            Assert.False(release.Task.IsCompleted);
            Assert.False(runner.CurrentRun!.IsCompleted);
            Assert.False(runner.OnTick());
            // Obtain the actual exit join task while its controlled producer is blocked.
            var joinReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            await TestHangGuard.WaitAsync(Task.Run(() => joinReturned.SetResult(runner.WaitForCurrentRunAsync())), "exit join invocation returned");
            var join = await joinReturned.Task;
            Assert.False(join.IsCompleted);
            release.TrySetResult();
            await TestHangGuard.WaitAsync(join, "exit join after release");
            Assert.True(runner.CurrentRun.IsCompletedSuccessfully);
            Assert.Equal(1, queries.MetadataCalls);
        }
        finally
        {
            release.TrySetResult();
            await TestHangGuard.WaitAsync(runner.WaitForCurrentRunAsync(), "blocked check cleanup");
        }
    }

    [Fact]
    public async Task BudgetChecksOnlyNewestFiftyOnceAndUsesHourlyClockGate()
    {
        using var fixture = await Fixture.CreateAsync(cleanOnly: true);
        var summaries = Enumerable.Range(0, 55).Select(index => new GoalSummary(
            $"{index:D8}", "Active", "fixture", fixture.Now.AddMinutes(index).ToString("O"))).ToArray();
        var queries = new ControlledQueries(summaries);
        var runner = fixture.Runner(queries);
        Assert.True(runner.OnTick());
        var result = await TestHangGuard.WaitAsync(runner.CurrentRun!, "bounded metadata check");
        Assert.Equal(50, result.SkippedReasons["snapshot-unreadable"]);
        Assert.Equal(summaries.Reverse().Take(50).Select(summary => summary.Id), queries.LoadedIds);
        Assert.Equal(50, queries.LoadedIds.Distinct().Count());
        fixture.Now += StateLogDivergenceCheckRunner.Interval - TimeSpan.FromTicks(1);
        Assert.False(runner.OnTick());
        Assert.Equal(1, queries.MetadataCalls);
        fixture.Now += TimeSpan.FromTicks(1);
        Assert.True(runner.OnTick());
        await TestHangGuard.WaitAsync(runner.CurrentRun!, "next hourly bounded check");
        Assert.Equal(2, queries.MetadataCalls);
        Assert.Single(fixture.Events("state-log-divergence-skipped"));
    }

    [Theory]
    [InlineData(true, "log-missing")]
    [InlineData(false, "log-malformed")]
    public async Task UnreadableLogIsSkippedAndCountedWithoutFalseStoredOnly(bool missing, string reason)
    {
        using var fixture = await Fixture.CreateAsync(cleanOnly: true);
        var path = fixture.LogPath(fixture.CleanGoal);
        if (missing) File.Delete(path);
        else File.AppendAllText(path, "{torn");
        var runner = fixture.Runner();
        Assert.True(runner.OnTick());
        var result = await TestHangGuard.WaitAsync(runner.CurrentRun!, "unreadable log check");
        Assert.Equal(0, result.CheckedGoals);
        Assert.Equal(1, result.SkippedReasons[reason]);
        Assert.Equal(0, result.EmittedEvents);
        Assert.Empty(fixture.Events("state-log-divergence"));
        Assert.Contains($"skipped=1 reasons={reason}:1", Assert.Single(fixture.Events("state-log-divergence-skipped")).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task QueryFailureDoesNotFaultSchedulingOrExitJoin()
    {
        using var fixture = await Fixture.CreateAsync(cleanOnly: true);
        var runner = new StateLogDivergenceCheckRunner(() => throw new IOException("injected"),
            fixture.Workspace.GoalLifecycleEventsDirectory, fixture.Workspace.ConductEventsLogPath, () => fixture.Now);
        Assert.True(runner.OnTick());
        await TestHangGuard.WaitAsync(runner.WaitForCurrentRunAsync(), "failed check joined");
        Assert.True(runner.CurrentRun!.IsCompletedSuccessfully);
        Assert.Single(fixture.Events("state-log-divergence-failed"));
        Assert.False(runner.OnTick());
    }

    private sealed class ControlledQueries(IReadOnlyList<GoalSummary> summaries, Func<Task>? beforeList = null) : IOrchestratorStateQueries
    {
        internal int MetadataCalls;
        internal readonly List<string> LoadedIds = [];
        public async Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            MetadataCalls++;
            if (beforeList is not null) await beforeList();
            return summaries;
        }
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
        {
            LoadedIds.AddRange(goalIds.Select(id => id.Value));
            return Task.FromResult(new AgentOrchestratorKernel());
        }
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), $"mcg-state-log-{Guid.NewGuid():N}");
        internal OrchestratorWorkspace Workspace { get; }
        internal GoalLifecycleEventWriter Writer { get; }
        internal Goal CleanGoal = null!;
        internal Goal LostGoal = null!;
        internal Goal StoredGoal = null!;
        internal DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");

        private Fixture()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Writer = new GoalLifecycleEventWriter(Workspace.GoalLifecycleEventsDirectory);
        }

        internal static async Task<Fixture> CreateAsync(bool cleanOnly = false)
        {
            var fixture = new Fixture();
            try
            {
                // Arrange the schema before repository writes or the runner's read-only queries.
                _ = StateDbMigrations.EnsureUpToDate(fixture.Workspace.SqliteStatePath);
                var kernel = new AgentOrchestratorKernel(new FixtureClock(fixture));
                kernel.SetEventWriter(fixture.Writer);
                fixture.CleanGoal = kernel.CreateGoal("Clean fixture");
                if (!cleanOnly)
                {
                    fixture.LostGoal = kernel.CreateGoal("Lost fixture");
                    fixture.StoredGoal = kernel.CreateGoal("Stored-only fixture");
                    fixture.Writer.AppendTimelineEvent(new(fixture.LostGoal.Id, null, ProgressKind.TaskRetried,
                        "finding evidence-on-demand", fixture.Now));
                    kernel.SetEventWriter(NullGoalLifecycleEventWriter.Instance);
                    kernel.RecordGoalPolicyDecision(fixture.StoredGoal.Id, "stored-only decision");
                }
                await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath).SaveAsync(kernel);
                // End the fixture's WAL activity before byte-equality probes. The connection is
                // private and unpooled; never clear pools owned by other parallel tests.
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = fixture.Workspace.SqliteStatePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                await connection.OpenAsync();
                await using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                await checkpoint.ExecuteNonQueryAsync();
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        internal StateLogDivergenceCheckRunner Runner(IOrchestratorStateQueries? repository = null) =>
            new(() => repository ?? SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath),
                Workspace.GoalLifecycleEventsDirectory, Workspace.ConductEventsLogPath, () => Now);
        private sealed class FixtureClock(Fixture fixture) : IClock
        {
            public DateTimeOffset UtcNow => fixture.Now;
        }
        internal string LogPath(Goal goal) => Path.Combine(Workspace.GoalLifecycleEventsDirectory, goal.Id.Value + ".jsonl");
        internal JsonElement[] Events(string kind) => !File.Exists(Workspace.ConductEventsLogPath) ? [] :
            File.ReadAllLines(Workspace.ConductEventsLogPath).Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.Clone();
            }).Where(entry => entry.GetProperty("eventKind").GetString() == kind).ToArray();
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
