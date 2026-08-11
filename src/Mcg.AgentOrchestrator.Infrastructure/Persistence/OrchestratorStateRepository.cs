using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IOrchestratorStateRepository
{
    Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default);

    Task<AgentOrchestratorKernel> LoadGoalsAsync(
        IReadOnlyCollection<GoalId> goalIds,
        CancellationToken cancellationToken = default);

    Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default);

    Task SaveAsync(
        string operationName,
        AgentOrchestratorKernel kernel,
        CancellationToken cancellationToken = default) =>
        SaveAsync(kernel, cancellationToken);

    Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
        IReadOnlyCollection<GoalId> goalIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
        int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
        CancellationToken cancellationToken = default);

    Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default);
}

public interface ITransactionalOrchestratorStateRepository : IOrchestratorStateRepository
{
    bool SupportsGoalCheckpointContainment => false;

    Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default);

    Task<T> TransactAsync<T>(
        string operationName,
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default) =>
        TransactAsync(transaction, cancellationToken);

    Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default);

    Task<T> TransactAsync<T>(
        string operationName,
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default) =>
        TransactAsync(transaction, cancellationToken);

    /// <summary>
    /// Returns the stored snapshot for one goal, or null if the goal does not exist.
    /// Uses a bare SELECT under WAL snapshot isolation; no write lock is held.
    /// </summary>
    Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the supplied goal snapshots as one storage transaction. Intended for callers that
    /// already ran the per-goal state machines in memory and only need a durable checkpoint.
    /// </summary>
    Task SaveGoalSnapshotsAsync(
        IReadOnlyCollection<GoalSnapshot> goals,
        CancellationToken cancellationToken = default);

    Task SaveGoalSnapshotsAsync(
        string operationName,
        IReadOnlyCollection<GoalSnapshot> goals,
        CancellationToken cancellationToken = default) =>
        SaveGoalSnapshotsAsync(goals, cancellationToken);

    Task<IReadOnlyList<GoalSnapshotSaveResult>> SaveGoalSnapshotsWithMergeAsync(
        IReadOnlyCollection<GoalSnapshotSaveRequest> goals,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support conductor tick snapshot merge persistence.");

    /// <summary>
    /// Persists conductor checkpoints independently so an exhausted transient lock for one goal does
    /// not prevent later goals in the same tick from becoming durable. Only typed SQLITE_BUSY and
    /// SQLITE_LOCKED results are converted to held outcomes; every other failure remains fail-closed.
    /// </summary>
    Task<IReadOnlyList<GoalSnapshotCheckpointResult>> CheckpointGoalSnapshotsAsync(
        IReadOnlyCollection<GoalSnapshotSaveRequest> goals,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support conductor checkpoint containment.");

    /// <summary>
    /// Loads the goal's current snapshot outside a transaction, calls the delegate to produce
    /// a new snapshot, then performs a short BEGIN IMMEDIATE compare-and-swap write of only
    /// that one goal row. Detects concurrent writes via an optimistic version field; a mismatch
    /// retries the full load-mutate-CAS cycle (same retry budget as SQLITE_BUSY).
    /// </summary>
    Task<T> TransactGoalAsync<T>(
        GoalId goalId,
        Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
        CancellationToken cancellationToken = default);

    Task<T> TransactGoalAsync<T>(
        string operationName,
        GoalId goalId,
        Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
        CancellationToken cancellationToken = default) =>
        TransactGoalAsync(goalId, transaction, cancellationToken);

    /// <summary>
    /// Performs the same goal-scoped optimistic transaction while also loading and atomically
    /// persisting that goal's human-input requests. Use this path when a goal mutation can create
    /// or complete a human wait.
    /// </summary>
    Task<T> TransactGoalStateAsync<T>(
        GoalId goalId,
        Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support goal-scoped state transactions.");

    Task<T> TransactGoalStateAsync<T>(
        string operationName,
        GoalId goalId,
        Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
        CancellationToken cancellationToken = default) =>
        TransactGoalStateAsync(goalId, transaction, cancellationToken);
}

public sealed record GoalStateSnapshot(
    GoalSnapshot Goal,
    IReadOnlyList<HumanInputRequestSnapshot> HumanInputRequests);

public sealed record OrchestratorStateOutboxMessage(
    string Id,
    string Kind,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public enum OrchestratorStateOutboxDisposition
{
    Complete,
    Quarantine
}

public sealed record OrchestratorStateOutboxProcessingResult(
    OrchestratorStateOutboxDisposition Disposition,
    string? Detail = null)
{
    public static OrchestratorStateOutboxProcessingResult Completed { get; } =
        new(OrchestratorStateOutboxDisposition.Complete);

    public static OrchestratorStateOutboxProcessingResult Quarantined(string detail) =>
        new(OrchestratorStateOutboxDisposition.Quarantine, detail);
}

public interface IOrchestratorStateOutboxRepository : ITransactionalOrchestratorStateRepository
{
    Task<T> TransactWithOutboxAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(
            bool ShouldSave,
            T Result,
            IReadOnlyList<OrchestratorStateOutboxMessage> OutboxMessages)>> transaction,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrchestratorStateOutboxMessage>> ListOutboxMessagesAsync(
        string kind,
        CancellationToken cancellationToken = default);

    Task<bool> TryProcessOutboxMessageAsync(
        string id,
        Func<OrchestratorStateOutboxMessage, CancellationToken, Task<OrchestratorStateOutboxProcessingResult>> processor,
        CancellationToken cancellationToken = default);
}

public sealed record GoalSnapshotSaveRequest(
    GoalSnapshot Baseline,
    GoalSnapshot Current,
    IReadOnlyList<HumanInputRequestSnapshot>? HumanInputRequests = null);

public sealed record GoalSnapshotSaveResult(
    string GoalId,
    GoalSnapshotSaveDisposition Disposition,
    GoalSnapshot? PersistedSnapshot,
    string Message);

public enum GoalSnapshotSaveDisposition
{
    Saved,
    Merged,
    Skipped
}

public enum GoalSnapshotCheckpointDisposition
{
    Durable,
    Held
}

public sealed record GoalSnapshotCheckpointResult(
    string GoalId,
    GoalSnapshotCheckpointDisposition Disposition,
    GoalSnapshotSaveResult? SaveResult,
    string Store,
    string DatabasePath,
    string Operation,
    int? SqliteErrorCode = null,
    int? SqliteExtendedErrorCode = null,
    int AttemptCount = 1,
    double ElapsedMilliseconds = 0)
{
    public bool IsDurable => Disposition == GoalSnapshotCheckpointDisposition.Durable;
}
