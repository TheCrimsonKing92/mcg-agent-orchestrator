using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class DotnetBuildEnvironmentManagerTestsSlotsBusyLiveness : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Fact]
    public void SlotsBusyReportsHeldPermitButOmitsReleasedMetadata()
    {
        var heldEnvironment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        var releasedEnvironment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 1);
        using var holder = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(heldEnvironment);
        using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(releasedEnvironment))
        {
        }
        Assert.Contains(Environment.ProcessId.ToString(),
            File.ReadAllText(releasedEnvironment.ExecutionLockPath), StringComparison.Ordinal);
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () =>
            new ProcessCommandLineSnapshot(new Dictionary<int, string>());
        try
        {
            DotnetBuildSlotsBusyException? fault = null;
            var output = AsyncLocalConsoleRouter.Capture(() =>
                fault = Assert.Throws<DotnetBuildSlotsBusyException>(() =>
                    DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                        heldEnvironment, timeout: TimeSpan.Zero)));

            Assert.NotNull(fault);
            Assert.Contains(fault.SlotsBusy.BusySlots,
                slot => slot.SlotIndex == 0 && slot.OwnerProcessId == Environment.ProcessId);
            Assert.DoesNotContain(fault.SlotsBusy.BusySlots, slot => slot.SlotIndex == 1);
            Assert.Contains($"slot-0:pid-{Environment.ProcessId}", output, StringComparison.Ordinal);
            Assert.DoesNotContain("slot-1:", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
        }
    }

    [Fact]
    public void SlotsBusyUsesNoneWhenNoExecutionLockIsHeld()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        File.WriteAllText(environment.ExecutionLockPath, Environment.ProcessId.ToString());
        using var priority = new FileStream(environment.ExecutionLockPath + ".acceptance-priority.lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        priority.Lock(0, 1);
        DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () =>
            new ProcessCommandLineSnapshot(new Dictionary<int, string>());
        try
        {
            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var fault = Assert.Throws<DotnetBuildSlotsBusyException>(() =>
                    DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(
                        environment, timeout: TimeSpan.Zero));
                Assert.Empty(fault.SlotsBusy.BusySlots);
            });
            Assert.Contains("busySlots=none", output, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = null;
            priority.Unlock(0, 1);
        }
    }
}
