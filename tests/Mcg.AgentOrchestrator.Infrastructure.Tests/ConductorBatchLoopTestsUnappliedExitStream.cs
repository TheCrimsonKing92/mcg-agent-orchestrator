using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

public sealed class ConductorBatchLoopTestsUnappliedExitStream : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsUnappliedExitStream(ITestOutputHelper output) : base(output)
    {
    }

    [Xunit.Fact(DisplayName = "UnappliedExit_record_reaches_the_conduct_stream_once_and_distinctly")]
    public void UnappliedExitRecordReachesTheConductStreamOnceAndDistinctly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-unapplied-exit-stream-{Guid.NewGuid():N}");
        try
        {
            var (kernel, goal, task, process) = DispatchExitSweepEligibilityTests.SeedExitedRound(
                WorkTaskStatus.Failed,
                root);
            var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var watch = new ConductorUnappliedExitWatch();
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));

            AsyncLocalConsoleRouter.Capture(() =>
                new ConductorBatchLoop(
                    // The records ride the same sweep progress-event channel remediation uses; the loop
                    // must not be taught a second path to reach the operator.
                    measuredSweep: loopKernel => new TerminalGoalSweepResult([], ProgressEvents: watch.Observe(loopKernel)),
                    conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                    kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    NoStopPath(),
                    maxIterations: 5,
                    watchInterval: TimeSpan.FromMilliseconds(1),
                    sleepFunc: _ => false));

            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "exit-unapplied")
                .ToArray();

            var record = Xunit.Assert.Single(records);
            Xunit.Assert.Equal(goal.Id.Value[..8], record.GoalId);
            Xunit.Assert.Contains($"task={task.Id.Value[..8]}", record.Detail, StringComparison.Ordinal);
            Xunit.Assert.Contains($"pid={process.ProcessId}", record.Detail, StringComparison.Ordinal);
            Xunit.Assert.Contains(
                $"artifact={JsonSerializer.Serialize(process.ExitCodePath)}",
                record.Detail,
                StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("sweep-escalation", record.EventKind, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
