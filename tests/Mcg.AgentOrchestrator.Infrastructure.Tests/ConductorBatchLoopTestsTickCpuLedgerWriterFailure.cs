using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTickCpuLedgerWriterFailure(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void ThrowingWriterPreservesTickCountsAndProgress()
    {
        var root = CreateTempDirectory("tick-cpu-writer-failure");
        try
        {
            var success = RunTick(Path.Combine(root, "success"));
            Assert.Single(File.ReadAllLines(Path.Combine(root, "success", ".orchestrator", "tick-cpu.jsonl")));
            var calls = 0;
            BatchTickSummary failed;
            using (TickCpuLedger.PushAppender((_, _) => { calls++; throw new IOException("injected writer failure"); }))
                failed = RunTick(Path.Combine(root, "failure"));

            Assert.Equal(1, calls);
            Assert.Equal(success.Advanced, failed.Advanced);
            Assert.Equal(success.Held, failed.Held);
            Assert.Equal(success.Escalated, failed.Escalated);
            Assert.Equal(success.Done, failed.Done);
            Assert.Equal(success.Retried, failed.Retried);
            Assert.Contains(failed.ProgressLines!, line => line.Contains(" cpu_ms=65 ", StringComparison.Ordinal));
            Assert.Equal(success.ProgressLines!.Select(Normalize), failed.ProgressLines!.Select(Normalize));
            Assert.False(File.Exists(Path.Combine(root, "failure", ".orchestrator", "tick-cpu.jsonl")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static BatchTickSummary RunTick(string root)
    {
        Directory.CreateDirectory(root);
        var (kernel, _) = SimpleGoal("CPU accounting dispatch");
        var cpu = TimeSpan.Zero;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                cpu += TimeSpan.FromMilliseconds(25);
                return DispatchStartOutcome.Started([Assert.Single(goal.Tasks)]);
            });
        BatchTickSummary? captured = null;
        new ConductorBatchLoop(
            workspace: OrchestratorWorkspace.ForDirectory(root), loadTrxCoherenceVerdicts: _ => { },
            utcNow: () => new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero),
            measuredSweep: _ => { cpu += TimeSpan.FromMilliseconds(40); return null; },
            processCpuTime: () => cpu).Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                maxIterations: 1, onTick: tick => captured = tick);
        Assert.NotNull(captured);
        return captured;
    }

    // Only transport timestamps, generated goal/task identities and stopwatch timings vary between fixtures.
    private static string Normalize(string line)
    {
        line = Regex.Replace(line, @" ts=\S+$", "");
        line = Regex.Replace(line, @"\b(?<name>\w+_ms)=\d+", match =>
            match.Groups["name"].Value.StartsWith("cpu", StringComparison.Ordinal)
                ? match.Value : match.Groups["name"].Value + "=<clock>");
        line = Regex.Replace(line, @"(?<=slowest=)[a-f0-9]{8,32}:\d+ms:", "<id>:<clock>ms:");
        return Regex.Replace(line, @"\b(goal|task)=[a-f0-9]{8,32}\b", "$1=<id>");
    }
}
