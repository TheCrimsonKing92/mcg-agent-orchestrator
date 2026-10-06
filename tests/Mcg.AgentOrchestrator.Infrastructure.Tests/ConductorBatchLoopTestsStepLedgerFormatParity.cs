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
        var dispatchCalls = 0;
        var sweepCalls = 0;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: candidate =>
            {
                dispatchCalls++;
                Assert.Equal(goal.Id, candidate.Id);
                cpu += TimeSpan.FromMilliseconds(25);
                return DispatchStartOutcome.Started([Assert.Single(candidate.Tasks)]);
            });
        var loop = new ConductorBatchLoop(measuredSweep: _ =>
        {
            sweepCalls++;
            cpu += TimeSpan.FromMilliseconds(40);
            return null;
        }, processCpuTime: () => cpu, janitorialTimestamp: () => 0);

        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);

        var lines = tick.ProgressLines!;
        Assert.Single(lines, line => line.Contains(" prewalk_steps="));
        Assert.Single(lines, line => line.Contains(" sweep_steps="));
        Assert.Single(lines, line => line.Contains(" load_counters="));
        // Frozen from HEAD 47a535fc formatters and the pre-existing CPU fixture.
        // Real time and generated identities are transport/fixture variability;
        // CPU, decisions, field order and every remaining byte are pinned.
        string Normalize(string line)
        {
            line = Regex.Replace(line, @" (?:prewalk_steps|sweep_steps|load_counters)=\S+", "");
            line = Regex.Replace(line, @" ts=\S+$", "");
            line = Regex.Replace(line, @"\b(?:elapsed_ms|dependency_metadata_ms)=\d+", match => match.Value.Split('=')[0] + "=<n>");
            line = Regex.Replace(line, @":\d+ms:", ":<n>ms:");
            return line.Replace(goal.Id.Value[..8], "<goal>", StringComparison.Ordinal)
                .Replace(task.Id.Value[..8], "<task>", StringComparison.Ordinal);
        }
        Assert.Equal(new[]
        {
            "PHASE_TIMING tick=1 phase=sweep elapsed_ms=<n> goals=1 completed_dependencies=0 set_aside=0 dependency_metadata_ms=<n> dependency_journals_read=0 sweep_git_index_ms=0 sweep_evidence_ms=0 sweep_ephemeral_ms=0 sweep_attention_ms=0 sweep_merge_evidence_ms=0 sweep_goals_ms=0 sweep_git_spawns=0 sweep_goals_swept=0 terminal_sweep_ms=0 persist_terminalizations_ms=0 recover_dispatches_ms=0 count_dispatches_ms=0 self_relaunch_drain_ms=0 canary_await_ms=0 readmit_setaside_ms=0 mark_dependencies_ms=0 reconcile_unscoped_ms=0 cpu_ms=40",
            "PHASE_TIMING tick=1 phase=prewalk elapsed_ms=<n> scoped=1 candidates=1 eligible=1 deferred_intent=0 excluded_parked=0 excluded_terminal=0 cache_entries=0 cpu_ms=0",
            "PHASE_TIMING tick=1 phase=dispatch-prep goal=<goal> task=<task> role=Developer elapsed_ms=<n> result=Started",
            "PHASE_TIMING tick=1 phase=per-goal-walk elapsed_ms=<n> goals=1 slowest=<goal>:<n>ms:Executed cpu_ms=25",
            "TICK tick=1 eligible=1",
            "GOAL goal=<goal> result=executed state=WorkspaceReady",
            "TICK_END tick=1 advanced=1 held=0 escalated=0 done=0 cpu_ms=65 cpu_sweep_ms=40 cpu_prewalk_ms=0 cpu_walk_ms=25"
        }, lines.Select(Normalize).ToArray());
        Assert.Equal(1, sweepCalls);
        Assert.Equal(1, dispatchCalls);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }
}
