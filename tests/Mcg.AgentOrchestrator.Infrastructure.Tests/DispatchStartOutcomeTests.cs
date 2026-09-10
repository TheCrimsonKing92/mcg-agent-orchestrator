using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class DispatchStartOutcomeTests
{
    [Xunit.Fact(DisplayName = "DispatchStartOutcome_maps_refinement_pending_to_deferred")]
    public void MapsRefinementPendingToDeferred()
    {
        var pending = new InvalidOperationException(
            "SPEC_REFINEMENT_PENDING goal=deadbeef owner=durable-outbox executor_started=true detail=running");
        var mapped = DispatchStartOutcome.FromDispatchException(pending, "Subscription dispatch start failed");
        Assert.Equal(DispatchStartOutcomeCategory.Deferred, mapped.Category);
        Assert.Equal(pending.Message, mapped.Reason);
    }

    [Xunit.Fact(DisplayName = "DispatchStartOutcome_maps_other_exceptions_to_spawn_failed")]
    public void MapsOtherExceptionsToSpawnFailed()
    {
        var mapped = DispatchStartOutcome.FromDispatchException(
            new InvalidOperationException("worker command refused"),
            "Subscription dispatch start failed");
        Assert.Equal(DispatchStartOutcomeCategory.SpawnFailed, mapped.Category);
        Assert.Equal("Subscription dispatch start failed: worker command refused", mapped.Reason);
    }

    [Xunit.Fact(DisplayName = "DispatchStartOutcome_carries_the_actual_started_task_identity")]
    public void CarriesActualStartedTaskIdentity()
    {
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var outcome = DispatchStartOutcome.Started([developer]);

        var actual = Assert.Single(outcome.DispatchedTasks!);
        Assert.Equal(developer.Id, actual.TaskId);
        Assert.Equal(AgentRole.Developer, actual.Role);
    }
}
