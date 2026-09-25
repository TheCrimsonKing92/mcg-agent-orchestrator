using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class DotnetBuildEnvironmentManagerTestsWriteFailureScope : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Xunit.Fact]
    public void Scoped_read_returns_only_own_root_entries_and_preserves_foreign_order()
    {
        var foreignRoot = ForeignRoot();
        var prefix = Guid.NewGuid().ToString("N");
        try
        {
            DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(foreignRoot, $"{prefix}-foreign-1");
            DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(foreignRoot, $"{prefix}-foreign-2");
            DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(StorageRoot, $"{prefix}-own-1");
            DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(foreignRoot, $"{prefix}-foreign-3");
            DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(StorageRoot, $"{prefix}-own-2");

            Assert.Equal(
                [$"{prefix}-own-1", $"{prefix}-own-2"],
                DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(StorageRoot, 10));
            Assert.Equal(
                [$"{prefix}-foreign-1", $"{prefix}-foreign-2", $"{prefix}-foreign-3"],
                DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(foreignRoot, 10));
            Assert.Empty(DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(StorageRoot, 10));
        }
        finally
        {
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(StorageRoot, 10);
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(foreignRoot, 10);
        }
    }

    [Xunit.Fact]
    public void Scoped_read_caps_matching_entries_and_normalizes_root_paths()
    {
        var foreignRoot = ForeignRoot();
        var prefix = Guid.NewGuid().ToString("N");
        try
        {
            DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(foreignRoot, $"{prefix}-foreign");
            DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(StorageRoot, $"{prefix}-own-1");
            DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(StorageRoot, $"{prefix}-own-2");
            var alternateSpelling = new DotnetBuildStorageRoot(
                StorageRoot.RootPath.ToUpperInvariant() + Path.DirectorySeparatorChar);

            Assert.Equal(
                [$"{prefix}-own-1"],
                DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(alternateSpelling, 1));
            Assert.Equal(
                [$"{prefix}-own-2"],
                DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(StorageRoot, 1));
            Assert.Equal(
                [$"{prefix}-foreign"],
                DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(foreignRoot, 1));
        }
        finally
        {
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(StorageRoot, 10);
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(foreignRoot, 10);
        }
    }

    [Xunit.Fact]
    public void Unscoped_read_returns_all_scopes_in_fifo_order()
    {
        var foreignRoot = ForeignRoot();
        var prefix = Guid.NewGuid().ToString("N");
        DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(null, $"{prefix}-unattributed");
        DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(StorageRoot, $"{prefix}-own");
        DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(foreignRoot, $"{prefix}-foreign");

        Assert.Equal(
            [$"{prefix}-own"],
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(StorageRoot, 10));
        Assert.Equal(
            [$"{prefix}-foreign"],
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(foreignRoot, 10));

        DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(StorageRoot, $"{prefix}-own-2");
        DotnetBuildEnvironmentManager.RecordOwnedRunRootWriteFailure(foreignRoot, $"{prefix}-foreign-2");
        var all = new List<string>();
        IReadOnlyList<string> batch;
        do
        {
            batch = DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(
                TerminalGoalSweep.MaxOwnedBuildRootsPerSweep);
            all.AddRange(batch);
        } while (batch.Count != 0);

        Assert.Equal(
            [$"{prefix}-unattributed", $"{prefix}-own-2", $"{prefix}-foreign-2"],
            all.Where(message => message.StartsWith(prefix, StringComparison.Ordinal)));
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
                foreignRoot, TerminalGoalSweep.MaxOwnedBuildRootsPerSweep);
            Assert.Equal(TerminalGoalSweep.MaxOwnedBuildRootsPerSweep, foreignFailures.Count);
            Assert.All(foreignFailures, failure =>
                Assert.Contains("owned-root registration unavailable", failure, StringComparison.Ordinal));
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(StorageRoot, 10);
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(
                foreignRoot, TerminalGoalSweep.MaxOwnedBuildRootsPerSweep);
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
