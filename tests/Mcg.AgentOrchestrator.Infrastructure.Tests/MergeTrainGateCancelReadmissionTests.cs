using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: the shared fixture owns one repository for this class; all holders are fake.
public sealed class MergeTrainGateCancelReadmissionTests(MergeTrainDeferralSharedFixture fixture)
    : AcceptanceCohortWorkflowTests, IClassFixture<MergeTrainDeferralSharedFixture>
{
    [Fact]
    public void RunMergeTrain_OperatorCancellation_ReadmitsWithoutFailureReceipt()
    {
        fixture.Reset();
        var now = DateTimeOffset.UnixEpoch.AddDays(20000);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Repository);
        var cleanup = CreateIsolatedCleanupContext(fixture.Repository).Hooks;
        var solo = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "parallel-attempts"), workspace.IntegrationBranch,
            utcNow: () => now, buildStorageRoot: cleanup.BuildStorageRoot);
        var driver = new ConductorDriver(fixture.Kernel, workspace, new UncalledVerifier(),
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), solo, cleanup);
        var selection = ProjectTrainSelection(driver, fixture.Goals);
        var launches = new List<ConductorGroupedGateAttempt>();
        var stops = new List<string>();
        var holders = new AcceptanceHolderExitFixture();
        var grouped = new ConductorGroupedGateAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"),
            utcNow: () => now, isProcessAlive: _ => true, classifyHolder: holders.Classify,
            launch: attempt =>
            {
                launches.Add(attempt);
                return new(222, now, "C:\\fake-dotnet.exe");
            }, stop: attempt =>
            {
                stops.Add(attempt.AttemptId);
                holders.RecordExit(attempt);
                return true;
            });
        driver.EnableOwnedGroupedGateAttempts(grouped);
        var first = driver.RunMergeTrain(selection, fixture.Goals, ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
        Assert.Contains("outcome=inflight", first.Detail);
        var attempt = Assert.Single(launches);
        Assert.True(grouped.ReservesCapacity(attempt));
        var goal = fixture.Goals[0];
        var intent = new OperatorIntentRecord("cancel-train", "cancel-train", "gate-cancel", goal.Id.Value, null,
            "{\"reason\":\"stale train\",\"attemptId\":null}", [], "operator", "cli", "test", now);
        new OperatorGateCancellation(solo, grouped, new ConductEventLogWriter(workspace.ConductEventsLogPath, utcNow: () => now))
            .Apply(fixture.Kernel, goal, intent, now);
        Assert.Equal(new[] { attempt.AttemptId }, stops);
        Assert.False(grouped.ReservesCapacity(ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath)));

        var second = driver.RunMergeTrain(selection, fixture.Goals, ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
        Assert.Contains("outcome=inflight", second.Detail);
        Assert.Equal(2, launches.Count);
        Assert.NotEqual(launches[0].AttemptId, launches[1].AttemptId);
        Assert.Equal(launches[0].IdentityValue, launches[1].IdentityValue);
        var store = new MergeTrainAcceptanceStore(fixture.DatabasePath);
        Assert.Null(store.TryReadReceipt(attempt.IdentityValue));
        Assert.Null(second.Receipt);
        Assert.Null(second.RecordedReceipt);
        Assert.Empty(second.Ejections);
        Assert.All(fixture.Goals, member => Assert.Equal(GoalStatus.Verified, member.Status));
        using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM merge_train_receipts) + " +
            "(SELECT COUNT(*) FROM merge_train_landings) + (SELECT COUNT(*) FROM merge_train_ejections);";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        AssertNoMergeTrainWorkspaces(fixture.Repository);
    }

    private sealed class UncalledVerifier : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner) => throw new InvalidOperationException("Fake child must never execute a gate.");

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) =>
            throw new InvalidOperationException("Fake child must never execute focused evidence.");
    }
}
