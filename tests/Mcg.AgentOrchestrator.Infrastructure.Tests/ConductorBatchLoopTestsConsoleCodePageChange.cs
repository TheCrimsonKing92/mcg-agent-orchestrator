using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

public sealed class ConductorBatchLoopTestsConsoleCodePageChange(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void ChangedOutputCodePageReachesConductStreamExactlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-code-page-stream-{Guid.NewGuid():N}");
        try
        {
            var (kernel, _, _, _) = DispatchExitSweepEligibilityTests.SeedExitedRound(WorkTaskStatus.Failed, root);
            var logPath = Path.Combine(root, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var readings = new Queue<(uint Input, uint Output)>([(437, 437), (437, 437), (437, 65001), (437, 65001)]);
            var watch = new ConductorConsoleCodePageWatch(() => readings.Dequeue());
            var ticks = new List<IReadOnlyList<string>>();
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));
            AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
                measuredSweep: _ =>
                {
                    var events = watch.Observe();
                    ticks.Add(events);
                    return new TerminalGoalSweepResult([], ProgressEvents: events);
                },
                conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                    maxIterations: 3, sleepFunc: _ => false));

            Assert.Equal(3, ticks.Count);
            Assert.Empty(readings);
            Assert.Empty(ticks[0]);
            Assert.Single(ticks[1]);
            Assert.Empty(ticks[2]);
            var records = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "console-code-page-changed").ToArray();
            Assert.Equal("CONSOLE_CODE_PAGE_CHANGED detail=\"input=437->437 output=437->65001\"", Assert.Single(records).Detail);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NoConsoleAtStartupDisablesObservations()
    {
        var reads = 0;
        var watch = new ConductorConsoleCodePageWatch(() => ++reads == 1 ? null : (437u, 65001u));
        Assert.Empty(watch.Observe());
        Assert.Empty(watch.Observe());
        Assert.Equal(1, reads);
    }

    [Fact]
    public void BothAxesChangeInOneEventAndMissingReadingPreservesBaseline()
    {
        var readings = new Queue<(uint Input, uint Output)?>([(437, 437), null, (65001, 65001), (65001, 65001)]);
        var watch = new ConductorConsoleCodePageWatch(() => readings.Dequeue());
        Assert.Empty(watch.Observe());
        Assert.Equal("CONSOLE_CODE_PAGE_CHANGED detail=\"input=437->65001 output=437->65001\"", Assert.Single(watch.Observe()));
        Assert.Empty(watch.Observe());
        Assert.Empty(readings);
    }
}
