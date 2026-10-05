using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTickCpuAccounting : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsTickCpuAccounting(ITestOutputHelper output) : base(output)
    {
    }

    [Xunit.Fact]
    public void TickCpuAccounting_ReportsInjectedPhaseAndTickDeltas()
    {
        var (kernel, _) = SimpleGoal("CPU accounting dispatch");
        var cpu = TimeSpan.Zero;
        var dispatchCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                dispatchCalls++;
                cpu += TimeSpan.FromMilliseconds(25);
                return DispatchStartOutcome.Started([Assert.Single(goal.Tasks)]);
            });
        var sweepCalls = 0;
        var loop = new ConductorBatchLoop(
            measuredSweep: _ =>
            {
                sweepCalls++;
                cpu += TimeSpan.FromMilliseconds(40);
                return null;
            },
            processCpuTime: () => cpu);

        var tick = RunOneTick(loop, kernel, driver);

        Assert.Equal(1, sweepCalls);
        Assert.Equal(1, dispatchCalls);
        Assert.EndsWith(" cpu_ms=40", PhaseLine(tick, "sweep"));
        Assert.EndsWith(" cpu_ms=0", PhaseLine(tick, "prewalk"));
        Assert.EndsWith(" cpu_ms=25", PhaseLine(tick, "per-goal-walk"));
        Assert.EndsWith(" cpu_ms=65 cpu_sweep_ms=40 cpu_prewalk_ms=0 cpu_walk_ms=25", SummaryLine(tick));
    }

    [Xunit.Fact]
    public void TickCpuAccounting_ResetsDeltasEachTick()
    {
        var (kernel, _) = SimpleGoal("CPU accounting across ticks");
        var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Parked CPU accounting goal");
        kernel.ParkGoal(parked.Id, "operator deferred");
        var cpu = TimeSpan.Zero;
        var sweepCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                var task = Assert.Single(goal.Tasks);
                kernel.RecordTaskDispatch(goal.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });
        var ticks = new List<BatchTickSummary>();

        new ConductorBatchLoop(
            measuredSweep: _ =>
            {
                cpu += TimeSpan.FromMilliseconds(++sweepCalls == 1 ? 40 : 10);
                return null;
            },
            processCpuTime: () => cpu).Run(
                kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                maxIterations: 2, onTick: ticks.Add);

        Assert.Equal(2, sweepCalls);
        Assert.Equal(2, ticks.Count);
        var firstTick = Assert.Single(ticks, tick => tick.Tick == 1);
        var secondTick = Assert.Single(ticks, tick => tick.Tick == 2);
        Assert.EndsWith(" cpu_ms=40", PhaseLine(firstTick, "sweep"));
        Assert.EndsWith(" cpu_ms=10", PhaseLine(secondTick, "sweep"));
        Assert.EndsWith(" cpu_ms=10 cpu_sweep_ms=10 cpu_prewalk_ms=0 cpu_walk_ms=0", SummaryLine(secondTick));
    }

    [Xunit.Fact]
    public void TickCpuAccounting_ClampsRegressingSourceToZero()
    {
        var (kernel, _) = SimpleGoal("CPU accounting regressing source");
        var cpu = TimeSpan.FromMilliseconds(20);
        var sweepCalls = 0;
        var loop = new ConductorBatchLoop(
            measuredSweep: _ =>
            {
                sweepCalls++;
                cpu -= TimeSpan.FromMilliseconds(5);
                return null;
            },
            processCpuTime: () => cpu);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal => DispatchStartOutcome.Started([Assert.Single(goal.Tasks)]));

        var tick = RunOneTick(loop, kernel, driver);

        Assert.Equal(1, sweepCalls);
        Assert.EndsWith(" cpu_ms=0", PhaseLine(tick, "sweep"));
        Assert.EndsWith(" cpu_ms=0 cpu_sweep_ms=0 cpu_prewalk_ms=0 cpu_walk_ms=0", SummaryLine(tick));
        Assert.DoesNotContain(tick.ProgressLines!, line => Regex.IsMatch(line, @"cpu(?:_\w+)?_ms=-"));
    }

    [Xunit.Fact]
    public void TickCpuAccounting_DefaultSourceEmitsNonnegativeFields()
    {
        var (kernel, _) = SimpleGoal("CPU accounting default source");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal => DispatchStartOutcome.Started([Assert.Single(goal.Tasks)]));

        var tick = RunOneTick(new ConductorBatchLoop(), kernel, driver);

        Assert.Matches(@"cpu_ms=\d+ cpu_sweep_ms=\d+ cpu_prewalk_ms=\d+ cpu_walk_ms=\d+$", SummaryLine(tick));
    }

    private static BatchTickSummary RunOneTick(ConductorBatchLoop loop, AgentOrchestratorKernel kernel, ConductorDriver driver)
    {
        BatchTickSummary? captured = null;
        loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1, onTick: tick => captured = tick);
        Assert.NotNull(captured);
        return captured;
    }

    private static string PhaseLine(BatchTickSummary tick, string phase) =>
        Unstamped(Assert.Single(tick.ProgressLines!, line =>
            line.StartsWith($"PHASE_TIMING tick={tick.Tick} phase={phase} ", StringComparison.Ordinal)));

    private static string SummaryLine(BatchTickSummary tick) =>
        Unstamped(Assert.Single(tick.ProgressLines!, line =>
            line.StartsWith($"TICK_END tick={tick.Tick} ", StringComparison.Ordinal) ||
            line.StartsWith($"TICK_SUMMARY tick={tick.Tick} ", StringComparison.Ordinal)));

    // EmitProgress appends a transport timestamp after the loop's own fields.
    private static string Unstamped(string line) => Regex.Replace(line, @" ts=\S+$", "");
}
