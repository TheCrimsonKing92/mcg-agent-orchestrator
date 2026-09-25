using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchReadinessRulesCandidateExclusionTests
{
    [Xunit.Fact]
    public void ExcludingOneReadyRoleTaskSelectsOtherReadyTask()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = new TaskSpec(TaskId.New(), "First independent change", AgentRole.Developer);
        var second = new TaskSpec(TaskId.New(), "Second independent change", AgentRole.Developer);
        var goal = kernel.CreateGoal("Select remaining task", [first, second]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);

        var batch = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(
            goal, agents, excludedTaskIds: new HashSet<TaskId> { first.Id });

        Xunit.Assert.DoesNotContain(first.Id, batch.TaskIds);
        Xunit.Assert.Contains(second.Id, batch.TaskIds);
    }
}
