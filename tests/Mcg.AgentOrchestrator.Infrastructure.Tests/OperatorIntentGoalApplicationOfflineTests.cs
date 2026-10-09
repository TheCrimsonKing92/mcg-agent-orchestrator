using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns a GUID workspace, SQLite databases and lease path;
// no shared process, environment mutation or timing-based synchronization is used.
public sealed class OperatorIntentGoalApplicationOfflineTests
{
    [Xunit.Fact]
    public async Task LiveLeaseRefusesWithoutClaimThenReleasedLeaseAppliesOnce()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateSeed(root);
            var baseline = await seed.Repository.LoadGoalAsync(seed.GoalId, TestContext.Current.CancellationToken);
            var coordinator = new OperatorIntentCoordinator(seed.Store);
            using (ConductorLoopLease.Acquire(seed.Workspace.OrchestratorDirectory))
            {
                var result = await OperatorIntentGoalApplication.ApplyOffline(
                    seed.Workspace, seed.Repository, coordinator, seed.Attempts, seed.GoalId);

                var refused = Assert.IsType<OperatorIntentOfflineOutcome.Refused>(result);
                Assert.Contains("conduct-loop.lock", refused.Message, StringComparison.Ordinal);
                Assert.Equal(OperatorIntentStatus.Pending, (await seed.Store.GetAsync(seed.Intent.Id, TestContext.Current.CancellationToken))!.Status);
                Assert.Equal(JsonSerializer.Serialize(baseline),
                    JsonSerializer.Serialize(await seed.Repository.LoadGoalAsync(seed.GoalId, TestContext.Current.CancellationToken)));
                Assert.True(File.Exists(LockPath(seed)));
            }

            var applied = Assert.IsType<OperatorIntentOfflineOutcome.Applied>(
                await OperatorIntentGoalApplication.ApplyOffline(
                    seed.Workspace, seed.Repository, coordinator, seed.Attempts, seed.GoalId));

            Assert.Contains(applied.Lines, line => line.Contains($"id={seed.Intent.Id}", StringComparison.Ordinal));
            await AssertAppliedOnce(seed);
            Assert.False(File.Exists(LockPath(seed)));

