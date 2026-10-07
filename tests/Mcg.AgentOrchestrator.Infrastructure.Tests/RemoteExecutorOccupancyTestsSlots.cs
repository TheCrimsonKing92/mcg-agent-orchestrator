using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its occupancy root and releases every handle.
public sealed class RemoteExecutorOccupancyTestsSlots
{
    [Fact]
    public void SlotOne_IndependentFromLegacyClaimAndExclusiveUntilDisposed()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            using var legacy = RemoteExecutorOccupancy.TryClaimExclusive(root, "one");
            Assert.NotNull(legacy);
            var slot = RemoteExecutorOccupancy.TryClaimExclusive(root, "one", 1);
            Assert.NotNull(slot);
            using (slot)
            {
                Assert.True(File.Exists(Path.Combine(root, ".orchestrator", "remote-executor-occupancy", "one", "slot-1.lock")));
                using var duplicate = RemoteExecutorOccupancy.TryClaimExclusive(root, "one", 1);
                Assert.Null(duplicate);
            }
            using var released = RemoteExecutorOccupancy.TryClaimExclusive(root, "one", 1);
            Assert.NotNull(released);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SlotZero_SharesLegacyLockAndNegativeSlotCannotClaim()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            using (var legacy = RemoteExecutorOccupancy.TryClaimExclusive(root, "one"))
            {
                Assert.NotNull(legacy);
                using var duplicate = RemoteExecutorOccupancy.TryClaimExclusive(root, "one", 0);
                Assert.Null(duplicate);
            }
            using var slotZero = RemoteExecutorOccupancy.TryClaimExclusive(root, "one", 0);
            Assert.NotNull(slotZero);
            using var legacyDuplicate = RemoteExecutorOccupancy.TryClaimExclusive(root, "one");
            Assert.Null(legacyDuplicate);
            using var negative = RemoteExecutorOccupancy.TryClaimExclusive(root, "one", -1);
            Assert.Null(negative);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SlotMarkers_StayOccupiedUntilEverySlotIsReleased()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            RemoteExecutorOccupancy.Claim(root, "one", "a", 0);
            RemoteExecutorOccupancy.Claim(root, "one", "a", 1);
            var folder = Path.Combine(root, ".orchestrator", "remote-executor-occupancy", "one");
            Assert.Equal(new[] { "a.json", "a.slot-1.json" }, Directory.GetFiles(folder).Select(Path.GetFileName).Order());
            Assert.True(RemoteExecutorOccupancy.IsOccupied(root, "one"));
            RemoteExecutorOccupancy.Release(root, "one", "a", 0);
            Assert.False(File.Exists(Path.Combine(folder, "a.json")));
            Assert.True(File.Exists(Path.Combine(folder, "a.slot-1.json")));
            Assert.True(RemoteExecutorOccupancy.IsOccupied(root, "one"));
            RemoteExecutorOccupancy.Release(root, "one", "a", 1);
            Assert.False(RemoteExecutorOccupancy.IsOccupied(root, "one"));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(root, true); }
    }
}
