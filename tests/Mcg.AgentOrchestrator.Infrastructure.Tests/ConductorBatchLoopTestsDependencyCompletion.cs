using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsDependencyCompletion : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsDependencyCompletion(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dependent_goal_advances_when_completed_dependency_is_metadata_only")]
    public void BatchLoopDependentGoalAdvancesWhenCompletedDependencyIsMetadataOnly()
    {
        var completedDependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active dependent goal");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [completedDependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownCompletedDependencyGoals([completedDependencyId]);

        var createdWorkspaces = new List<GoalId>();
        var driver = MakeDriver(createWorkspace: goal =>
        {
            createdWorkspaces.Add(goal.Id);
            return "/tmp/workspace";
        });

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Advanced);
        Assert.Contains(active.Id, createdWorkspaces);
        Assert.Equal(0, summary.Held);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dependent_goal_holds_when_parked_dependency_is_metadata_only")]
    public void BatchLoopDependentGoalHoldsWhenParkedDependencyIsMetadataOnly()
    {
        var parkedDependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active dependent goal");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [parkedDependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(parkedDependencyId, GoalStatus.Parked.ToString())
        ]);

        var createdWorkspaces = new List<GoalId>();
        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(createWorkspace: goal =>
            {
                createdWorkspaces.Add(goal.Id);
                return "/tmp/workspace";
            }),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.DoesNotContain(kernel.Goals, goal => goal.Id == parkedDependencyId);
        Assert.Equal(1, summary.Held);
        Assert.Empty(createdWorkspaces);
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dependent_goal_holds_when_dependency_is_completed_without_landing")]
    public void BatchLoopDependentGoalHoldsWhenDependencyIsCompletedWithoutLanding()
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "active dependent goal");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, GoalStatus.Completed.ToString())
        ]);

        var createdWorkspaces = new List<GoalId>();
        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(createWorkspace: goal =>
            {
                createdWorkspaces.Add(goal.Id);
                return "/tmp/workspace";
            }),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.DoesNotContain(kernel.Goals, goal => goal.Id == dependencyId);
        Assert.False(kernel.IsKnownCompletedDependencyGoal(dependencyId));
        Assert.Equal(0, summary.Advanced);
        Assert.Equal(1, summary.Held);
        Assert.Empty(createdWorkspaces);
    }

    [Xunit.Theory(DisplayName = "BatchLoop_terminal_unlanded_dependency_escalates_without_worker_start")]
    [Xunit.InlineData("Failed")]
    [Xunit.InlineData("Retired")]
    public void BatchLoopTerminalUnlandedDependencyEscalatesWithoutWorkerStart(string terminalStatus)
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "dependent");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, terminalStatus)
        ]);
        var workerStarts = 0;
        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(
                createWorkspace: _ => throw new Xunit.Sdk.XunitException("held goal must not create a workspace"),
                dispatchAndStart: _ =>
                {
                    workerStarts++;
                    return DispatchStartOutcome.Started();
                }),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(0, workerStarts);
        Assert.Equal(1, summary.Escalated);
        Assert.Contains(
            kernel.GetGoal(active.Id).Timeline,
            progress => progress.Message.Contains(
                $"dependency-terminal-without-landing: {dependencyId.Value[..8]} state={terminalStatus}",
                StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_dependency_added_after_goal_start_does_not_retroactively_hold_goal")]
    public void BatchLoopDependencyAddedAfterGoalStartDoesNotRetroactivelyHoldGoal()
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "already started dependent");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        var rehydrated = kernel.GetGoal(active.Id);
        StartProcess(
            kernel,
            rehydrated,
            rehydrated.Tasks.Single(),
            DateTimeOffset.Parse("2026-07-30T00:00:00Z"),
            "base");
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, GoalStatus.Completed.ToString())
        ]);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(createWorkspace: _ => throw new Xunit.Sdk.XunitException("running goal must not recreate a workspace")),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(0, summary.Escalated);
        Assert.Equal(1, summary.Held);
        Assert.DoesNotContain(
            kernel.GetGoal(active.Id).Timeline,
            progress => progress.Message.Contains("dependency-terminal-without-landing", StringComparison.Ordinal));
        Assert.DoesNotContain(
            kernel.GetGoal(active.Id).Timeline,
            progress => progress.Message.Contains("waiting on dependency", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BatchLoop_landed_dependency_remains_satisfied_after_terminal_metadata_changes")]
    public void BatchLoopLandedDependencyRemainsSatisfiedAfterTerminalMetadataChanges()
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "dependent");
        kernel.ReplaceWithSnapshot(kernel.ExportSnapshot() with
        {
            Goals = kernel.ExportSnapshot().Goals
                .Select(goal => goal.Id == active.Id.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownCompletedDependencyGoals([dependencyId]);
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, GoalStatus.Superseded.ToString())
        ]);
        var createdWorkspaces = new List<GoalId>();

        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(createWorkspace: goal =>
            {
                createdWorkspaces.Add(goal.Id);
                return "/tmp/workspace";
            }),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
        Assert.Contains(active.Id, createdWorkspaces);
    }
}
