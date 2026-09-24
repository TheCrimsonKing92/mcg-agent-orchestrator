using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class StructuralCoveragePermitWaitTests : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Fact]
    public void HeldPermitCompletesAfterReleaseAndPublishesProgress()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        using var holder = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(environment);
        var clock = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        var beats = 0;
        var overrides = new GoalAcceptanceVerifierTestOverrides
        {
            StructuralCoveragePermitWaitBound = TimeSpan.FromSeconds(1),
            StructuralCoveragePermitWaitHeartbeatInterval = TimeSpan.FromMilliseconds(1),
            OnStructuralCoveragePermitWaitForTests = beat =>
            {
                beats++;
                Assert.True(File.Exists(beat.HeartbeatPath));
                holder.Dispose();
            }
        };

        using (StructuralCoveragePermitWait.Acquire(
            environment, null, StorageRoot, overrides, progress.Add, clock,
            duration => clock.Advance(duration), null, CancellationToken.None))
        {
            Assert.Equal(1, beats);
            Assert.Contains(progress, beat => beat.Phase == StructuralCoveragePermitWait.PhaseName && beat.SlotIndex == 0);
        }

        Assert.True(RootedDotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(StorageRoot, 0));
    }

    [Fact]
    public void HeldPermitTimeoutHasDistinctReasonAndHolder()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        using var holder = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(environment);
        var clock = new RecordingTimeProvider();
        var progress = new List<AcceptanceGateProgress>();
        var overrides = new GoalAcceptanceVerifierTestOverrides
        {
            StructuralCoveragePermitWaitBound = TimeSpan.FromMilliseconds(100),
            StructuralCoveragePermitWaitHeartbeatInterval = TimeSpan.FromMilliseconds(1)
        };

        var fault = Assert.Throws<AcceptanceInfrastructureDeferredException>(() =>
            StructuralCoveragePermitWait.Acquire(
                environment, null, StorageRoot, overrides, progress.Add, clock,
                duration => clock.Advance(duration), null, CancellationToken.None));

        Assert.Equal(StructuralCoveragePermitWait.UnavailableReasonCode, fault.ReasonCode);
        Assert.Contains("build-0", fault.Message, StringComparison.Ordinal);
        Assert.Contains($"pid-{Environment.ProcessId}", fault.Message, StringComparison.Ordinal);
        Assert.Contains(progress, beat => beat.Phase == StructuralCoveragePermitWait.PhaseName);
        Assert.All(progress, beat => Assert.False(File.Exists(beat.HeartbeatPath)));
    }
}
