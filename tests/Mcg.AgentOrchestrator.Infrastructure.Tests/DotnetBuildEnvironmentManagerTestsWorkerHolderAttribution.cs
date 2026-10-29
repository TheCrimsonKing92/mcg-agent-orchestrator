using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsWorkerHolderAttribution
{
    [Fact]
    public async Task HostBuildHolderIdentifiesGoalRoleAndTask()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        using var incumbent = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
            DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                DotnetBuildEnvironmentManager.CreateStableSlotAttempt(0, storageRoot: fixture.Storage), TimeSpan.Zero)).Lease;
        DotnetBuildLeaseAcquisition? observed = null;
        var invocation = await fixture.RunHelper(item =>
        {
            if (observed is not null || item.Observations.Last().Phase != WorkerBuildPhase.BuildSlotAcquired) return;
            var environment = DotnetBuildEnvironmentManager.CreateAttempt(fixture.GoalId, "holder-observer", storageRoot: fixture.Storage);
            observed = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero);
        });
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(observed);
        var slot = Assert.Single(busy.BusySlots.Where(s => s.SlotIndex != incumbent.Environment.BuildPermitIndex));
        Assert.Equal($"worker-build:goal-{fixture.GoalId.Value[..8]}:role-Developer:task-{fixture.TaskId.Value[..8]}", slot.HolderLabel);
        Assert.DoesNotContain("holder-unknown", DotnetBuildEnvironmentManager.FormatBusySlots([slot]));
    }
}
