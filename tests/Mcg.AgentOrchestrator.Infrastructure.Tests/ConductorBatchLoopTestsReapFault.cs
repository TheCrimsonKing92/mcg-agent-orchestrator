using System.ComponentModel;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsReapFault : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsReapFault(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void Run_AdvanceAndReapFault_AdvancesHealthyGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var faulty = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, DefaultAgents(), "faulty goal");
        var healthy = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, DefaultAgents(), "healthy goal");
        var advanced = new List<GoalId>();
        var reapCalls = new List<GoalId>();
        var lines = new List<string>();
        var driver = MakeDriver(
            getFacts: goal => goal.Id == faulty.Id
                ? throw new InvalidOperationException("advance failed")
                : new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                advanced.Add(goal.Id);
                return DispatchStartOutcome.Started();
            });

        var summary = new ConductorBatchLoop(reapGoalRunningDispatches: (_, goal) =>
        {
            reapCalls.Add(goal.Id);
            if (goal.Id == faulty.Id)
                throw new Win32Exception(5);
        }, detachGoalRunningDispatches: (_, _) => { }).Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1, onTick: tick => lines.AddRange(tick.ProgressLines ?? []));

        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Escalated);
        Assert.Contains(healthy.Id, advanced);
        Assert.Equal(new[] { faulty.Id }, reapCalls);
        var fault = Assert.Single(lines.Where(line => line.StartsWith("REAP_FAULT ")));
        Assert.StartsWith($"REAP_FAULT tick=1 goal={faulty.Id.Value[..8]} exception=Win32Exception message=", fault);
        Assert.DoesNotContain('\n', fault);
        Assert.DoesNotContain('\r', fault);
        Assert.Single(kernel.GetGoal(faulty.Id).Timeline.Where(item =>
            item.Kind == ProgressKind.GoalPolicyDecision && item.Message.Contains("REAP_FAULT")));
    }

    [Fact]
    public void Run_FatalReapRecordWrite_RethrowsOriginalException()
    {
        var (kernel, goal) = SimpleGoal("fatal reap");
        var expected = new DispatchRecordWriteException(
            DispatchRecordWriteFailureCause.Unrecoverable,
            DispatchRecordCheckpointPhase.ProcessMayHaveStarted,
            "dispatch-start", goal.Id, goal.Tasks.Single().Id, 8,
            new SqliteException("reap persistence failed", 8));
        var driver = MakeDriver(getFacts: _ => throw new InvalidOperationException("advance failed"));

        var actual = Assert.Throws<DispatchRecordWriteException>(() =>
            new ConductorBatchLoop(reapGoalRunningDispatches: (_, _) => throw expected)
                .Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                    maxIterations: 1));

        Assert.Same(expected, actual);
        Assert.DoesNotContain(kernel.GetGoal(goal.Id).Timeline,
            item => item.Message.Contains("REAP_FAULT"));
    }
}
