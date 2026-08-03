namespace Mcg.AgentOrchestrator.Core;

public sealed record ProgressiveReviewGlanceGuardReceipt(
    string ScopeConfidence,
    IReadOnlyList<string> TrustedScopePaths,
    IReadOnlyList<string> ChangedFiles,
    string Note,
    string EvidenceLine,
    string ReasonCode,
    bool LegacyPhraseHintMatched,
    string OriginalVerdict,
    string FinalVerdict,
    bool Downgraded,
    string DowngradeReason,
    string StructuralComparison);

public interface IGoalLifecycleEventWriter
{
    void AppendTimelineEvent(ProgressEvent progressEvent);
    void AppendGoalCreated(GoalId goalId, string objective);
    void AppendClarificationNeeded(GoalId goalId, string clarificationId);
    void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> staleTopicKeys, string recoveryCommand);
    void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName);
    void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt);
    void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures);
    void AppendAcceptanceCriterionWaived(
        GoalId goalId,
        string criterion,
        string actor,
        DateTimeOffset recordedAt,
        string reason,
        string capturedAcceptanceCriteriaHash);
    void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch);
    void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha);
    void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source);
    void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, GoalStatus status, string reason, string source) =>
        AppendGoalEscalated(goalId, state, reason, source);
    void AppendGoalEvictedFromConductor(GoalId goalId, GoalStatus status, string trigger) { }
    void AppendCleanedUp(GoalId goalId);
    void AppendProgressiveReviewGlanceReceipt(
        GoalId goalId,
        TaskId taskId,
        string trigger,
        string inputsHash,
        string verdict,
        string note,
        int inputTokens,
        int outputTokens,
        int totalTokens,
        TimeSpan wallTime,
        string? model,
        string? profile);
    void AppendProgressiveReviewGlanceGuardReceipt(
        GoalId goalId,
        TaskId taskId,
        ProgressiveReviewGlanceGuardReceipt receipt) { }
    void AppendProgressiveReviewGlanceSummary(
        GoalId goalId,
        int totalGlances,
        int onTrack,
        int concern,
        int fundamentalMisdirection,
        int invalid,
        int totalTokens);
}

public sealed class NullGoalLifecycleEventWriter : IGoalLifecycleEventWriter
{
    public static NullGoalLifecycleEventWriter Instance { get; } = new();

    public void AppendTimelineEvent(ProgressEvent progressEvent) { }
    public void AppendGoalCreated(GoalId goalId, string objective) { }
    public void AppendClarificationNeeded(GoalId goalId, string clarificationId) { }
    public void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> staleTopicKeys, string recoveryCommand) { }
    public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) { }
    public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) { }
    public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) { }
    public void AppendAcceptanceCriterionWaived(GoalId goalId, string criterion, string actor, DateTimeOffset recordedAt, string reason, string capturedAcceptanceCriteriaHash) { }
    public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) { }
    public void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha) { }
    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) { }
    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, GoalStatus status, string reason, string source) { }
    public void AppendGoalEvictedFromConductor(GoalId goalId, GoalStatus status, string trigger) { }
    public void AppendCleanedUp(GoalId goalId) { }
    public void AppendProgressiveReviewGlanceReceipt(
        GoalId goalId,
        TaskId taskId,
        string trigger,
        string inputsHash,
        string verdict,
        string note,
        int inputTokens,
        int outputTokens,
        int totalTokens,
        TimeSpan wallTime,
        string? model,
        string? profile) { }
    public void AppendProgressiveReviewGlanceGuardReceipt(
        GoalId goalId,
        TaskId taskId,
        ProgressiveReviewGlanceGuardReceipt receipt) { }
    public void AppendProgressiveReviewGlanceSummary(
        GoalId goalId,
        int totalGlances,
        int onTrack,
        int concern,
        int fundamentalMisdirection,
        int invalid,
        int totalTokens) { }
}
