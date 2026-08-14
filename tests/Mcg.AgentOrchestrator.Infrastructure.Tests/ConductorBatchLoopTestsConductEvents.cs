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
public sealed class ConductorBatchLoopTestsConductEvents : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsConductEvents(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact(DisplayName = "ConductEvents_shared_stream_receives_events_from_sequential_loop_instances")]
    public void ConductEventsSharedStreamReceivesEventsFromSequentialLoopInstances()
    {
        var root = CreateTempDirectory("mcg-conduct-events");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

        string output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var (firstKernel, _) = SimpleGoal("first conduct event stream goal");
            new ConductorBatchLoop(conductEventLogWriter: writer).Run(
                firstKernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);

            var (secondKernel, _) = SimpleGoal("second conduct event stream goal");
            new ConductorBatchLoop(conductEventLogWriter: writer).Run(
                secondKernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
        });

        Assert.Contains("GOAL goal=", output, StringComparison.Ordinal);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.True(records.Count(record => record.EventKind == "loop-start") >= 2);
        Assert.Contains(records, record => record.EventKind == "goal" && record.GoalId is not null);
    }

    [Xunit.Fact(DisplayName = "ConductEvents_required_rollback_survives_transient_stream_write_failure")]
    public void ConductEventsRequiredRollbackSurvivesTransientStreamWriteFailure()
    {
        var root = CreateTempDirectory("mcg-conduct-events-required");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var writer = new ConductEventLogWriter(logPath);

        bool appendedImmediately;
        using (var streamLock = new FileStream(
            logPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read))
        {
            appendedImmediately = writer.AppendRequired(
                "loop-relaunch-rollback",
                "goal1234",
                "LOOP_RELAUNCH_ROLLBACK goal=goal1234 phase=self-check rolledBack=true continuing=true");
        }

        Assert.False(appendedImmediately);
        Assert.Single(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(logPath)!, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));

        writer.Append("loop-stop", null, "LOOP_STOP tick=2 reason=test");

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Contains(records, record =>
            record.EventKind == "loop-relaunch-rollback" &&
            record.GoalId == "goal1234" &&
            record.Detail.Contains("continuing=true", StringComparison.Ordinal));
        Assert.Contains(records, record => record.EventKind == "loop-stop");
        Assert.Empty(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(logPath)!, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_migrates_and_drains_legacy_parent_pending_events")]
    public void ConductEventsMigratesAndDrainsLegacyParentPendingEvents()
    {
        var root = CreateTempDirectory("mcg-conduct-events-legacy-pending");
        var logDirectory = Path.Combine(root, ".orchestrator", "logs");
        var logPath = Path.Combine(logDirectory, ConductEventLogWriter.CurrentFileName);
        Directory.CreateDirectory(logDirectory);
        var legacyPendingPath = Path.Combine(
            logDirectory,
            $"{Path.GetFileName(logPath)}.pending-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(
            legacyPendingPath,
            JsonSerializer.Serialize(
                new ConductEventRecord(
                    DateTimeOffset.Parse("2026-07-27T12:00:00Z"),
                    "legacy-required",
                    "goal1234",
                    "LEGACY_REQUIRED goal=goal1234"),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine);

        var writer = new ConductEventLogWriter(logPath);
        writer.Append("loop-stop", null, "LOOP_STOP tick=1 reason=test");

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Contains(records, record => record.EventKind == "legacy-required" && record.GoalId == "goal1234");
        Assert.Contains(records, record => record.EventKind == "loop-stop");
        Assert.False(File.Exists(legacyPendingPath));
        Assert.Empty(Directory.GetFiles(
            Path.Combine(logDirectory, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_migrates_legacy_pending_events_for_each_log_name")]
    public void ConductEventsMigratesLegacyPendingEventsForEachLogName()
    {
        var root = CreateTempDirectory("mcg-conduct-events-legacy-pending-names");
        var logDirectory = Path.Combine(root, ".orchestrator", "logs");
        Directory.CreateDirectory(logDirectory);
        var firstLogPath = Path.Combine(logDirectory, "first-events.log");
        var secondLogPath = Path.Combine(logDirectory, "second-events.log");

        static string WriteLegacyPending(string logDirectory, string logPath, string eventKind)
        {
            var pendingPath = Path.Combine(
                logDirectory,
                $"{Path.GetFileName(logPath)}.pending-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(
                pendingPath,
                JsonSerializer.Serialize(
                    new ConductEventRecord(
                        DateTimeOffset.Parse("2026-07-27T12:00:00Z"),
                        eventKind,
                        null,
                        eventKind),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine);
            return pendingPath;
        }

        var firstPendingPath = WriteLegacyPending(logDirectory, firstLogPath, "first-legacy");
        var secondPendingPath = WriteLegacyPending(logDirectory, secondLogPath, "second-legacy");

        var firstWriter = new ConductEventLogWriter(firstLogPath);
        var secondWriter = new ConductEventLogWriter(secondLogPath);
        firstWriter.Append("first-current", null, "first-current");
        secondWriter.Append("second-current", null, "second-current");

        Assert.Contains("\"eventKind\":\"first-legacy\"", File.ReadAllText(firstLogPath), StringComparison.Ordinal);
        Assert.Contains("\"eventKind\":\"second-legacy\"", File.ReadAllText(secondLogPath), StringComparison.Ordinal);
        Assert.False(File.Exists(firstPendingPath));
        Assert.False(File.Exists(secondPendingPath));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_parallel_required_writers_drain_each_event_exactly_once")]
    public async Task ConductEventsParallelRequiredWritersDrainEachEventExactlyOnce()
    {
        var root = CreateTempDirectory("mcg-conduct-events-parallel-required");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        using var drainEntrants = new CountdownEvent(2);
        using var releaseDrain = new ManualResetEventSlim();

        void BeforeDrain()
        {
            drainEntrants.Signal();
            releaseDrain.Wait();
        }

        var firstWriter = new ConductEventLogWriter(logPath, beforeRequiredEventDrain: BeforeDrain);
        var secondWriter = new ConductEventLogWriter(logPath, beforeRequiredEventDrain: BeforeDrain);
        var writes = new[]
        {
            Task.Run(() => firstWriter.AppendRequired(
                "gate-progress",
                "goal0001",
                "PHASE_PROGRESS goal=goal0001 phase=infrastructure-shard target=first")),
            Task.Run(() => secondWriter.AppendRequired(
                "gate-progress",
                "goal0002",
                "PHASE_PROGRESS goal=goal0002 phase=infrastructure-shard target=second"))
        };

        try
        {
            Assert.True(
                drainEntrants.Wait(TimeSpan.FromSeconds(5)),
                "Both writers must enter the drain concurrently before either is released.");
        }
        finally
        {
            releaseDrain.Set();
        }

        var appendVerdicts = await Task.WhenAll(writes);
        Assert.All(appendVerdicts, Assert.True);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Equal(2, records.Length);
        Assert.Single(records, record => record.EventKind == "gate-progress" && record.GoalId == "goal0001");
        Assert.Single(records, record => record.EventKind == "gate-progress" && record.GoalId == "goal0002");
        Assert.Empty(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(logPath)!, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));
    }

    [Xunit.Fact(Timeout = 30_000, DisplayName = "ConductEvents_required_lifecycle_staging_honors_cross_process_gate")]
    public async Task ConductEventsRequiredLifecycleStagingHonorsCrossProcessGate()
    {
        var root = CreateTempDirectory("mcg-conduct-events-cross-process-required");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        using var beforeDrain = new ManualResetEventSlim();
        using var crossProcessGate = new Mutex(
            initiallyOwned: false,
            ConductEventLogWriter.RequiredEventMutexName(logPath));
        Assert.True(crossProcessGate.WaitOne(TimeSpan.FromSeconds(5)));

        var writer = new ConductEventLogWriter(logPath, beforeRequiredEventDrain: beforeDrain.Set);
        var record = new ConductEvidenceLifecycleEvent(
            DateTimeOffset.UtcNow,
            "EVIDENCE_END",
            "goal-cross-process",
            "goal-cross-process",
            "attempt-cross-process",
            1,
            "evidence:attempt-cross-process:end",
            DurationSeconds: 1d,
            Outcome: "passed",
            TestsExecuted: 1);
        var append = Task.Run(() => writer.AppendRequired(record));

        try
        {
            Assert.True(
                beforeDrain.Wait(TimeSpan.FromSeconds(5)),
                "The writer did not reach the required-event gate.");
            var pendingDirectory = Path.Combine(
                Path.GetDirectoryName(logPath)!,
                ConductEventLogWriter.PendingEventsDirectoryName);
            Assert.False(
                Directory.Exists(pendingDirectory) && Directory.EnumerateFiles(pendingDirectory).Any(),
                "Lifecycle staging must not touch its deterministic pending path while another process owns the stream gate.");
        }
        finally
        {
            crossProcessGate.ReleaseMutex();
        }

        Assert.True(await append);
        var written = Assert.Single(File.ReadAllLines(logPath));
        using var document = JsonDocument.Parse(written);
        Assert.Equal(record.EventId, document.RootElement.GetProperty("event_id").GetString());
    }

    [Xunit.Fact(DisplayName = "ConductEvents_parallel_append_and_required_write_serialize_forced_rotation")]
    public async Task ConductEventsParallelAppendAndRequiredWriteSerializeForcedRotation()
    {
        var root = CreateTempDirectory("mcg-conduct-events-parallel-rotation");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var timestamp = DateTimeOffset.Parse("2026-07-25T18:00:00Z");
        new ConductEventLogWriter(logPath, maxBytes: 0, utcNow: () => timestamp).Append(
            "seed",
            null,
            new string('x', 128));

        using var appendCommitReached = new ManualResetEventSlim();
        using var releaseAppendCommit = new ManualResetEventSlim();
        using var requiredDrainAttempted = new ManualResetEventSlim();
        var appendWriter = new ConductEventLogWriter(
            logPath,
            maxBytes: 32,
            utcNow: () => timestamp,
            beforeAppendCommit: () =>
            {
                appendCommitReached.Set();
                releaseAppendCommit.Wait();
            });
        var requiredWriter = new ConductEventLogWriter(
            logPath,
            maxBytes: 32,
            utcNow: () => timestamp,
            beforeRequiredEventDrain: requiredDrainAttempted.Set);

        var append = Task.Run(() => appendWriter.Append(
            "gate-total",
            "goal0001",
            "GATE_TOTAL goal=goal0001 durationMs=100"));
        Task<bool>? required = null;
        try
        {
            Assert.True(
                appendCommitReached.Wait(TimeSpan.FromSeconds(5)),
                "The append writer must reach the forced-rotation commit boundary.");
            required = Task.Run(() => requiredWriter.AppendRequired(
                "gate-progress",
                "goal0002",
                "PHASE_PROGRESS goal=goal0002 phase=infrastructure-shard target=required"));
            Assert.True(
                requiredDrainAttempted.Wait(TimeSpan.FromSeconds(5)),
                "The required writer must attempt its drain while the append commit is held.");
            Assert.False(
                required.IsCompleted,
                "The required writer must remain serialized behind the append rotation and write.");
        }
        finally
        {
            releaseAppendCommit.Set();
        }

        await append;
        Assert.NotNull(required);
        Assert.True(await required);

        var records = Directory.GetFiles(Path.GetDirectoryName(logPath)!, "conduct-events*.log")
            .SelectMany(File.ReadAllLines)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();
        Assert.Single(records, record => record.EventKind == "gate-total" && record.GoalId == "goal0001");
        Assert.Single(records, record => record.EventKind == "gate-progress" && record.GoalId == "goal0002");
        Assert.Empty(Directory.GetFiles(
            Path.Combine(Path.GetDirectoryName(logPath)!, ConductEventLogWriter.PendingEventsDirectoryName),
            $"{Path.GetFileName(logPath)}.pending-*.jsonl"));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_shared_stream_records_escalation_reason")]
    public void ConductEventsSharedStreamRecordsEscalationReason()
    {
        var root = CreateTempDirectory("mcg-conduct-events-escalation");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "escalation event stream goal");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptance: _ => false);

        new ConductorBatchLoop(conductEventLogWriter: writer).Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1,
            maxVerifyRetries: 0);

        var records = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToArray();

        Assert.Contains(records, record =>
            record.EventKind == "goal-escalation" &&
            record.GoalId == goal.Id.Value[..8] &&
            record.Detail.Contains("reason=Acceptance_verification_failed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductEvents_shared_stream_distinguishes_task_failure_rules")]
    public void ConductEventsSharedStreamDistinguishesTaskFailureRules()
    {
        var root = CreateTempDirectory("mcg-conduct-events-task-failure-rules");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        var writer = new ConductEventLogWriter(logPath);
        var kernel = new AgentOrchestratorKernel();
        var testsFailureGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Worker-declared failing tests");
        var testsFailureTask = testsFailureGoal.Tasks.Single();
        var testsFailureOutput = string.Join(Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: dotnet test",
            "tests: fail - focused suites reported green but worker declared a blocking finding",
            "commit: none",
            "blockers: none",
            "model_fit: fixture/model - adequate - deterministic fixture",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordTaskDispatch(
            testsFailureGoal.Id,
            testsFailureTask.Id,
            new TaskDispatchRecord("codex-cli", "codex exec tests", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(
            testsFailureGoal.Id,
            testsFailureTask.Id,
            new TaskVerificationRecord(
                "codex exec tests",
                "C:\\repo",
                0,
                testsFailureOutput,
                string.Empty,
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true));

        var exitFailureGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Nonzero process exit");
        var exitFailureTask = exitFailureGoal.Tasks.Single();
        kernel.RecordTaskDispatch(
            exitFailureGoal.Id,
            exitFailureTask.Id,
            new TaskDispatchRecord("local", "exit 42", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(
            exitFailureGoal.Id,
            exitFailureTask.Id,
            new TaskVerificationRecord(
                "exit 42",
                "C:\\repo",
                42,
                string.Empty,
                "failed",
                DateTimeOffset.UtcNow));

        var summary = new ConductorBatchLoop(conductEventLogWriter: writer).Run(
            kernel,
            MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(2, summary.Escalated);
        var escalations = File.ReadAllLines(logPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(record => record.EventKind == "goal-escalation")
            .ToArray();
        var testsFailure = Assert.Single(escalations.Where(record => record.GoalId == testsFailureGoal.Id.Value[..8]));
        var exitFailure = Assert.Single(escalations.Where(record => record.GoalId == exitFailureGoal.Id.Value[..8]));
        Assert.Contains("succeeded-worker-result-failing-tests", testsFailure.Detail, StringComparison.Ordinal);
        Assert.Contains("unknown-failure", exitFailure.Detail, StringComparison.Ordinal);
        Assert.NotEqual(testsFailure.Detail, exitFailure.Detail);
    }

    [Xunit.Fact(DisplayName = "ConductEvents_rollover_preserves_stable_current_filename")]
    public void ConductEventsRolloverPreservesStableCurrentFilename()
    {
        var root = CreateTempDirectory("mcg-conduct-events-rollover");
        var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, new string('x', 128));

        var writer = new ConductEventLogWriter(
            logPath,
            maxBytes: 32,
            utcNow: () => DateTimeOffset.Parse("2026-07-13T02:30:00Z"));

        writer.Append("loop-stop", null, "LOOP_STOP tick=0 reason=test");

        Assert.True(File.Exists(logPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(logPath)!, "conduct-events-*.log"));
        var current = JsonSerializer.Deserialize<ConductEventRecord>(
            File.ReadAllText(logPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("loop-stop", current.EventKind);
    }
}
