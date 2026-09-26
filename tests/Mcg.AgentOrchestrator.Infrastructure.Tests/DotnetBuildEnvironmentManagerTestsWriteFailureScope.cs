using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class DotnetBuildEnvironmentManagerTestsWriteFailureScope : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Xunit.Fact]
    public void Scoped_read_returns_only_own_root_entries_and_preserves_foreign_order()
    {
        var foreignRoot = ForeignRoot();
        var buffer = new OwnedRunRootWriteFailureBuffer();
        var prefix = Guid.NewGuid().ToString("N");
        buffer.Enqueue(foreignRoot.RootPath, $"{prefix}-foreign-1");
        buffer.Enqueue(foreignRoot.RootPath, $"{prefix}-foreign-2");
        buffer.Enqueue(StorageRoot.RootPath, $"{prefix}-own-1");
        buffer.Enqueue(foreignRoot.RootPath, $"{prefix}-foreign-3");
        buffer.Enqueue(StorageRoot.RootPath, $"{prefix}-own-2");

        Assert.Equal([$"{prefix}-own-1", $"{prefix}-own-2"], buffer.Drain(10, StorageRoot.RootPath));
        Assert.Equal(
            [$"{prefix}-foreign-1", $"{prefix}-foreign-2", $"{prefix}-foreign-3"],
            buffer.Drain(10, foreignRoot.RootPath));
        Assert.Empty(buffer.Drain(10, StorageRoot.RootPath));
    }

    [Xunit.Fact]
    public void Scoped_read_caps_matching_entries_and_normalizes_root_paths()
    {
        var foreignRoot = ForeignRoot();
        var buffer = new OwnedRunRootWriteFailureBuffer();
        var prefix = Guid.NewGuid().ToString("N");
        buffer.Enqueue(foreignRoot.RootPath, $"{prefix}-foreign");
        buffer.Enqueue(StorageRoot.RootPath, $"{prefix}-own-1");
        buffer.Enqueue(StorageRoot.RootPath, $"{prefix}-own-2");
        var alternateSpelling = StorageRoot.RootPath.ToUpperInvariant() + Path.DirectorySeparatorChar;

        Assert.Equal([$"{prefix}-own-1"], buffer.Drain(1, alternateSpelling));
        Assert.Equal([$"{prefix}-own-2"], buffer.Drain(1, StorageRoot.RootPath));
        Assert.Equal([$"{prefix}-foreign"], buffer.Drain(1, foreignRoot.RootPath));
    }

    [Xunit.Fact]
    public void Unscoped_read_returns_all_scopes_in_fifo_order()
    {
        var foreignRoot = ForeignRoot();
        var buffer = new OwnedRunRootWriteFailureBuffer();
        var prefix = Guid.NewGuid().ToString("N");
        buffer.Enqueue(null, $"{prefix}-unattributed");
        buffer.Enqueue(StorageRoot.RootPath, $"{prefix}-own");
        buffer.Enqueue(foreignRoot.RootPath, $"{prefix}-foreign");

        Assert.Equal([$"{prefix}-own"], buffer.Drain(10, StorageRoot.RootPath));
        Assert.Equal([$"{prefix}-foreign"], buffer.Drain(10, foreignRoot.RootPath));

        buffer.Enqueue(StorageRoot.RootPath, $"{prefix}-own-2");
        buffer.Enqueue(foreignRoot.RootPath, $"{prefix}-foreign-2");
        var all = new List<string>();
        IReadOnlyList<string> batch;
        do
        {
            batch = buffer.Drain(TerminalGoalSweep.MaxOwnedBuildRootsPerSweep, null);
            all.AddRange(batch);
        } while (batch.Count != 0);

        Assert.Equal(
            [$"{prefix}-unattributed", $"{prefix}-own-2", $"{prefix}-foreign-2"],
            all);
    }

    [Xunit.Fact]
    public void Release_write_failure_is_scoped_despite_foreign_registration_unavailable_entries() =>
        VerifyWriteFailureIsScoped(releaseFailure: true);

    [Xunit.Fact]
    public void Cleanup_write_failure_is_scoped_despite_foreign_registration_unavailable_entries() =>
        VerifyWriteFailureIsScoped(releaseFailure: false);

    private void VerifyWriteFailureIsScoped(bool releaseFailure)
    {
        var foreignRoot = ForeignRoot();
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = root =>
                string.Equals(root.RootPath, StorageRoot.RootPath, StringComparison.OrdinalIgnoreCase)
                    ? new WriteThrowingRegistrar(releaseFailure)
                    : null;
            for (var index = 0; index < TerminalGoalSweep.MaxOwnedBuildRootsPerSweep; index++)
                RootedDotnetBuildEnvironmentManager.CreateAttempt(foreignRoot, null, $"foreign-{index}");

            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(
                StorageRoot, null, releaseFailure ? "release-write-failure" : "cleanup-write-failure");
            if (releaseFailure)
                DotnetBuildEnvironmentManager.RecordOwnedRunRootRelease(
                    environment, OwnedRunRootReleaseOutcome.Succeeded);
            else
                Assert.True(RootedDotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(StorageRoot, environment));

            var sweep = TerminalGoalSweep.ExecuteOwnedBuildRootReap(
                () => new TerminalGoalSweepOwnedRootResult(0, 0, 0, 0, []), StorageRoot);
            var operatorEvent = Assert.Single(sweep.OperatorEvents);
            Assert.Contains("SWEEP_OWNED_ROOT_OBSERVED", operatorEvent, StringComparison.Ordinal);
            Assert.Contains(
                releaseFailure ? "release persistence failed" : "cleanup persistence failed",
                operatorEvent,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(environment.RootPath, operatorEvent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                releaseFailure ? "synthetic release write failure" : "synthetic cleanup write failure",
                operatorEvent,
                StringComparison.Ordinal);

            var foreignFailures = DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(
                TerminalGoalSweep.MaxOwnedBuildRootsPerSweep, foreignRoot);
            Assert.Equal(TerminalGoalSweep.MaxOwnedBuildRootsPerSweep, foreignFailures.Count);
            Assert.All(foreignFailures, failure =>
                Assert.Contains("owned-root registration unavailable", failure, StringComparison.Ordinal));
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(10, StorageRoot);
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(
                TerminalGoalSweep.MaxOwnedBuildRootsPerSweep, foreignRoot);
        }
    }

    private DotnetBuildStorageRoot ForeignRoot() =>
        new(Path.Combine(StorageRoot.RootPath, "foreign"));

    private sealed class WriteThrowingRegistrar(bool releaseFailure) : IOwnedRunRootRegistrar
    {
        public void Register(string canonicalPath, OwnedRunRootPurpose purpose, SpawnProcessIdentity owner,
            string? goalId, string? dispatchId, DateTimeOffset now) { }

        public void Release(string canonicalPath, OwnedRunRootReleaseOutcome outcome, DateTimeOffset now)
        {
            if (releaseFailure)
                throw new InvalidOperationException("synthetic release write failure");
        }

        public void MarkRemoved(string canonicalPath, DateTimeOffset now)
        {
            if (!releaseFailure)
                throw new InvalidOperationException("synthetic cleanup write failure");
        }

        public void MarkCleanupFailure(string canonicalPath, string holderEvidence,
            DateTimeOffset nextAttemptAfter, DateTimeOffset now) { }

        public void RecordRetention(string canonicalPath, string evidence, DateTimeOffset now) { }
    }
}
