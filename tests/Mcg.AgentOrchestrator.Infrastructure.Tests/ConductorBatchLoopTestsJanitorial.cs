using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsJanitorial : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsJanitorial(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact(DisplayName = "BatchLoop_retries_contended_janitorial_lock_without_reexecuting_sweep")]
    public void BatchLoopRetriesContendedJanitorialLockWithoutReexecutingSweep()
    {
        var root = CreateTempDirectory("mcg-conduct-janitorial-contention");
        var dbPath = Path.Combine(root, "state.db");
        using var lockHeld = new ManualResetEventSlim();
        using var releaseLock = new ManualResetEventSlim();
        using var lockReleased = new ManualResetEventSlim();
        var retryDelays = 0;
        var store = new ReconcileSweepRemediationStore(
            dbPath,
            busyTimeoutSeconds: 1,
            busyRetryDelay: (_, _, _) =>
            {
                retryDelays++;
                releaseLock.Set();
                Assert.True(lockReleased.Wait(TimeSpan.FromSeconds(5)), "The operator writer must release before retry.");
                return Task.CompletedTask;
            });
        var writer = Task.Run(() =>
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
                DefaultTimeout = 1
            }.ToString());
            connection.Open();
            Execute(connection, "BEGIN IMMEDIATE");
            lockHeld.Set();
            try
            {
                Assert.True(releaseLock.Wait(TimeSpan.FromSeconds(10)), "The sweep retry must release the operator writer.");
            }
            finally
            {
                Execute(connection, "ROLLBACK");
                lockReleased.Set();
            }
        });

        Assert.True(lockHeld.Wait(TimeSpan.FromSeconds(5)), "The operator writer must hold the database before the sweep starts.");
        var attempts = 0;
        string output;
        try
        {
            output = AsyncLocalConsoleRouter.Capture(() =>
                new ConductorBatchLoop(measuredSweep: _ =>
                {
                    attempts++;
                    store.Observe("state-key", "goal-id", "blocker", "evidence", "remedy");
                    return null;
                }).Run(
                    new AgentOrchestratorKernel(),
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1));
        }
        finally
        {
            releaseLock.Set();
            Assert.True(writer.Wait(TimeSpan.FromSeconds(10)), "The operator writer must exit.");
        }

        Assert.Equal(1, attempts);
        Assert.Equal(1, retryDelays);
        Assert.DoesNotContain("LOOP_JANITORIAL_FAILED", output, StringComparison.Ordinal);
        Assert.Equal("state-key", store.Observe("state-key", "goal-id", "blocker", "evidence", "remedy").StateKey);

        static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_escalates_repeated_transient_janitorial_skips_as_one_condition")]
    public void BatchLoopEscalatesRepeatedTransientJanitorialSkipsAsOneCondition()
    {
        var root = CreateTempDirectory("mcg-conduct-events-janitorial-degraded");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var attempts = 0;
        var (kernel, _) = SimpleGoal("held janitorial degraded signal goal");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        var output = AsyncLocalConsoleRouter.Capture(() =>
            new ConductorBatchLoop(
                measuredSweep: _ =>
                {
                    attempts++;
                    throw new SqliteException("injected janitorial contention", 5);
                },
                conductEventLogWriter: writer,
                writeJitter: () => 0).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: ConductorBatchLoop.JanitorialFailureEscalationThreshold * 3,
                    watchInterval: TimeSpan.FromMilliseconds(1),
                    sleepFunc: _ => false,
                    keepAliveWhenIdle: true));

        Assert.Equal(ConductorBatchLoop.JanitorialFailureEscalationThreshold * 3, attempts);
        Assert.Contains("LOOP_JANITORIAL_DEGRADED", output, StringComparison.Ordinal);
        Assert.Contains($"consecutiveFailures={ConductorBatchLoop.JanitorialFailureEscalationThreshold}", output, StringComparison.Ordinal);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Single(records, record =>
            record.EventKind == "loop-janitorial-degraded" &&
            record.Detail.Contains("workDeferred=true", StringComparison.Ordinal));
        Assert.Equal(
            ConductorBatchLoop.JanitorialFailureEscalationThreshold * 3,
            records.Count(record => record.EventKind == "loop-janitorial-failure"));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_reports_non_transient_janitorial_failure_without_retrying_or_stopping")]
    public void BatchLoopReportsNonTransientJanitorialFailureWithoutRetryingOrStopping()
    {
        var root = CreateTempDirectory("mcg-conduct-events-janitorial");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var driver = MakeDriver();
        var sweeps = 0;

        AsyncLocalConsoleRouter.Capture(() =>
            new ConductorBatchLoop(
                measuredSweep: _ =>
                {
                    sweeps++;
                    throw new InvalidOperationException("janitorial access denied");
                },
                conductEventLogWriter: writer).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 1));

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.Contains(records, record =>
            record.EventKind == "loop-janitorial-failure" &&
            record.Detail.Contains("transient=false", StringComparison.Ordinal) &&
            record.Detail.Contains("exception=InvalidOperationException", StringComparison.Ordinal) &&
            record.Detail.Contains("janitorial_access_denied", StringComparison.Ordinal));
        Assert.Equal(1, sweeps);
        Assert.DoesNotContain(records, record =>
            record.EventKind == "loop-stop" &&
            record.Detail.Contains("reason=unintended-exit", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_reports_non_transient_idle_wake_janitorial_failure_without_stopping_unintentionally")]
    public void BatchLoopReportsNonTransientIdleWakeJanitorialFailureWithoutStoppingUnintentionally()
    {
        var root = CreateTempDirectory("mcg-conduct-events-idle-wake-janitorial");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var stopFile = NoStopPath();
        var wakeSignal = new TestWakeSignal(() => File.WriteAllText(stopFile, "stop"));
        var sweeps = 0;

        try
        {
            AsyncLocalConsoleRouter.Capture(() =>
                new ConductorBatchLoop(
                    measuredSweep: _ => ++sweeps == 1
                        ? null
                        : throw new InvalidOperationException("idle wake janitorial access denied"),
                    conductEventLogWriter: writer).Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    maxIterations: 1,
                    watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                    wakeSignal: wakeSignal,
                    keepAliveWhenIdle: true));

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();

            Assert.Contains(records, record =>
                record.EventKind == "loop-janitorial-failure" &&
                record.Detail.Contains("phase=idle-wake-sweep", StringComparison.Ordinal) &&
                record.Detail.Contains("idle_wake_janitorial_access_denied", StringComparison.Ordinal));
            Assert.Equal(2, sweeps);
            Assert.DoesNotContain(records, record =>
                record.EventKind == "loop-stop" &&
                record.Detail.Contains("reason=unintended-exit", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }

    [Xunit.Fact(DisplayName = "BatchLoop_reports_non_transient_watch_wake_janitorial_failure_without_stopping_unintentionally")]
    public void BatchLoopReportsNonTransientWatchWakeJanitorialFailureWithoutStoppingUnintentionally()
    {
        var root = CreateTempDirectory("mcg-conduct-events-watch-wake-janitorial");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var (kernel, _) = SimpleGoal("held wake janitorial failure goal");
        var stopFile = NoStopPath();
        var wakeSignal = new TestWakeSignal(() => File.WriteAllText(stopFile, "stop"));
        var sweeps = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        try
        {
            AsyncLocalConsoleRouter.Capture(() =>
                new ConductorBatchLoop(
                    measuredSweep: _ => ++sweeps == 1
                        ? null
                        : throw new InvalidOperationException("watch wake janitorial access denied"),
                    conductEventLogWriter: writer).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    stopFile,
                    maxIterations: 1,
                    watchInterval: TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds),
                    wakeSignal: wakeSignal));

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();

            Assert.Contains(records, record =>
                record.EventKind == "loop-janitorial-failure" &&
                record.Detail.Contains("phase=wake-sweep", StringComparison.Ordinal) &&
                record.Detail.Contains("watch_wake_janitorial_access_denied", StringComparison.Ordinal));
            Assert.Equal(2, sweeps);
            Assert.DoesNotContain(records, record =>
                record.EventKind == "loop-stop" &&
                record.Detail.Contains("reason=unintended-exit", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(stopFile)) File.Delete(stopFile);
        }
    }
}
