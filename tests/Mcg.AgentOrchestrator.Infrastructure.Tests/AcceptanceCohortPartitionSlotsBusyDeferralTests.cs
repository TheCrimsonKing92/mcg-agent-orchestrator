using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCohortPartitionSlotsBusyDeferralTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PartitionStartsOnSecondRoundAndRecordsNormalReceipts(int busyPartition)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = new[]
            {
                CreateCompletedGoal(kernel, "First partition", repo),
                CreateCompletedGoal(kernel, "Second partition", repo)
            };
            CreateWorktreeCandidate(repo, goals[0].Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            CreateWorktreeCandidate(repo, goals[1].Id, "tests/Second.cs", "second");
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var cleanup = CreateIsolatedCleanupContext(repo).Hooks;
            var resultPaths = new[]
            {
                WritePassingTrx(repo, "first-partition-green.trx"),
                WritePassingTrx(repo, "second-partition-green.trx")
            };
            var verifier = new SequenceAcceptanceVerifier([
                FailedVerification(repo, "combined-red.trx", "combined"),
                new(true, false, 0, null, TestResultPaths: [resultPaths[0]]),
                new(true, false, 0, null, TestResultPaths: [resultPaths[1]])
            ]);
            var partitionLabels = goals.Select(goal => $"cohort-partition:goal-{goal.Id.Value[..8]}").ToArray();
            var acquisitions = new int[2];
            var gateAcquisitions = 0;
            var delays = 0;
            var leases = new List<DotnetBuildEnvironmentLease>();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(workspace.OrchestratorDirectory, "parallel-attempts"),
                buildStorageRoot: cleanup.BuildStorageRoot,
                acquireCohortStableSlotRound: (_, label, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var partition = Array.IndexOf(partitionLabels, label);
                    if (partition >= 0)
                    {
                        if (++acquisitions[partition] == 1 && partition == busyPartition)
                            throw new DotnetBuildSlotsBusyException(new("first-available-stable-slot", []));
                    }
                    else
                    {
                        Assert.Equal($"cohort-gate:goal-{goals[0].Id.Value[..8]}+{goals[1].Id.Value[..8]}", label);
                        gateAcquisitions++;
                    }
                    var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                        TimeSpan.Zero, storageRoot: cleanup.BuildStorageRoot, holderLabel: label);
                    leases.Add(lease);
                    return lease;
                },
                cohortStableSlotRoundDelay: (_, _) => { delays++; return Task.CompletedTask; });
            var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), coordinator, cleanup);

            var run = driver.RunAcceptanceCohort(ProjectSelection(driver, goals[0], goals[1]),
                goals, ConductorAutonomyPolicy.Permissive);

            var receipt = Assert.IsType<AcceptanceCohortReceipt>(run.Receipt);
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, receipt.Outcome);
            Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, receipt.Attribution);
            var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            var partitions = store.ReadPartitionReceipts(receipt.Identity.Value);
            Assert.Equal(2, partitions.Count);
            Assert.All(partitions, partition =>
            {
                Assert.Equal(AcceptanceCohortGateOutcome.Passed, partition.Outcome);
                Assert.Equal(resultPaths[partition.MemberOrdinal], Assert.Single(partition.TestResultPaths));
                Assert.True(File.Exists(partition.TestResultPaths[0]));
            });
            Assert.Equal(2, acquisitions[busyPartition]);
            Assert.Equal(1, acquisitions[1 - busyPartition]);
            Assert.Equal(1, gateAcquisitions);
            Assert.Equal(1, delays);
            Assert.Equal(3, verifier.RunCount);
            Assert.Equal<GoalId?>([null, goals[0].Id, goals[1].Id], verifier.GoalIds);
            Assert.Equal(3, leases.Count);
            Assert.All(leases, lease => Assert.False(lease.IsExecutionLockHeld));
            AssertNoCohortWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }
}
