using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: unique fixture-owned filesystem/SQLite roots; injected clock; no model calls.
public sealed class SpecClarificationStoreReferenceDispatchTests
{
    [Fact]
    public async Task PersistedTaskAnswer_NextDispatch_DeliversVerifiedArtifact()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        const string record = "Clarification evidence: café and a second line.\nrecord sentinel";
        var source = fixture.WriteSource("operator-evidence/answer.md", record);
        var (kernel, goal, task) = fixture.CreateGoal("Implement scoped behavior without copied evidence.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = fixture.Answer(kernel, goal, task.Id, "answer");

        kernel = await fixture.Reload(kernel);
        goal = kernel.GetGoal(goal.Id);
        task = goal.FindTask(task.Id);
        var restored = Assert.Single(kernel.HumanInputRequests, item => item.Id == request.Id);
        Assert.True(restored.IsCompleted);
        Assert.Equal(task.Id, restored.TaskId);
        Assert.Equal(request.AuthoritativeAnswer, restored.AuthoritativeAnswer);
        Assert.DoesNotContain("store-ref:", goal.AuthoritativeBrief.Text);
        Assert.DoesNotContain(goal.Tasks, item => item.RequiredRole == AgentRole.Planner);

        var prepared = fixture.Prepare(kernel, goal, task);

        fixture.AssertDelivered(prepared, goal, "answer", source, record);
        Assert.Empty(fixture.Diagnostics(goal));
    }
}
