using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IOrchestratorStateRepository
{
    Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default);

    Task<AgentOrchestratorKernel> LoadGoalsAsync(
        IReadOnlyCollection<GoalId> goalIds,
        CancellationToken cancellationToken = default);

    Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
        int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
        CancellationToken cancellationToken = default);

    Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default);
}

public interface ITransactionalOrchestratorStateRepository : IOrchestratorStateRepository
{
    Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default);

    Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default);

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

    Task<IReadOnlyList<GoalSnapshotSaveResult>> SaveGoalSnapshotsWithMergeAsync(
        IReadOnlyCollection<GoalSnapshotSaveRequest> goals,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support conductor tick snapshot merge persistence.");

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
}

public sealed record GoalSnapshotSaveRequest(GoalSnapshot Baseline, GoalSnapshot Current);

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
