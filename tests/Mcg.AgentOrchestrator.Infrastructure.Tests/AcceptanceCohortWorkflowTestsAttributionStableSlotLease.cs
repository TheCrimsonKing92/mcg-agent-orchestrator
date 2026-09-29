using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsAttributionStableSlotLease : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FailedCohort_HoldsEachPartitionLeaseAndReleasesEveryLeaseOnce(int throwingCall)
    {
        RunScenario(throwingCall, acquisitionFailure: false, cancelFirst: false);
    }

    [Fact]
    public void CancelledAttribution_ReleasesTheLeaseAndRecordsInfrastructureFailure()
    {
        RunScenario(throwingCall: -1, acquisitionFailure: false, cancelFirst: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FailedPartitionLeaseAcquisition_IsIndeterminate(int failingPartition)
    {
        RunScenario(throwingCall: -1, acquisitionFailure: true, cancelFirst: false,
            failingPartition: failingPartition);
    }

    private void RunScenario(int throwingCall, bool acquisitionFailure, bool cancelFirst,
        int failingPartition = -1)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First leased partition", repo);
            var second = CreateCompletedGoal(kernel, "Second leased partition", repo);
            CreateWorktreeCandidate(repo, first.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            CreateWorktreeCandidate(repo, second.Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            using var cancellation = new CancellationTokenSource();
            var verifier = new LeaseObservingVerifier(repo, throwingCall, cancelFirst, cancellation);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var issued = new List<(DotnetBuildEnvironmentLease Lease, int[] Releases)>();
            var acquisitionCount = 0;
            driver.CohortPartitionStableSlotLeaseSource = (_, token) =>
            {
                var partition = acquisitionCount++;
                Assert.All(issued, item => Assert.False(item.Lease.IsExecutionLockHeld));
                if (acquisitionFailure && partition == failingPartition)
                    throw new IOException("Controlled slot acquisition failure");
                token.ThrowIfCancellationRequested();
                var lease = AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(repo);
                var releases = new int[1];
                lease.RegisterExecutionLockReleaseObserver(() => releases[0]++);
                issued.Add((lease, releases));
                return lease;
            };
            var selection = ProjectSelection(driver, first, second);

            if (throwingCall == 3)
            {
                Assert.Throws<UnauthorizedAccessException>(() => driver.RunAcceptanceCohort(
                    selection, [first, second], ConductorAutonomyPolicy.Permissive));
            }
            else
            {
                var result = driver.RunAcceptanceCohort(
                    selection, [first, second], ConductorAutonomyPolicy.Permissive,
                    cancellationToken: cancellation.Token);
                var receipt = Assert.IsType<AcceptanceCohortReceipt>(result.Receipt);
                Assert.Equal(AcceptanceCohortGateOutcome.Failed, receipt.Outcome);
                if (acquisitionFailure || cancelFirst || throwingCall is 1 or 2)
                    Assert.Equal(AcceptanceCohortAttributionOutcome.Indeterminate, receipt.Attribution);
                else
                    Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, receipt.Attribution);
                if (acquisitionFailure)
                {
                    var store = new CohortAcceptanceStore(Path.Combine(
                        workspace.OrchestratorDirectory, "cohort-acceptance.db"));
                    Assert.Equal(AcceptanceCohortAttributionOutcome.Indeterminate,
                        store.TryReadReceipt(receipt.Identity.Value)?.Attribution);
                    using var connection = new SqliteConnection($"Data Source={Path.Combine(
                        workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT outcome FROM cohort_partition_receipts
                         WHERE cohort_id=$cohort AND member_ordinal=$ordinal
                        """;
                    command.Parameters.AddWithValue("$cohort", receipt.Identity.Value);
                    command.Parameters.AddWithValue("$ordinal", failingPartition);
                    Assert.Equal("InfrastructureFailure", command.ExecuteScalar());
                }
            }

            Assert.Equal(2, acquisitionCount);
            Assert.Equal(acquisitionFailure || cancelFirst ? 1 : 2, issued.Count);
            Assert.All(issued, item =>
            {
                Assert.Equal(1, item.Releases[0]);
                Assert.False(item.Lease.IsExecutionLockHeld);
            });
            Assert.Equal(1, verifier.CombinedLeaseReleases);
            Assert.False(verifier.CombinedLease!.IsExecutionLockHeld);
            Assert.Equal<GoalId?>(
                acquisitionFailure && failingPartition == 0
                    ? [null, second.Id]
                    : cancelFirst || acquisitionFailure && failingPartition == 1
                        ? [null, first.Id]
                        : [null, first.Id, second.Id],
                verifier.ObservedGoalIds);
            Assert.Equal(issued.Select(item => item.Lease), verifier.PartitionLeases);
            Assert.All(verifier.PartitionSlots, slot => Assert.NotNull(slot));
            Assert.All(verifier.PartitionHeldDuringRun, held => Assert.True(held));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private sealed class LeaseObservingVerifier(
        string repo, int throwingCall, bool cancelFirst, CancellationTokenSource cancellation)
        : IGoalAcceptanceVerifier
    {
        private int _calls;
        internal int CombinedLeaseReleases { get; private set; }
        internal DotnetBuildEnvironmentLease? CombinedLease { get; private set; }
        internal List<GoalId?> ObservedGoalIds { get; } = [];
        internal List<DotnetBuildEnvironmentLease> PartitionLeases { get; } = [];
        internal List<int?> PartitionSlots { get; } = [];
        internal List<bool> PartitionHeldDuringRun { get; } = [];

        public Task<AcceptanceVerificationResult> RunOwnedAsync(
            string worktreePath, GoalId? goalId, IReadOnlyList<string>? changedFiles,
            int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            var call = _calls++;
            ObservedGoalIds.Add(goalId);
            Assert.NotNull(stableSlotLease);
            Assert.NotNull(stableSlotIndex);
            Assert.True(stableSlotLease.IsExecutionLockHeld);
            if (call == 0)
            {
                CombinedLease = stableSlotLease;
                stableSlotLease.RegisterExecutionLockReleaseObserver(() => CombinedLeaseReleases++);
                return Task.FromResult(FailedVerification(repo, "combined-lease-red.trx", "combined"));
            }
            Assert.False(CombinedLease!.IsExecutionLockHeld);
            PartitionLeases.Add(stableSlotLease);
            PartitionSlots.Add(stableSlotIndex);
            PartitionHeldDuringRun.Add(stableSlotLease.IsExecutionLockHeld);
            if (cancelFirst && call == 1)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            if (throwingCall == call || throwingCall == 3 && call == 2)
            {
                if (call == 1) throw new InvalidDataException("Controlled first partition failure");
                if (throwingCall == 2) throw new IOException("Controlled second partition failure");
                throw new UnauthorizedAccessException("Controlled uncaught partition failure");
            }
            return Task.FromResult(new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult($"partition-{call}", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, $"partition-{call}-green.trx")]));
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
            string worktreePath, GoalId? goalId, string request,
            IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) =>
            throw new NotSupportedException();
    }
}
