using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Each case owns its repository, receipt database and build storage; no global slot pressure or waits.
public sealed class AcceptanceCohortMergeTrainSlotsBusyDeferralTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BusyRounds_RecoverWithReceiptOrDeferWithoutDeadAttempt(int busyRounds)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = new[]
            {
                CreateCompletedGoal(kernel, "First train member", repo),
                CreateCompletedGoal(kernel, "Second train member", repo),
                CreateCompletedGoal(kernel, "Third train member", repo)
            };
            var paths = new[]
            {
                "tests/Mcg.AgentOrchestrator.Core.Tests/TrainFirst.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TrainSecond.cs",
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TrainThird.cs"
            };
            for (var index = 0; index < goals.Length; index++)
            {
                var candidate = CreateWorktreeCandidate(repo, goals[index].Id, paths[index], $"member {index}");
                kernel.RecordGoalRefinement(goals[index].Id, new RefinedSpec(goals[index].Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                    { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                kernel.MapCriterionEvidenceOwner(goals[index].Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
            }
            var resultPath = WritePassingTrx(repo, "train-green.trx");
            var verifier = new SequenceAcceptanceVerifier([new(true, false, 0, null,
                TestResultPaths: [resultPath])]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var cleanup = CreateIsolatedCleanupContext(repo).Hooks;
            var acquisitions = 0;
            var delays = 0;
            var admitted = 0;
            string? identity = null;
            DotnetBuildEnvironmentLease? acquired = null;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(workspace.OrchestratorDirectory, "parallel-attempts"), workspace.IntegrationBranch,
                buildStorageRoot: cleanup.BuildStorageRoot,
                acquireCohortStableSlotRound: (trainId, label, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    identity ??= trainId;
                    Assert.Equal(identity, trainId);
                    Assert.StartsWith("merge-train:", label);
                    if (++acquisitions <= busyRounds)
                        throw new DotnetBuildSlotsBusyException(new("first-available-stable-slot", []));
                    return acquired = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                        TimeSpan.Zero, storageRoot: cleanup.BuildStorageRoot, holderLabel: label);
                },
                cohortStableSlotRoundDelay: (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    delays++;
                    return Task.CompletedTask;
                });
            var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), coordinator, cleanup);
            var selection = ProjectTrainSelection(driver, goals);
            var mainBefore = selection.Members[0].MainRevision;
            var databasePath = Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db");
            var store = new MergeTrainAcceptanceStore(databasePath);

            var run = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive,
                onGateAdmitted: () => admitted++);

            Assert.NotNull(identity);
            Assert.Equal(Math.Min(busyRounds + 1, 3), acquisitions);
            Assert.Equal(Math.Min(busyRounds, 2), delays);
            Assert.Equal(3, run.MemberResults.Count);
            Assert.Empty(run.Ejections);
            Assert.DoesNotContain("gate infrastructure failure", run.Detail, StringComparison.Ordinal);
            if (busyRounds == 3)
            {
                Assert.Null(run.Receipt);
                Assert.Null(run.RecordedReceipt);
                Assert.Null(store.TryReadReceipt(identity));
                Assert.Equal($"outcome=deferred-slots-busy train={identity}", run.Detail);
                Assert.Equal(0, verifier.RunCount);
                Assert.Equal(0, admitted);
                Assert.Null(acquired);
                Assert.All(goals, goal =>
                {
                    Assert.Equal(GoalStatus.Verified, goal.Status);
                    var held = Assert.IsType<ConductorAdvanceOutcome.Held>(run.MemberResults[goal.Id.Value].Outcome);
                    Assert.Equal(GoalLifecycleState.Verified, held.State);
                });
                // No failed gate receipt, landing or ejection can be mistaken for a dead train attempt.
                using var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT COUNT(*) FROM merge_train_receipts) + " +
                    "(SELECT COUNT(*) FROM merge_train_landings) + (SELECT COUNT(*) FROM merge_train_ejections);";
                Assert.Equal(0L, (long)command.ExecuteScalar()!);
                Assert.Equal(mainBefore, RunGitOutput(repo, "rev-parse", "main").Trim());
            }
            else
            {
                var receipt = Assert.IsType<MergeTrainReceipt>(run.Receipt);
                Assert.Equal(MergeTrainGateOutcome.Passed, receipt.Outcome);
                Assert.Equal(identity, receipt.Identity.Value);
                var recordedResultPath = Assert.Single(receipt.GateTestResultPaths);
                Assert.True(File.Exists(recordedResultPath));
                Assert.Equal(File.ReadAllText(resultPath), File.ReadAllText(recordedResultPath));
                Assert.Equal(receipt.ReceiptId, store.TryReadReceipt(identity)?.ReceiptId);
                Assert.Equal(MergeTrainGateOutcome.Passed, store.TryReadReceipt(identity)?.Outcome);
                Assert.Equal(1, verifier.RunCount);
                Assert.Equal(1, admitted);
                Assert.NotNull(acquired);
                Assert.False(acquired.IsExecutionLockHeld);
                Assert.All(goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            }
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }
}
