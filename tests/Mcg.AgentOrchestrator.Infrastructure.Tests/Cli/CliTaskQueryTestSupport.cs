using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public abstract class CliTaskQueryTestSupport
{
    protected static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcg-cli-task-query-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    protected static string CaptureConsole(Action action) => AsyncLocalConsoleRouter.Capture(action);

    protected sealed class QueryOnlyStateRepository(AgentOrchestratorKernel kernel) : IOrchestratorStateQueries
    {
        private readonly AgentOrchestratorKernel _kernel = Clone(kernel);

        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(OpenRequests(_kernel));

        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) => Task.FromResult(TerminalHolds(_kernel, goalIds));

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(FilterGoals(_kernel, goalIds));

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildMetadata(_kernel));
    }

    protected sealed class ProbeStateRepository(AgentOrchestratorKernel kernel) : IOrchestratorStateOutboxRepository
    {
        private readonly AgentOrchestratorKernel _kernel = Clone(kernel);
        private readonly Dictionary<string, OrchestratorStateOutboxMessage> _outbox = new(StringComparer.Ordinal);
        private readonly List<string?> _observedWriteOperationTags = [];

        public IReadOnlyList<string?> ObservedWriteOperationTags => _observedWriteOperationTags.AsReadOnly();

        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return Task.FromResult(OpenRequests(_kernel));
        }

        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return Task.FromResult(TerminalHolds(_kernel, goalIds));
        }

        public bool ThrowOnOutbox { get; init; }

        public bool DisappearSelectedGoals { get; init; }

        public HashSet<string> UnavailableGoalIds { get; } = new(StringComparer.Ordinal);

        public int FullLoadAttempts { get; private set; }

        public int LoadGoalsCount { get; private set; }

        public int LoadGoalCount { get; private set; }

        public int ListGoalMetadataCount { get; private set; }

        public int MutationAttempts { get; private set; }

        public int SaveAttempts { get; private set; }

        public int MergeSaveAttempts { get; private set; }

        public int ListOutboxMessagesCount { get; private set; }

        public int OutboxClaimAttempts { get; private set; }

        public List<string> LoadedGoalIds { get; } = [];

        public void SeedOutboxMessage(OrchestratorStateOutboxMessage message) => _outbox.Add(message.Id, message);

        public bool HasOutboxMessage(string id) => _outbox.ContainsKey(id);

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            FullLoadAttempts++;
            throw new InvalidOperationException("Full-kernel hydration is not allowed for this test.");
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            LoadGoalsCount++;
            if (DisappearSelectedGoals)
                return Task.FromResult(new AgentOrchestratorKernel());

            var availableGoalIds = goalIds
                .Where(goalId => !UnavailableGoalIds.Contains(goalId.Value))
                .ToArray();
            var filtered = FilterGoals(_kernel, availableGoalIds);
            LoadedGoalIds.AddRange(filtered.Goals.Select(goal => goal.Id.Value));
            return Task.FromResult(filtered);
        }

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            ListGoalMetadataCount++;
            return Task.FromResult(BuildMetadata(_kernel));
        }

        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return Task.FromResult(BuildMetadata(_kernel));
        }

        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return Task.FromResult<IReadOnlyList<GoalId>>([]);
        }

        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return Task.FromResult<IReadOnlyList<ModelFitHistoryRow>>([]);
        }

        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return Task.FromResult<IReadOnlyList<ModelOutcomeRecord>>([]);
        }

        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(
            AgentRole role,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return Task.FromResult<ModelFitBestFit?>(null);
        }

        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            RecordSave();
            return Task.CompletedTask;
        }

        public Task SaveGoalSnapshotsAsync(
            IReadOnlyCollection<GoalSnapshot> goals,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            RecordSave();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GoalSnapshotSaveResult>> SaveGoalSnapshotsWithMergeAsync(
            IReadOnlyCollection<GoalSnapshotSaveRequest> goals,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            MergeSaveAttempts++;
            RecordSave();
            return Task.FromResult<IReadOnlyList<GoalSnapshotSaveResult>>([]);
        }

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return ThrowMutation<T>();
        }

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return ThrowMutation<T>();
        }

        public Task<T> TransactWithOutboxAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(
                bool ShouldSave,
                T Result,
                IReadOnlyList<OrchestratorStateOutboxMessage> OutboxMessages)>> transaction,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return ThrowMutation<T>();
        }

        public Task<GoalSnapshot?> LoadGoalAsync(
            GoalId goalId,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            LoadGoalCount++;
            return Task.FromResult<GoalSnapshot?>(_kernel.ExportSnapshot().Goals.SingleOrDefault(goal => goal.Id == goalId.Value));
        }

        public Task<T> TransactGoalAsync<T>(
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return ThrowMutation<T>();
        }

        public Task<T> TransactGoalStateAsync<T>(
            GoalId goalId,
            Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            return ThrowMutation<T>();
        }

        public Task<IReadOnlyList<OrchestratorStateOutboxMessage>> ListOutboxMessagesAsync(
            string kind,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            ListOutboxMessagesCount++;
            if (ThrowOnOutbox)
                throw new InvalidOperationException("Outbox reads are not allowed for this test.");

            return Task.FromResult<IReadOnlyList<OrchestratorStateOutboxMessage>>(_outbox.Values
                .Where(message => message.Kind.Equals(kind, StringComparison.Ordinal))
                .ToArray());
        }

        public Task<bool> TryProcessOutboxMessageAsync(
            string id,
            Func<OrchestratorStateOutboxMessage, CancellationToken, Task<OrchestratorStateOutboxProcessingResult>> processor,
            CancellationToken cancellationToken = default)
        {
            ObserveWriteOperationTag();
            OutboxClaimAttempts++;
            if (ThrowOnOutbox)
                throw new InvalidOperationException("Outbox claims are not allowed for this test.");

            return Task.FromResult(false);
        }

        private void ObserveWriteOperationTag() =>
            _observedWriteOperationTags.Add(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);

        private Task<T> ThrowMutation<T>()
        {
            MutationAttempts++;
            throw new InvalidOperationException("State mutation is not allowed for this test.");
        }

        private void RecordSave()
        {
            SaveAttempts++;
            throw new InvalidOperationException("State writes are not allowed for this test.");
        }
    }

    private static AgentOrchestratorKernel FilterGoals(
        AgentOrchestratorKernel kernel,
        IReadOnlyCollection<GoalId> goalIds)
    {
        var ids = goalIds.Select(goalId => goalId.Value).ToHashSet(StringComparer.Ordinal);
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Where(goal => ids.Contains(goal.Id)).ToArray(),
            HumanInputRequests = snapshot.HumanInputRequests
                .Where(request => ids.Contains(request.GoalId))
                .ToArray()
        });
    }

    private static IReadOnlyList<GoalSummary> BuildMetadata(AgentOrchestratorKernel kernel) =>
        kernel.Goals
            .Select(goal => new GoalSummary(
                goal.Id.Value,
                goal.Status.ToString(),
                goal.Objective,
                (goal.Timeline.LastOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue).ToString("O"),
                CreatedAt: goal.Timeline.FirstOrDefault()?.OccurredAt))
            .OrderByDescending(goal => goal.UpdatedAt, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<HumanInputRequestSnapshot> OpenRequests(AgentOrchestratorKernel kernel) =>
        kernel.ExportSnapshot().HumanInputRequests.Where(request => !request.IsCompleted).ToArray();

    private static IReadOnlyList<TerminalOwnerQuestionHold> TerminalHolds(
        AgentOrchestratorKernel kernel, IReadOnlyCollection<GoalId> goalIds) =>
        kernel.Goals.Where(goal => goalIds.Contains(goal.Id) &&
            goal.Status is GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded &&
            goal.CurrentHold?.State.Equals("steward-owner-question", StringComparison.OrdinalIgnoreCase) == true)
            .Select(goal => new TerminalOwnerQuestionHold(goal.Id.Value, goal.CurrentHold!.Identity,
                goal.CurrentHold.State, goal.CurrentHold.Blocker, goal.CurrentHold.StartedAt)).ToArray();

    private static AgentOrchestratorKernel Clone(AgentOrchestratorKernel kernel) =>
        AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
}
