using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class ConductorLoopHandoffTests
{
    [Fact(DisplayName = "ConductorLoopHandoff_alive_successor_waits_past_legacy_timeout_until_loop_start")]
    public void AliveSuccessorWaitsPastLegacyTimeoutUntilLoopStart()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-handoff-alive");
        try
        {
            var legacyTimeout = TimeSpan.FromMilliseconds(100);
            var loopStartDelay = TimeSpan.FromMilliseconds(450);
            var stopwatch = Stopwatch.StartNew();
            ConductorLoopHandoffResult? result = null;

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                result = ConductorLoopHandoff.TryStartSuccessor(
                    HandoffOptions(
                        root,
                        verificationTimeout: legacyTimeout,
                        verificationHardTimeout: TimeSpan.FromSeconds(2),
                        loopStartProbe: (_, _) => stopwatch.Elapsed >= loopStartDelay),
                    new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                    request =>
                    {
                        File.WriteAllText(request.StdoutPath, "successor booting");
                        return new ConductLoopLaunchResult(Environment.ProcessId, request.StdoutPath, request.StderrPath);
                    });
            });

            Assert.True(result!.Started);
            Assert.Contains("terminalReason=loop-start", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF_PENDING", output, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_HANDOFF_FAILED", output, StringComparison.Ordinal);

            var records = ReadHandoffRecords(root);
            Assert.Contains(records, record => record.Status == "Pending");
            Assert.DoesNotContain(records, record => record.Status == "Failed");

            var conductEventsPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
            Assert.Contains("LOOP_HANDOFF_PENDING", File.ReadAllText(conductEventsPath), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "ConductorLoopHandoff_dead_successor_fails_promptly_without_loop_start")]
    public void DeadSuccessorFailsPromptlyWithoutLoopStart()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-handoff-dead");
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = ConductorLoopHandoff.TryStartSuccessor(
                HandoffOptions(root),
                new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                request => new ConductLoopLaunchResult(int.MaxValue, request.StdoutPath, request.StderrPath));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.False(result.Started);
            Assert.True(result.Failed);
            Assert.Equal("successor-child-dead", result.Reason);
            Assert.Contains("terminalReason=child-dead", result.VerificationOutcome, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact(DisplayName = "ConductorLoopHandoff_alive_successor_hard_ceiling_reports_alive_timeout")]
    public void AliveSuccessorHardCeilingReportsAliveTimeout()
    {
        var root = CreateTempDirectory("mcg-conduct-loop-handoff-hard-timeout");
        try
        {
            ConductorLoopHandoffResult? result = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                result = ConductorLoopHandoff.TryStartSuccessor(
                    HandoffOptions(
                        root,
                        verificationTimeout: TimeSpan.FromMilliseconds(50),
                        verificationHardTimeout: TimeSpan.FromMilliseconds(300),
                        loopStartProbe: (_, _) => false),
                    new ConductorLoopHandoffRequest(12, TimeSpan.FromHours(4), 0),
                    request =>
                    {
                        File.WriteAllText(request.StdoutPath, "successor booting");
                        return new ConductLoopLaunchResult(Environment.ProcessId, request.StdoutPath, request.StderrPath);
                    });
            });

            Assert.False(result!.Started);
            Assert.True(result.Failed);
            Assert.Equal("successor-alive-timeout", result.Reason);
            Assert.Contains("processAlive=true", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("terminalReason=alive-timeout", result.VerificationOutcome, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF_PENDING", output, StringComparison.Ordinal);
            Assert.Contains("LOOP_HANDOFF_FAILED", output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static ConductLoopHandoffOptions HandoffOptions(
        string root,
        TimeSpan verificationTimeout = default,
        TimeSpan verificationHardTimeout = default,
        Func<ConductLoopHandoffOptions, long, bool>? loopStartProbe = null) =>
        new(
            Args: ["conduct", "--loop", "--watch", "--max-duration", "14400"],
            ExecutionDirectory: root,
            OrchestratorDirectory: Path.Combine(root, ".orchestrator"),
            LogDirectory: Path.Combine(root, ".orchestrator", "logs"),
            RunEventStorePath: Path.Combine(root, ".orchestrator", "run-events.db"),
            StopFilePath: Path.Combine(root, ConductorBatchLoop.StopFileName),
            RenewalCount: 0,
            MaxRenewals: ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding,
            ReleaseCurrentLease: () => { },
            VerificationTimeout: verificationTimeout,
            VerificationHardTimeout: verificationHardTimeout,
            LoopStartProbe: loopStartProbe);

    private static IReadOnlyList<RunEventRecord> ReadHandoffRecords(string root) =>
        new SqliteRunEventStore(Path.Combine(root, ".orchestrator", "run-events.db"))
            .ReadSinceAsync()
            .GetAwaiter()
            .GetResult()
            .Where(record => record.Operation == "LOOP_HANDOFF")
            .ToArray();

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
