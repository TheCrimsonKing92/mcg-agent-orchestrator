using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each class owns its repository; cases reset its mutable state.
public sealed class MergeTrainGateOnlySlotsBusyDeferralTests(MergeTrainDeferralSharedFixture fixture)
    : AcceptanceCohortWorkflowTests, IClassFixture<MergeTrainDeferralSharedFixture>
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void GateOnlyRetriesThenRecordsReceiptOrDefers(int busyRounds)
    {
        WithTrain(scenario =>
        {
            scenario.BusyRounds = busyRounds;
            var run = scenario.Driver.RunMergeTrain(scenario.Selection, scenario.Goals,
                ConductorAutonomyPolicy.Permissive, gateOnly: true);

            Assert.Equal(3, scenario.Acquisitions);
            Assert.Equal(2, scenario.Delays);
            Assert.Empty(run.Ejections);
            if (busyRounds == 3)
            {
                Assert.Equal($"outcome=deferred-slots-busy train={scenario.Identity}", run.Detail);
                Assert.Null(run.Receipt);
                Assert.Null(run.RecordedReceipt);
                Assert.Equal(3, run.MemberResults.Count);
                Assert.All(run.MemberResults.Values, member => Assert.Equal(GoalLifecycleState.Verified,
                    Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome).State));
                AssertNoRecordedOutcome(scenario);
            }
            else
            {
                var receipt = Assert.IsType<MergeTrainReceipt>(run.RecordedReceipt);
                Assert.Equal(MergeTrainGateOutcome.Passed, receipt.Outcome);
                Assert.Equal(receipt.ReceiptId, scenario.Store.TryReadReceipt(scenario.Identity!)?.ReceiptId);
                Assert.Equal(1, scenario.Verifier.RunCount);
                Assert.NotNull(scenario.Lease);
                Assert.False(scenario.Lease.IsExecutionLockHeld);
            }
            Assert.All(scenario.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            Assert.Equal(scenario.MainBefore, RunGitOutput(scenario.Repo, "rev-parse", "main").Trim());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedDeferralRecoversAndRetriesUntilSlotIsFree(bool ownerAliveAtRecovery)
    {
        WithTrain(scenario =>
        {
            scenario.EnableGrouped();
            for (var tick = 0; tick < 2; tick++)
            {
                var inflight = scenario.StartBackground();
                Assert.Contains("outcome=inflight", inflight.Detail);
                Assert.Equal(tick + 1, scenario.Launches.Count);
                var attempt = scenario.Launches[tick];
                ConductorGroupedGateOutcome? outcome = null;
                Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(attempt.MetadataPath,
                    child => outcome = scenario.Driver.RunGroupedGateAttemptBody(child),
                    (_, _) => new NoConsoleRedirection()));
                scenario.Holders.RecordExit(attempt);
                Assert.NotNull(outcome);
                Assert.Equal("deferred-slots-busy", outcome.Verdict);
                Assert.Equal("", outcome.ReceiptId);
                using var document = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
                Assert.Equal("completed", document.RootElement.GetProperty("Status").GetString());
                Assert.Equal("deferred-slots-busy", document.RootElement.GetProperty("Verdict").GetString());
                Assert.Equal("", document.RootElement.GetProperty("ReceiptId").GetString());
                Assert.True(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt));
                Assert.Equal((tick + 1) * 3, scenario.Acquisitions);
                Assert.Equal((tick + 1) * 2, scenario.Delays);
                AssertNoRecordedOutcome(scenario);
                scenario.Alive = ownerAliveAtRecovery;
            }

            // The next tick also revisits the already-reconciled first deferral.
            scenario.BusyRounds = 0;
            var retry = scenario.StartBackground();
            Assert.Contains("outcome=inflight", retry.Detail);
            Assert.Equal(3, scenario.Launches.Count);
            AssertNoRecordedOutcome(scenario);
            var freeAttempt = scenario.Launches[2];
            Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(freeAttempt.MetadataPath,
                scenario.Driver.RunGroupedGateAttemptBody, (_, _) => new NoConsoleRedirection()));
            scenario.Holders.RecordExit(freeAttempt);
            using var passed = JsonDocument.Parse(File.ReadAllText(freeAttempt.ResultPath));
            Assert.Equal("completed", passed.RootElement.GetProperty("Status").GetString());
            Assert.Equal("passed", passed.RootElement.GetProperty("Verdict").GetString());
            var receiptId = passed.RootElement.GetProperty("ReceiptId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(receiptId));
            var receipt = Assert.IsType<MergeTrainReceipt>(scenario.Store.TryReadReceipt(freeAttempt.IdentityValue));
            Assert.Equal(receiptId, receipt.ReceiptId);
            Assert.Equal(MergeTrainGateOutcome.Passed, receipt.Outcome);
            Assert.Equal(7, scenario.Acquisitions);
            Assert.Equal(4, scenario.Delays);
            Assert.Equal(1, scenario.Verifier.RunCount);
            Assert.False(scenario.Lease!.IsExecutionLockHeld);
            Assert.All(scenario.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
        });
    }

    [Fact]
    public void GenuineGateExceptionStillPublishesFailureAndRecoveryReceipt()
    {
        WithTrain(scenario =>
        {
            scenario.BusyRounds = 0;
            scenario.Verifier.ThrowOnGate = true;
            scenario.EnableGrouped();
            scenario.StartBackground();
            var attempt = Assert.Single(scenario.Launches);
            Assert.Equal(1, ConductorGroupedGateAttemptHost.Run(attempt.MetadataPath,
                scenario.Driver.RunGroupedGateAttemptBody, (_, _) => new NoConsoleRedirection()));
            scenario.Holders.RecordExit(attempt);
            using var document = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
            Assert.Equal("failed", document.RootElement.GetProperty("Status").GetString());
            Assert.Equal(nameof(IOException), document.RootElement.GetProperty("ErrorType").GetString());
            Assert.False(ConductorGroupedGateAttemptCoordinator.IsSlotsBusyDeferral(attempt));
            Assert.Null(scenario.Store.TryReadReceipt(attempt.IdentityValue));
            Assert.Equal(1, scenario.Verifier.RunCount);
            Assert.False(scenario.Lease!.IsExecutionLockHeld);

            scenario.Alive = false;
            scenario.StartBackground();
            var receipt = Assert.IsType<MergeTrainReceipt>(scenario.Store.TryReadReceipt(attempt.IdentityValue));
            Assert.Equal(MergeTrainGateOutcome.InfrastructureFailure, receipt.Outcome);
            Assert.Equal("infrastructure:grouped-gate-owner-dead", Assert.Single(receipt.FailedChecks));
            Assert.Single(scenario.Launches);
        });
    }

    private void WithTrain(Action<Scenario> assert)
    {
        fixture.Reset();
        var repo = fixture.Repository;
        Scenario? activeScenario = null;
        try
        {
            var kernel = fixture.Kernel;
            var goals = fixture.Goals;
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var cleanup = CreateIsolatedCleanupContext(repo).Hooks;
            var verifier = new GateVerifier(new(true, false, 0, null,
                TestResultPaths: [WritePassingTrx(repo, "train-green.trx")]));
            var scenario = new Scenario(repo, goals, workspace, verifier);
            activeScenario = scenario;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(workspace.OrchestratorDirectory, "parallel-attempts"), workspace.IntegrationBranch,
                buildStorageRoot: cleanup.BuildStorageRoot,
                acquireCohortStableSlotRound: (identity, label, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    scenario.Identity ??= identity;
                    Assert.Equal(scenario.Identity, identity);
                    Assert.StartsWith("merge-train:", label);
                    if (++scenario.Acquisitions <= scenario.BusyRounds)
                        throw new DotnetBuildSlotsBusyException(new("first-available-stable-slot", []));
                    return scenario.Lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                        TimeSpan.Zero, storageRoot: cleanup.BuildStorageRoot, holderLabel: label,
                        cancellationToken: token);
                }, cohortStableSlotRoundDelay: (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    scenario.Delays++;
                    return Task.CompletedTask;
                });
            scenario.Driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), coordinator, cleanup);
            scenario.Selection = ProjectTrainSelection(scenario.Driver, goals);
            scenario.MainBefore = scenario.Selection.Members[0].MainRevision;
            assert(scenario);
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { activeScenario?.Lease?.Dispose(); }
    }

    private static void AssertNoRecordedOutcome(Scenario scenario)
    {
        Assert.Null(scenario.Store.TryReadReceipt(scenario.Identity!));
        Assert.Equal(0, scenario.Verifier.RunCount);
        Assert.All(scenario.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
        Assert.Equal(scenario.MainBefore, RunGitOutput(scenario.Repo, "rev-parse", "main").Trim());
        using var connection = new SqliteConnection($"Data Source={scenario.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM merge_train_receipts) + " +
            "(SELECT COUNT(*) FROM merge_train_landings) + (SELECT COUNT(*) FROM merge_train_ejections);";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    private sealed class Scenario(string repo, Goal[] goals, OrchestratorWorkspace workspace, GateVerifier verifier)
    {
        internal string Repo => repo;
        internal Goal[] Goals => goals;
        internal GateVerifier Verifier => verifier;
        internal ConductorDriver Driver { get; set; } = null!;
        internal ConductorMergeTrainSelection Selection { get; set; } = null!;
        internal string MainBefore { get; set; } = "";
        internal string DatabasePath => Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db");
        internal MergeTrainAcceptanceStore Store => new(DatabasePath);
        internal int BusyRounds = int.MaxValue;
        internal int Acquisitions;
        internal int Delays;
        internal string? Identity;
        internal DotnetBuildEnvironmentLease? Lease;
        internal bool Alive;
        internal AcceptanceHolderExitFixture Holders { get; } = new();
        internal List<ConductorGroupedGateAttempt> Launches { get; } = [];

        internal void EnableGrouped() => Driver.EnableOwnedGroupedGateAttempts(new ConductorGroupedGateAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"),
            launch: attempt =>
            {
                Launches.Add(attempt);
                Alive = true;
                return new ConductorGroupedGateLaunchResult(Environment.ProcessId, null, null);
            }, isProcessAlive: _ => Alive, classifyHolder: Holders.Classify));

        internal ConductorMergeTrainRunResult StartBackground() => Driver.RunMergeTrain(Selection, Goals,
            ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
    }

    private sealed class GateVerifier(AcceptanceVerificationResult result) : IGoalAcceptanceVerifier
    {
        internal int RunCount;
        internal bool ThrowOnGate;
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            Assert.NotNull(stableSlotLease);
            Assert.True(stableSlotLease.IsExecutionLockHeld);
            RunCount++;
            if (ThrowOnGate) throw new IOException("fixture gate failure after stable slot admission");
            return Task.FromResult(result);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) =>
            throw new NotSupportedException("Focused evidence is not used by the train gate fixture.");
    }

    private sealed class NoConsoleRedirection : IDisposable { public void Dispose() { } }
}
