using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsAttentionAnswerIntent : CliCommandTestBase
{
    [Xunit.Fact]
    public async Task Attention_answer_queues_until_tick_then_rejects_answered_item()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Answer by intent");
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var item = await collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
            "Scope", "Which scope?", $"spec-clarification:{goal.Id.Value}:scope:answer-intent")
            .WaitAsync(TimeSpan.FromSeconds(30));

        var output = ExecuteCliAndCapture(
            ["attention", "answer", goal.Id.Value[..8], item.Id[..8], "Use selected scope"],
            kernel, workspace);
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var pending = Xunit.Assert.Single(await intents.ListForGoalAsync(goal.Id.Value).WaitAsync(TimeSpan.FromSeconds(30)));
        Xunit.Assert.Contains($"id={pending.Id} verb=answer", output);
        Xunit.Assert.Contains("status=Pending", output);
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, pending.Status);
        Xunit.Assert.Equal(OperatorActorKind.Human, pending.ActorKind);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised,
            (await collaboration.ListAsync(goal.Id.Value).WaitAsync(TimeSpan.FromSeconds(30))).Single().Status);

        var coordinator = OperatorIntentCoordinator.CreateDefault(workspace);
        Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
        coordinator.CompletePersisted([goal.Id]);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied,
            (await intents.GetAsync(pending.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);

        var duplicateOutput = ExecuteCliAndCapture(
            ["attention", "answer", goal.Id.Value[..8], item.Id[..8], "Another scope",
                "--actor-kind", "Agent", "--operator-actor", "author-agent"],
            kernel, workspace);
        Xunit.Assert.Contains("status=Pending", duplicateOutput);
        var duplicate = (await intents.ListForGoalAsync(goal.Id.Value).WaitAsync(TimeSpan.FromSeconds(30)))
            .Single(intent => intent.Id != pending.Id);
        Xunit.Assert.Equal(OperatorActorKind.Agent, duplicate.ActorKind);
        coordinator.ExecutePending(kernel, goal);
        var rejected = await intents.GetAsync(duplicate.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Xunit.Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
        Xunit.Assert.Contains("already answered", rejected.Outcome);
        Xunit.Assert.Null(await collaboration.GetDecisionStateAsync($"answer-{duplicate.Id}")
            .WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
