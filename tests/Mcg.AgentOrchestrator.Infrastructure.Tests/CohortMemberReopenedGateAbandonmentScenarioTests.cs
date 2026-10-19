using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: this class owns its shared repository; all child launches and stops are fake.
public sealed class CohortMemberReopenedGateAbandonmentScenarioTests(MergeTrainDeferralSharedFixture fixture)
    : AcceptanceCohortWorkflowTests, IClassFixture<MergeTrainDeferralSharedFixture>
{
    [Fact]
    public async Task RunMergeTrain_ReopenedMember_ReleasesSlotAndReadmitsVerifiedMembers()
    {
        fixture.Reset();
        var now = DateTimeOffset.UnixEpoch.AddDays(20000);
        var kernel = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot(), new Clock(now));
        var goals = fixture.Goals.Select(member => kernel.Goals.Single(goal => goal.Id == member.Id)).ToArray();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Repository);
        var cleanup = CreateIsolatedCleanupContext(fixture.Repository).Hooks;
        var solo = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "parallel-attempts"), workspace.IntegrationBranch,
            utcNow: () => now, buildStorageRoot: cleanup.BuildStorageRoot);
        var driver = new ConductorDriver(kernel, workspace, new UncalledVerifier(),
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), solo, cleanup);
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
        var first = driver.RunMergeTrain(ProjectTrainSelection(driver, goals), goals,
            ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
        Assert.Contains("outcome=inflight", first.Detail);
        var attempt = Assert.Single(launches);
        Assert.True(grouped.ReservesCapacity(attempt));
        Assert.Equal(goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
            driver.GetActiveCohortGateMemberGoalIds().Order(StringComparer.Ordinal));

        var reopened = goals[0];
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var intent = await store.EnqueueAsync(new OperatorIntentRecord("reopen-train", "reopen-train", "retry",
            reopened.Id.Value, reopened.Tasks.Single().Id.Value,
            JsonSerializer.Serialize(new RetryOperatorIntentPayload("New test finding", null,
                RetryCause: RetryCause.NewTestFinding), OperatorIntentJson.Options), [], "operator", "cli", "test", now));
        var intents = new OperatorIntentCoordinator(store, utcNow: () => now)
        {
            ReopenedMemberGateAbandonment = new(grouped,
                new ConductEventLogWriter(workspace.ConductEventsLogPath, utcNow: () => now), () => now)
        };

        var application = OperatorIntentGoalApplication.ApplyPending(kernel, reopened, intents, solo);

        Assert.True(application.Result.MutatedGoalState);
        Assert.Equal(new[] { attempt.AttemptId }, stops);
        Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
        Assert.Equal(GoalStatus.Active, reopened.Status);
        Assert.All(goals.Skip(1), member => Assert.Equal(GoalStatus.Verified, member.Status));
        Assert.Equal(now, ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath).ReconciledAt);
        AssertEmptyTrainTables();
        intents.CompletePersisted([reopened.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);

        var second = driver.RunAcceptanceCohort(ProjectSelection(driver, goals[1], goals[2]), goals.Skip(1).ToArray(),
            ConductorAutonomyPolicy.Permissive, runGateInBackground: true);

        Assert.Contains("outcome=inflight", second.Detail);
        Assert.Equal(2, launches.Count);
        Assert.NotEqual(attempt.AttemptId, launches[1].AttemptId);
        Assert.Equal("cohort", launches[1].Kind);
        Assert.Equal(goals.Skip(1).Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
            launches[1].Members.Select(member => member.GoalId).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { attempt.AttemptId }, stops);
        Assert.Null(second.Receipt);
        Assert.Equal(goals.Skip(1).Select(goal => goal.Id.Value).Order(StringComparer.Ordinal),
            second.MemberResults.Keys.Order(StringComparer.Ordinal));
        AssertEmptyTrainTables();
        AssertNoMergeTrainWorkspaces(fixture.Repository);
    }

    private void AssertEmptyTrainTables()
    {
        using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM merge_train_receipts) + " +
            "(SELECT COUNT(*) FROM merge_train_landings) + (SELECT COUNT(*) FROM merge_train_ejections);";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    private sealed class Clock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }

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
