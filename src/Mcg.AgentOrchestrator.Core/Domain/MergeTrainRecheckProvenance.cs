namespace Mcg.AgentOrchestrator.Core;

/// <summary>Safety inputs and execution evidence composing a red gate with a green recheck.</summary>
public sealed record MergeTrainRecheckProvenance(
    string RedTrainId,
    string RedReceiptId,
    string TrainAttemptId,
    string RecheckAttemptId,
    GoalId CulpritGoalId,
    string AttributionSource,
    IReadOnlyList<string> Selection,
    bool DependencySelectionResolved,
    IReadOnlyList<string> DependencyClasses,
    bool? LandingPathsDisjoint,
    string RecheckMainRevision,
    string RecheckTreeRevision,
    IReadOnlyList<string> ExecutedSelectors,
    int NotRunSelectionCount);
