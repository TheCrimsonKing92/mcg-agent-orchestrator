using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public abstract class CliGoalUnparkTestSupport : CliTaskQueryTestSupport
{
    private protected static async Task<UnparkSeed> CreateParkedSeed(string root, string objective = "Parked goal")
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Preserve this task", AgentRole.Developer);
        var goal = kernel.CreateGoal(objective, [task]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Preserve this human input.");
        kernel.ParkGoal(goal.Id, "Seed parked state");
        await repository.SaveAsync(kernel);
        return new UnparkSeed(workspace, repository, goal.Id, task.Id, request.Id);
    }

    private protected static async Task<UnparkSeed> CreateParkedOpenHumanWaitSeed(string root)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Preserve this open human wait", AgentRole.Developer);
        var goal = kernel.CreateGoal("Parked goal with open human wait", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Preserve this open human input.");
        await repository.SaveAsync(kernel);
        await repository.SaveGoalSnapshotsAsync([kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Parked }]);
        return new UnparkSeed(workspace, repository, goal.Id, task.Id, request.Id);
    }

    private protected static CommandResult RunCommand(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        Goal? selectedGoal = null)
    {
        bool? changed = null;
        Exception? error = null;
        Goal? currentGoal = selectedGoal;
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var output = CaptureConsole(() =>
        {
            try
            {
                changed = CliPersistentStateRunner.ExecuteCommand(
                    args,
                    repository,
                    workspace,
                    ref agents,
                    new InMemoryModelProviderRegistry([]),
                    ref profiles,
                    ref currentGoal);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        return new CommandResult(changed, output, error, currentGoal);
    }

    private protected static string EventPath(OrchestratorWorkspace workspace, GoalId goalId) =>
        Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goalId.Value}.jsonl");

    private protected static Task<GoalStateSnapshot?> LoadGoalStateExactly(
        ITransactionalOrchestratorStateRepository repository,
        GoalId goalId) =>
        repository.TransactGoalStateAsync(
            goalId,
            (state, _) => Task.FromResult((false, (GoalStateSnapshot?)null, state)));

    private protected sealed record UnparkSeed(
        OrchestratorWorkspace Workspace,
        SqliteOrchestratorStateRepository Repository,
        GoalId GoalId,
        TaskId TaskId,
        HumanInputRequestId HumanInputRequestId);

    private protected sealed record CommandResult(
        bool? Changed,
        string Output,
        Exception? Error,
        Goal? CurrentGoal);

    private protected sealed class GoalTransactionProbeRepository(
        ITransactionalOrchestratorStateRepository inner) : ITransactionalOrchestratorStateRepository
    {
        public Func<int, GoalSnapshot, CancellationToken, Task>? AfterApplication { get; init; }
        public bool CancelBeforeCommit { get; init; }
        public Exception? ThrowBeforeCommit { get; init; }
        public Exception? ThrowAfterCommit { get; init; }
        public int WholeKernelLoadCount { get; private set; }
        public int WholeKernelSaveCount { get; private set; }
        public int ApplicationCount { get; private set; }

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
        {
            WholeKernelLoadCount++;
            throw new InvalidOperationException("whole-kernel load sentinel");
        }

        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
        {
            WholeKernelSaveCount++;
            throw new InvalidOperationException("whole-kernel save sentinel");
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) =>
            inner.LoadGoalsAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListConductLoopGoalMetadataAsync(cancellationToken);

        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) =>
            inner.ListGoalIdsWithCompletedHumanInputAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListModelFitHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
            CancellationToken cancellationToken = default) =>
            inner.BuildModelOutcomeScorecardAsync(windowSize, cancellationToken);

        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(
            AgentRole role,
            CancellationToken cancellationToken = default) =>
            inner.QueryBestFitForRoleAsync(role, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<GoalSnapshot?> LoadGoalAsync(
            GoalId goalId,
            CancellationToken cancellationToken = default) =>
            inner.LoadGoalAsync(goalId, cancellationToken);

        public Task SaveGoalSnapshotsAsync(
            IReadOnlyCollection<GoalSnapshot> goals,
            CancellationToken cancellationToken = default) =>
            inner.SaveGoalSnapshotsAsync(goals, cancellationToken);

        public Task<T> TransactGoalAsync<T>(
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            TransactGoalAsync("test:unpark", goalId, transaction, cancellationToken);

        public async Task<T> TransactGoalAsync<T>(
            string operationName,
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var result = await inner.TransactGoalAsync(
                operationName,
                goalId,
                async (snapshot, token) =>
                {
                    var application = await transaction(snapshot, token);
                    if (application.ShouldSave && snapshot is not null)
                    {
                        ApplicationCount++;
                        if (AfterApplication is not null)
                        {
                            await AfterApplication(ApplicationCount, snapshot, token);
                        }

                        if (CancelBeforeCommit)
                        {
                            cancellation.Cancel();
                        }

                        if (ThrowBeforeCommit is not null)
                        {
                            throw ThrowBeforeCommit;
                        }
                    }

                    return application;
                },
                cancellation.Token);

            if (ThrowAfterCommit is not null)
            {
                throw ThrowAfterCommit;
            }

            return result;
        }

        public Task<T> TransactGoalStateAsync<T>(
            GoalId goalId,
            Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactGoalStateAsync(goalId, transaction, cancellationToken);
    }
}