            Assert.IsType<OperatorIntentOfflineOutcome.NothingPending>(
                await OperatorIntentGoalApplication.ApplyOffline(
                    seed.Workspace, seed.Repository, new OperatorIntentCoordinator(seed.Store), seed.Attempts, seed.GoalId));
            await AssertAppliedOnce(seed);
            Assert.False(File.Exists(LockPath(seed)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task CompetingSaveKeepsClaimAndStoredChangeThenFreshRunAppliesOnce()
    {
        var root = CreateTempDirectory();
        try
        {
            var seed = await CreateSeed(root);
            var baseline = (await seed.Repository.LoadGoalAsync(seed.GoalId, TestContext.Current.CancellationToken))!;
            var competingObjective = baseline.Objective + " (competing)";
            var repository = new CompetingSaveRepository(seed.Repository, seed.GoalId, competingObjective);

            var notDurable = Assert.IsType<OperatorIntentOfflineOutcome.NotDurable>(
                await OperatorIntentGoalApplication.ApplyOffline(
                    seed.Workspace, repository, new OperatorIntentCoordinator(seed.Store), seed.Attempts, seed.GoalId));

            var request = Assert.Single(repository.SaveRequests!);
            Assert.True(request.RejectConflict);
            Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(request.Baseline));
            Assert.NotNull(request.HumanInputRequests);
            Assert.Equal(repository.SaveResult!.Message, notDurable.Reason);
            Assert.Equal(GoalSnapshotSaveDisposition.Skipped, repository.SaveResult.Disposition);
            Assert.Equal(OperatorIntentStatus.Claimed, (await seed.Store.GetAsync(seed.Intent.Id, TestContext.Current.CancellationToken))!.Status);
            var competing = (await seed.Repository.LoadGoalAsync(seed.GoalId, TestContext.Current.CancellationToken))!;
            Assert.Equal(competingObjective, competing.Objective);
            Assert.Equal(competingObjective, Assert.Single(competing.BriefVersions!).Text);
            Assert.DoesNotContain(competing.Timeline, item => item.OperatorIntentApplied?.IntentId == seed.Intent.Id);
            Assert.Equal(WorkTaskStatus.Assigned, Assert.Single(competing.Tasks).Status);
            Assert.False(File.Exists(LockPath(seed)));

            Assert.IsType<OperatorIntentOfflineOutcome.Applied>(
                await OperatorIntentGoalApplication.ApplyOffline(
                    seed.Workspace, seed.Repository, new OperatorIntentCoordinator(seed.Store), seed.Attempts, seed.GoalId));

            await AssertAppliedOnce(seed);
            Assert.Equal(competingObjective, (await seed.Repository.LoadGoalAsync(seed.GoalId, TestContext.Current.CancellationToken))!.Objective);
            Assert.False(File.Exists(LockPath(seed)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertAppliedOnce(Seed seed)
    {
        Assert.Equal(OperatorIntentStatus.Applied, (await seed.Store.GetAsync(seed.Intent.Id))!.Status);
        var stored = (await seed.Repository.LoadGoalAsync(seed.GoalId))!;
        var marker = Assert.Single(stored.Timeline, item => item.OperatorIntentApplied?.IntentId == seed.Intent.Id);
        Assert.Equal("applied", marker.OperatorIntentApplied!.Outcome);
        Assert.Equal(WorkTaskStatus.Running, Assert.Single(stored.Tasks).Status);
    }

    private static async Task<Seed> CreateSeed(string root)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Apply pending progress", AgentRole.Developer);
        var goal = kernel.CreateGoal("Offline operator progress", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repository.SaveAsync(kernel);
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var intent = new OperatorIntentRecord(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Progress,
            goal.Id.Value, task.Id.Value,
            JsonSerializer.Serialize(new ProgressOperatorIntentPayload(WorkTaskStatus.Running, "Offline progress."),
                JsonSerializerOptions.Web),
            [], "operator", "cli", "local-process", DateTimeOffset.UtcNow);
        await store.EnqueueAsync(intent);
        var attempts = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, workspace.ExecutionDirectory);
        return new Seed(workspace, repository, goal.Id, store, intent, attempts);
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-offline-intents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string LockPath(Seed seed) => Path.Combine(seed.Workspace.OrchestratorDirectory, "conduct-loop.lock");

    private sealed record Seed(
        OrchestratorWorkspace Workspace,
        SqliteOrchestratorStateRepository Repository,
        GoalId GoalId,
        SqliteOperatorIntentStore Store,
        OperatorIntentRecord Intent,
        ConductorParallelAcceptanceAttemptCoordinator Attempts);

    private sealed class CompetingSaveRepository(
        ITransactionalOrchestratorStateRepository inner,
        GoalId goalId,
        string objective) : ITransactionalOrchestratorStateRepository
    {
        public IReadOnlyCollection<GoalSnapshotSaveRequest>? SaveRequests { get; private set; }
        public GoalSnapshotSaveResult? SaveResult { get; private set; }

        public async Task<IReadOnlyList<GoalSnapshotSaveResult>> SaveGoalSnapshotsWithMergeAsync(
            IReadOnlyCollection<GoalSnapshotSaveRequest> goals,
            CancellationToken cancellationToken = default)
        {
            SaveRequests = goals;
            Assert.Equal(goalId.Value, Assert.Single(goals).Baseline.Id);
            await inner.TransactGoalStateAsync(goalId, (state, _) =>
            {
                Assert.NotNull(state);
                var brief = Assert.Single(state.Goal.BriefVersions!);
                // Persist a coherent competing snapshot so reload does not repair the brief
                // and create another baseline conflict during the fresh applier run.
                var competingGoal = state.Goal with
                {
                    Objective = objective,
                    BriefVersions = [brief with { Text = objective }]
                };
                return Task.FromResult((true,
                    (GoalStateSnapshot?)(state with { Goal = competingGoal }), true));
            }, cancellationToken);
            var results = await inner.SaveGoalSnapshotsWithMergeAsync(goals, cancellationToken);
            SaveResult = Assert.Single(results);
            return results;
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(goalId, Assert.Single(goalIds));
            return inner.LoadGoalsAsync(goalIds, cancellationToken);
        }

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);
        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(kernel, cancellationToken);
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(cancellationToken);
        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListConductLoopGoalMetadataAsync(cancellationToken);
        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) => inner.ListGoalIdsWithCompletedHumanInputAsync(goalIds, cancellationToken);
        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
            inner.ListModelFitHistoryAsync(cancellationToken);
        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize, CancellationToken cancellationToken = default) =>
            inner.BuildModelOutcomeScorecardAsync(windowSize, cancellationToken);
        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default) =>
            inner.QueryBestFitForRoleAsync(role, cancellationToken);
        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) => inner.TransactAsync(transaction, cancellationToken);
        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) => inner.TransactAsync(transaction, cancellationToken);
        public Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default) =>
            inner.LoadGoalAsync(goalId, cancellationToken);
        public Task SaveGoalSnapshotsAsync(IReadOnlyCollection<GoalSnapshot> goals, CancellationToken cancellationToken = default) =>
            inner.SaveGoalSnapshotsAsync(goals, cancellationToken);
        public Task<T> TransactGoalAsync<T>(GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default) => inner.TransactGoalAsync(goalId, transaction, cancellationToken);
        public Task<T> TransactGoalStateAsync<T>(GoalId goalId,
            Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
            CancellationToken cancellationToken = default) => inner.TransactGoalStateAsync(goalId, transaction, cancellationToken);
    }
}
