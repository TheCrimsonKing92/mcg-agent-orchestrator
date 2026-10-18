using Mcg.AgentOrchestrator.Core;

public sealed class ProviderHoldSubstitutionEventTests
{
    [Xunit.Theory]
    [Xunit.InlineData("stderr.receipt", "stderr.receipt")]
    [Xunit.InlineData("", "none")]
    public void SubstitutionPersistsOneEventAndOnlyChangesTheTarget(string receipt, string expected)
    {
        var kernel = new AgentOrchestratorKernel();
        var primary = ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.6");
        var alternate = ProviderHoldAgentSubstitutionTests.Scenario.Agent("openai-tester", "OpenAI", "codex-cli", "gpt-6.1-sol");
        var goal = kernel.CreateGoal("Persist routing.", [
            new TaskSpec(TaskId.New(), "First", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Sibling", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, [primary, alternate]);
        var task = goal.Tasks[0];
        var sibling = goal.Tasks[1];
        var revision = task.ConductorRoutingRevision;
        var siblingRevision = sibling.ConductorRoutingRevision;

        kernel.SubstituteProviderHeldTaskAgent(goal.Id, task.Id, alternate, "xai::<provider-default>", receipt);

        var evt = Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.Equal(task.Id, evt.TaskId);
        Assert.Equal($"held binding=xai::<provider-default>; held agent=xai-tester; substitute=openai-tester; receipt={expected}", evt.Message);
        Assert.Equal(ProgressKind.TaskRedelegated, goal.Timeline.TakeWhile(item => item != evt).Last().Kind);
        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Equal(revision + 1, task.ConductorRoutingRevision);
        Assert.Equal(primary.Id, sibling.AssignedAgentId);
        Assert.Equal(siblingRevision, sibling.ConductorRoutingRevision);

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredGoal = restored.GetGoal(goal.Id);
        Assert.Equal(evt, Assert.Single(restoredGoal.Timeline, item => item.Kind == ProgressKind.ProviderHoldSubstituted));
        Assert.Equal(alternate.Id, restored.GetTask(goal.Id, task.Id).AssignedAgentId);
        Assert.Equal(revision + 1, restored.GetTask(goal.Id, task.Id).ConductorRoutingRevision);
        Assert.Equal(primary.Id, restored.GetTask(goal.Id, sibling.Id).AssignedAgentId);
        Assert.Throws<InvalidOperationException>(() => restored.SubstituteProviderHeldTaskAgent(
            goal.Id, task.Id, primary, "openai::<provider-default>", receipt));
        Assert.Single(restoredGoal.Timeline, item => item.Kind == ProgressKind.ProviderHoldSubstituted);
    }
}
