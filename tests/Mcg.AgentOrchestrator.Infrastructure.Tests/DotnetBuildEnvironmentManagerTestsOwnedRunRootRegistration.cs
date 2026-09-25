using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class DotnetBuildEnvironmentManagerTestsOwnedRunRootRegistration : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Build_root_is_registered_before_directory_creation(bool stableSlot)
    {
        var registrar = new RecordingRegistrar();
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = _ => registrar;

            var environment = stableSlot
                ? RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0)
                : RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "owned-root-ordering");

            var registration = Assert.Single(registrar.Registrations);
            Assert.Equal(environment.RootPath, registration.Path);
            Assert.False(registration.ExistedWhenRegistered);
            Assert.True(Directory.Exists(environment.ArtifactsPath));
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
        }
    }

    [Xunit.Fact]
    public void Missing_state_store_creates_unregistered_root_that_is_observed_and_retained()
    {
        VerifyUnavailableStoreCreatesObservedRetainedRoot(createStaleStore: false);
    }

    [Xunit.Fact]
    public void Stale_state_store_creates_unregistered_root_that_is_observed_and_retained()
    {
        VerifyUnavailableStoreCreatesObservedRetainedRoot(createStaleStore: true);
    }

    [Xunit.Theory]
    [Xunit.InlineData(OwnedRunRootReleaseOutcome.Succeeded)]
    [Xunit.InlineData(OwnedRunRootReleaseOutcome.Failed)]
    [Xunit.InlineData(OwnedRunRootReleaseOutcome.Cancelled)]
    public void Lease_release_records_terminal_outcome(OwnedRunRootReleaseOutcome outcome)
    {
        var registrar = new RecordingRegistrar();
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = _ => registrar;
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, $"release-{outcome}");
            var acquisition = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                environment,
                timeout: TimeSpan.FromSeconds(1));
            var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(acquisition).Lease;

            lease.SetReleaseOutcome(outcome);
            lease.Dispose();

            var release = Assert.Single(registrar.Releases);
            Assert.Equal(environment.RootPath, release.Path);
            Assert.Equal(outcome, release.Outcome);
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
        }
    }

    [Xunit.Fact]
    public void Unclassified_dispose_records_failed_outcome()
    {
        var registrar = new RecordingRegistrar();
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = _ => registrar;
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "release-abandoned");
            var acquisition = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                environment,
                timeout: TimeSpan.FromSeconds(1));
            var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(acquisition).Lease;

            lease.Dispose();

            Assert.Equal(OwnedRunRootReleaseOutcome.Failed, Assert.Single(registrar.Releases).Outcome);
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
        }
    }

    [Xunit.Fact]
    public void Release_write_failure_is_reported_to_terminal_sweep()
    {
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = _ => new ReleaseThrowingRegistrar();
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(
                StorageRoot,
                null,
                "release-write-failure");
            var acquisition = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                environment,
                timeout: TimeSpan.FromSeconds(1));
            var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(acquisition).Lease;

            var disposeException = Record.Exception(lease.Dispose);
            var sweep = TerminalGoalSweep.ExecuteOwnedBuildRootReap(
                () => new TerminalGoalSweepOwnedRootResult(0, 0, 0, 0, []), StorageRoot);

            Assert.Null(disposeException);
            var operatorEvent = Assert.Single(sweep.OperatorEvents);
            Assert.Contains("SWEEP_OWNED_ROOT_OBSERVED", operatorEvent, StringComparison.Ordinal);
            Assert.Contains("release persistence failed", operatorEvent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(environment.RootPath, operatorEvent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("synthetic release write failure", operatorEvent, StringComparison.Ordinal);
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
        }
    }

    [Xunit.Fact]
    public void Cleanup_write_failure_is_reported_to_terminal_sweep()
    {
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = _ => new CleanupWriteThrowingRegistrar();
            var otherStorageRoot = new DotnetBuildStorageRoot(Path.Combine(StorageRoot.RootPath, "other-root"));
            var otherEnvironment = RootedDotnetBuildEnvironmentManager.CreateAttempt(
                otherStorageRoot, null, "other-cleanup-write-failure");
            Assert.True(RootedDotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(
                otherStorageRoot, otherEnvironment));
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(
                StorageRoot,
                null,
                "cleanup-write-failure");

            Assert.True(RootedDotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(StorageRoot, environment));
            var sweep = TerminalGoalSweep.ExecuteOwnedBuildRootReap(
                () => new TerminalGoalSweepOwnedRootResult(0, 0, 0, 0, []), StorageRoot);

            var operatorEvent = Assert.Single(sweep.OperatorEvents);
            Assert.Contains("SWEEP_OWNED_ROOT_OBSERVED", operatorEvent, StringComparison.Ordinal);
            Assert.Contains("cleanup persistence failed", operatorEvent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(environment.RootPath, operatorEvent, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("synthetic cleanup write failure", operatorEvent, StringComparison.Ordinal);
            Assert.Contains(otherEnvironment.RootPath,
                Assert.Single(DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(1, otherStorageRoot)),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
        }
    }

    [Xunit.Fact]
    public void Legacy_stream_dispose_records_failed_outcome()
    {
        var registrar = new RecordingRegistrar();
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = _ => registrar;
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "legacy-stream-release");

            using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment))
            {
            }

            var release = Assert.Single(registrar.Releases);
            Assert.Equal(environment.RootPath, release.Path);
            Assert.Equal(OwnedRunRootReleaseOutcome.Failed, release.Outcome);
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
        }
    }

    [Xunit.Fact]
    public void Cancelled_acquisition_token_records_cancelled_outcome()
    {
        var registrar = new RecordingRegistrar();
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = _ => registrar;
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, "release-cancelled-token");
            using var cancellation = new CancellationTokenSource();
            var acquisition = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                environment,
                timeout: TimeSpan.FromSeconds(1),
                cancellation.Token);
            var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(acquisition).Lease;

            cancellation.Cancel();
            lease.Dispose();

            Assert.Equal(OwnedRunRootReleaseOutcome.Cancelled, Assert.Single(registrar.Releases).Outcome);
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
        }
    }

    [Xunit.Fact]
    public void Late_terminal_classification_updates_an_already_released_row()
    {
        var registrar = new RecordingRegistrar();
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = _ => registrar;
            var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
            var acquisition = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
                environment,
                timeout: TimeSpan.FromSeconds(1));
            var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(acquisition).Lease;

            lease.ReleaseExecutionLock();
            lease.SetReleaseOutcome(OwnedRunRootReleaseOutcome.Succeeded);

            Assert.Equal(OwnedRunRootReleaseOutcome.Failed, registrar.Releases[0].Outcome);
            Assert.Equal(OwnedRunRootReleaseOutcome.Succeeded, registrar.Releases[1].Outcome);
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(OwnedRunRootReleaseOutcome.Succeeded)]
    [Xunit.InlineData(OwnedRunRootReleaseOutcome.Failed)]
    [Xunit.InlineData(OwnedRunRootReleaseOutcome.Cancelled)]
    public async Task Acceptance_execution_owner_records_terminal_outcome(OwnedRunRootReleaseOutcome outcome)
    {
        var registrar = new RecordingRegistrar();
        var priorFactory = DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory;
        try
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = _ => registrar;
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(StorageRoot, null, $"owner-{outcome}");
            var owner = CreateExecutionOwner(outcome.ToString());
            owner.TrackEnvironment(environment);
            if (outcome == OwnedRunRootReleaseOutcome.Succeeded)
                owner.MarkSuccessful();
            else if (outcome == OwnedRunRootReleaseOutcome.Cancelled)
                owner.MarkCancelled();

            await owner.DisposeAsync();

            Assert.Equal(outcome, registrar.Releases.Last().Outcome);
        }
        finally
        {
            DotnetBuildEnvironmentManager.OwnedRunRootRegistrarFactory = priorFactory;
        }
    }

    private AcceptanceAttemptExecutionOwner CreateExecutionOwner(string suffix)
    {
        var resultsPrefix = Path.Combine(StorageRoot.RootPath, $"results-{suffix}");
        return new AcceptanceAttemptExecutionOwner(
            new AcceptanceAttemptIdentity(
                $"attempt-{suffix}",
                "goal-owned-root",
                StorageRoot.RootPath,
                "candidate",
                "main",
                "candidate",
                resultsPrefix,
                SlotIndex: null,
                Environment.ProcessId,
                LivenessCheckHint: null),
            new AcceptanceGateEngineSettings());
    }

    private sealed class RecordingRegistrar : IOwnedRunRootRegistrar
    {
        public List<(string Path, bool ExistedWhenRegistered)> Registrations { get; } = [];
        public List<(string Path, OwnedRunRootReleaseOutcome Outcome)> Releases { get; } = [];

        public void Register(string canonicalPath, OwnedRunRootPurpose purpose, SpawnProcessIdentity owner, string? goalId, string? dispatchId, DateTimeOffset now) =>
            Registrations.Add((canonicalPath, Directory.Exists(canonicalPath)));

        public void Release(string canonicalPath, OwnedRunRootReleaseOutcome outcome, DateTimeOffset now) =>
            Releases.Add((canonicalPath, outcome));

        public void MarkRemoved(string canonicalPath, DateTimeOffset now)
        {
        }

        public void MarkCleanupFailure(string canonicalPath, string holderEvidence, DateTimeOffset nextAttemptAfter, DateTimeOffset now)
        {
        }

        public void RecordRetention(string canonicalPath, string evidence, DateTimeOffset now)
        {
        }
    }

    private void VerifyUnavailableStoreCreatesObservedRetainedRoot(bool createStaleStore)
    {
        var stateDbPath = Path.Combine(StorageRoot.RootPath, ".orchestrator", "state.db");
        var registry = new OwnedRunRootRegistry(stateDbPath);
        var unavailableRepositoryRoot = Path.Combine(
            StorageRoot.RootPath,
            createStaleStore ? "stale-state-store" : "missing-state-store");
        if (createStaleStore)
        {
            var staleStateDirectory = Path.Combine(unavailableRepositoryRoot, ".orchestrator");
            Directory.CreateDirectory(staleStateDirectory);
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(staleStateDirectory, "state.db")};Mode=ReadWriteCreate;Pooling=False;");
            connection.Open();
        }

        var registeredRegistrar = DotnetBuildEnvironmentManager.SetOwnedRunRootRegistrarForTests(StorageRoot, null);
        var priorRepositoryRoot = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", unavailableRepositoryRoot);
            var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(
                StorageRoot,
                null,
                createStaleStore ? "stale-state-store" : "missing-state-store");

            Assert.True(Directory.Exists(environment.RootPath));
            var observation = new OwnedRunRootObserver(
                    registry,
                    StorageRoot,
                    new SystemOwnedRunRootDirectoryEnumerator())
                .ObserveUnregisteredRoots(0, 10);
            Assert.Contains(
                observation.UnregisteredRoots,
                report => report.Contains(environment.RootPath, StringComparison.OrdinalIgnoreCase));

            var reap = new OwnedRunRootReaper(
                    registry,
                    StorageRoot,
                    TimeProvider.System,
                    new UnexpectedProcessInspector(),
                    new SystemOwnedRunRootFileSystem(),
                    TimeSpan.FromMinutes(1))
                .Reap(0, 10);
            Assert.Equal(0, reap.ProcessedCount);
            Assert.True(Directory.Exists(environment.RootPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT", priorRepositoryRoot);
            DotnetBuildEnvironmentManager.SetOwnedRunRootRegistrarForTests(StorageRoot, registeredRegistrar);
            DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(10, StorageRoot);
        }
    }

    private sealed class UnexpectedProcessInspector : IOwnedRunRootProcessInspector
    {
        public OwnedRunRootOwnerInspection Inspect(SpawnProcessIdentity recordedOwner) =>
            throw new Xunit.Sdk.XunitException("An unregistered root must not reach owner inspection.");
    }

    private sealed class ReleaseThrowingRegistrar : IOwnedRunRootRegistrar
    {
        public void Register(string canonicalPath, OwnedRunRootPurpose purpose, SpawnProcessIdentity owner, string? goalId, string? dispatchId, DateTimeOffset now)
        {
        }

        public void Release(string canonicalPath, OwnedRunRootReleaseOutcome outcome, DateTimeOffset now) =>
            throw new InvalidOperationException("synthetic release write failure");

        public void MarkRemoved(string canonicalPath, DateTimeOffset now)
        {
        }

        public void MarkCleanupFailure(string canonicalPath, string holderEvidence, DateTimeOffset nextAttemptAfter, DateTimeOffset now)
        {
        }

        public void RecordRetention(string canonicalPath, string evidence, DateTimeOffset now)
        {
        }
    }

    private sealed class CleanupWriteThrowingRegistrar : IOwnedRunRootRegistrar
    {
        public void Register(string canonicalPath, OwnedRunRootPurpose purpose, SpawnProcessIdentity owner, string? goalId, string? dispatchId, DateTimeOffset now)
        {
        }

        public void Release(string canonicalPath, OwnedRunRootReleaseOutcome outcome, DateTimeOffset now)
        {
        }

        public void MarkRemoved(string canonicalPath, DateTimeOffset now) =>
            throw new InvalidOperationException("synthetic cleanup write failure");

        public void MarkCleanupFailure(string canonicalPath, string holderEvidence, DateTimeOffset nextAttemptAfter, DateTimeOffset now) =>
            throw new InvalidOperationException("synthetic cleanup write failure");

        public void RecordRetention(string canonicalPath, string evidence, DateTimeOffset now)
        {
        }
    }
}
