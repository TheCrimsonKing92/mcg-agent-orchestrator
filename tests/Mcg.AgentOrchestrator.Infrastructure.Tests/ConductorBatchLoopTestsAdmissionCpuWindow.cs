using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsAdmissionCpuWindow(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void AdmissionProbe_ChargesOnlyAdmission_AndWalkProbeChargesOnlyWalk()
    {
        var baseline = RunFixture(false, false);
        var admission = RunFixture(true, false);
        var walk = RunFixture(false, true);
        var admissionLine = Phase(admission, "acceptance-admission");
        Assert.Equal(30, Cpu(admissionLine, "cpu_ms"));
        Assert.Contains("admission_steps=attempt-census:30:1,", admissionLine);
        foreach (var field in new[] { "cpu_sweep_ms", "cpu_prewalk_ms", "cpu_walk_ms" })
            Assert.Equal(Cpu(End(baseline), field), Cpu(End(admission), field));
        Assert.Equal(Cpu(End(admission), "cpu_sweep_ms") + Cpu(End(admission), "cpu_prewalk_ms") +
            Cpu(End(admission), "cpu_walk_ms") + 30, Cpu(End(admission), "cpu_ms"));
        Assert.Equal(0, Cpu(Phase(walk, "acceptance-admission"), "cpu_ms"));
        Assert.Equal(Cpu(End(baseline), "cpu_walk_ms") + 30, Cpu(End(walk), "cpu_walk_ms"));
        Assert.Equal(Cpu(End(baseline), "cpu_sweep_ms"), Cpu(End(walk), "cpu_sweep_ms"));
        Assert.Equal(Cpu(End(baseline), "cpu_prewalk_ms"), Cpu(End(walk), "cpu_prewalk_ms"));
        foreach (var tick in new[] { baseline, admission, walk })
        {
            var lines = tick.ProgressLines!.ToList();
            Assert.True(lines.IndexOf(Phase(tick, "prewalk")) < lines.IndexOf(Phase(tick, "acceptance-admission")));
            Assert.True(lines.IndexOf(Phase(tick, "acceptance-admission")) < lines.IndexOf(Phase(tick, "per-goal-walk")));
        }
    }

    private static BatchTickSummary RunFixture(bool admissionProbe, bool walkProbe)
    {
        var (kernel, _) = SimpleGoal("Admission CPU accounting");
        var cpu = TimeSpan.Zero;
        var clock = new FixtureTimeProvider();
        var dispatchCalls = 0;
        var probeCalls = 0;
        void AddCpu(int milliseconds)
        {
            cpu += TimeSpan.FromMilliseconds(milliseconds);
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        }
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                dispatchCalls++;
                AddCpu(walkProbe ? 55 : 25);
                return DispatchStartOutcome.Started([Assert.Single(goal.Tasks)]);
            });
        var loop = new ConductorBatchLoop(measuredSweep: _ => { AddCpu(40); return null; },
            processCpuTime: () => cpu, janitorialTimestamp: () => 0).WithDiagnosticTimeProvider(clock)
            .WithStepProbe(name =>
            {
                if (name != "attempt-census") return;
                probeCalls++;
                if (admissionProbe) AddCpu(30);
            });
        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        Assert.Equal(1, dispatchCalls);
        Assert.Equal(1, probeCalls);
        return tick;
    }

    private static string Phase(BatchTickSummary tick, string phase) => Assert.Single(tick.ProgressLines!,
        line => line.StartsWith($"PHASE_TIMING tick=1 phase={phase} ", StringComparison.Ordinal));
    private static string End(BatchTickSummary tick) => Assert.Single(tick.ProgressLines!,
        line => line.StartsWith("TICK_END tick=1 ", StringComparison.Ordinal));
    private static long Cpu(string line, string field) =>
        long.Parse(Regex.Match(line, $@"\b{field}=(\d+)").Groups[1].Value);

    private sealed class FixtureTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
