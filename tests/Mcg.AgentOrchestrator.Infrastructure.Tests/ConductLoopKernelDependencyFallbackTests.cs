using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its database and journal root; no process or environment mutation.
public sealed class ConductLoopKernelDependencyFallbackTests
{
    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task LoadKernel_NonHydratedDependency_ReusesMetadataAndPreservesLanding(bool landed)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var inner = new SqliteOrchestratorStateRepository(db);
            var kernel = new AgentOrchestratorKernel();
            var dependency = kernel.CreateGoal("Completed dependency", [
                new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)
            ]);
            var dependent = kernel.CreateGoal("Hydrated dependent", [
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(dependency.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(dependent.Id, AgentCatalog.Default().Agents);
            kernel.SetGoalDependency(dependent.Id, dependency.Id);
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal.Id == dependency.Id.Value
                    ? goal with { Status = GoalStatus.Completed }
                    : goal).ToArray()
            });
            await inner.SaveAsync(kernel);
            if (landed)
            {
                GoalOperationJournal.RecordLandingIntent(root, dependency,
                    $"goal/{dependency.Id.Value[..8]}", "main", "abcdef1234567890", "test:landing");
                GoalOperationJournal.Completed(root, dependency, "conductor:land", "Verified fixture landing.");
            }

            var repository = new RecordingRepository(inner);
            var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);

            var hydrated = Assert.Single(loaded.Goals);
            Assert.Equal(dependent.Id, hydrated.Id);
            Assert.Contains(dependency.Id, hydrated.DependsOn);
            Assert.DoesNotContain(loaded.Goals, goal => goal.Id == dependency.Id);
            Assert.Equal(1, repository.ConductMetadataCalls);
            Assert.Equal(0, repository.FullMetadataCalls);
            Assert.True(loaded.TryGetKnownDependencyGoalStatus(dependency.Id, out var status));
            Assert.Equal(GoalStatus.Completed.ToString(), status);
            Assert.Equal(landed, loaded.IsKnownCompletedDependencyGoal(dependency.Id));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private sealed class RecordingRepository(SqliteOrchestratorStateRepository inner)
        : ITransactionalOrchestratorStateRepository
    {
        public int FullMetadataCalls { get; private set; }
        public int ConductMetadataCalls { get; private set; }

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            FullMetadataCalls++;
            return inner.ListGoalMetadataAsync(cancellationToken);
        }

        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            ConductMetadataCalls++;
            return inner.ListConductLoopGoalMetadataAsync(cancellationToken);
        }

        // Inherit ListGoalIdStatusesAsync: switching the fallback to another query must also be counted.
        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);

        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(kernel, cancellationToken);

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            inner.LoadGoalsAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            inner.ListGoalIdsWithCompletedHumanInputAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
            inner.ListModelFitHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize, CancellationToken cancellationToken = default) =>
            inner.BuildModelOutcomeScorecardAsync(windowSize, cancellationToken);

        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(
            AgentRole role, CancellationToken cancellationToken = default) =>
            inner.QueryBestFitForRoleAsync(role, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default) =>
            inner.LoadGoalAsync(goalId, cancellationToken);

        public Task SaveGoalSnapshotsAsync(
            IReadOnlyCollection<GoalSnapshot> goals, CancellationToken cancellationToken = default) =>
            inner.SaveGoalSnapshotsAsync(goals, cancellationToken);

        public Task<T> TransactGoalAsync<T>(
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactGoalAsync(goalId, transaction, cancellationToken);
    }
}
