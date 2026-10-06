using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStepLedgerFormatParity(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Xunit.Fact(DisplayName = "Removing the new tokens preserves every fixture progress line and dispatch decision")]
    public void FixtureLinesAndDecisionsMatchBaseline()
    {
        var (kernel, goal) = SimpleGoal("CPU accounting dispatch");
        var task = Assert.Single(goal.Tasks);
        var cpu = TimeSpan.Zero;
        var clock = new FixtureTimeProvider();
        var dispatchCalls = 0;
        var sweepCalls = 0;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: candidate =>
            {
                dispatchCalls++;
                Assert.Equal(goal.Id, candidate.Id);
                cpu += TimeSpan.FromMilliseconds(25);
                clock.Advance(TimeSpan.FromMilliseconds(25));
                return DispatchStartOutcome.Started([Assert.Single(candidate.Tasks)]);
            });
        var loop = new ConductorBatchLoop(measuredSweep: _ =>
        {
            sweepCalls++;
            cpu += TimeSpan.FromMilliseconds(40);
            clock.Advance(TimeSpan.FromMilliseconds(40));
            return null;
        }, processCpuTime: () => cpu, janitorialTimestamp: () => 0)
            .WithDiagnosticTimeProvider(clock);

        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);

        var lines = tick.ProgressLines!;
        Assert.Single(lines, line => line.Contains(" prewalk_steps="));
        Assert.Single(lines, line => line.Contains(" sweep_steps="));
        Assert.Single(lines, line => line.Contains(" load_counters="));
        // Frozen from HEAD 47a535fc formatters with deterministic diagnostic inputs.
        // Strip only the three added tokens: timestamps and every existing timing
        // field remain part of the byte-for-byte comparison.
        Assert.Equal(new[]
        {
            "PHASE_TIMING tick=1 phase=sweep elapsed_ms=40 goals=1 completed_dependencies=0 set_aside=0 dependency_metadata_ms=0 dependency_journals_read=0 sweep_git_index_ms=0 sweep_evidence_ms=0 sweep_ephemeral_ms=0 sweep_attention_ms=0 sweep_merge_evidence_ms=0 sweep_goals_ms=0 sweep_git_spawns=0 sweep_goals_swept=0 terminal_sweep_ms=0 persist_terminalizations_ms=0 recover_dispatches_ms=0 count_dispatches_ms=0 self_relaunch_drain_ms=0 canary_await_ms=0 readmit_setaside_ms=0 mark_dependencies_ms=0 reconcile_unscoped_ms=0 cpu_ms=40 ts=2026-01-01T00:00:00.0400000+00:00",
            "PHASE_TIMING tick=1 phase=prewalk elapsed_ms=0 scoped=1 candidates=1 eligible=1 deferred_intent=0 excluded_parked=0 excluded_terminal=0 cache_entries=0 cpu_ms=0 ts=2026-01-01T00:00:00.0400000+00:00",
            $"PHASE_TIMING tick=1 phase=dispatch-prep goal={goal.Id.Value[..8]} task={task.Id.Value[..8]} role=Developer elapsed_ms=25 result=Started ts=2026-01-01T00:00:00.0650000+00:00",
            $"PHASE_TIMING tick=1 phase=per-goal-walk elapsed_ms=25 goals=1 slowest={goal.Id.Value[..8]}:25ms:Executed cpu_ms=25 ts=2026-01-01T00:00:00.0650000+00:00",
            "TICK tick=1 eligible=1 ts=2026-01-01T00:00:00.0650000+00:00",
            $"GOAL goal={goal.Id.Value[..8]} result=executed state=WorkspaceReady ts=2026-01-01T00:00:00.0650000+00:00",
            "TICK_END tick=1 advanced=1 held=0 escalated=0 done=0 cpu_ms=65 cpu_sweep_ms=40 cpu_prewalk_ms=0 cpu_walk_ms=25 ts=2026-01-01T00:00:00.0650000+00:00"
        }, lines.Select(line => Regex.Replace(line, @" (?:prewalk_steps|sweep_steps|load_counters)=\S+", "")).ToArray());
        Assert.Equal(1, sweepCalls);
        Assert.Equal(1, dispatchCalls);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    private sealed class FixtureTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
