namespace Mcg.AgentOrchestrator.Core;

public sealed record OrchestratorSnapshot(
    IReadOnlyList<GoalSnapshot> Goals,
    IReadOnlyList<HumanInputRequestSnapshot> HumanInputRequests);

public sealed record GoalSnapshot(
    string Id,
    string Objective,
    GoalStatus Status,
    IReadOnlyList<TaskSnapshot> Tasks,
    IReadOnlyList<ProgressEventSnapshot> Timeline,
    IReadOnlyList<string>? DependsOn = null,
    string? SourceBacklogItemId = null,
    RefinedSpecSnapshot? RefinedSpec = null,
    AcceptanceFailureSnapshot? LatestAcceptanceFailure = null);

public sealed record AcceptanceFailureSnapshot(
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> FailedChecks);

public sealed record RefinedSpecSnapshot(
    string BehavioralContract,
    IReadOnlyList<string> AcceptanceCriteria,
    string VerificationClass,
    IReadOnlyList<RefinedSpecDecisionSnapshot> Decisions,
    IReadOnlyList<RefinedSpecOpenQuestionSnapshot> OpenQuestions);

public sealed record RefinedSpecDecisionSnapshot(string Question, string Choice, string Rationale);

public sealed record RefinedSpecOpenQuestionSnapshot(
    string Id,
    string Question,
    string ForkKind,
    string Status,
    string? Answer = null);

public sealed record TaskSnapshot(
    string Id,
    string Description,
    AgentRole RequiredRole,
    WorkTaskStatus Status,
    string? AssignedAgentId,
    TaskExecutionSnapshot? LastExecution,
    TaskVerificationSnapshot? LastVerification,
    IReadOnlyList<TaskVerificationSnapshot>? VerificationHistory,
    TaskDispatchSnapshot? LastDispatch,
    TaskProcessSnapshot? LastProcess,
    string? VerificationPlan = null,
    DateTimeOffset? SubscriptionRetryAfter = null,
    string? SubscriptionLimitReviewNote = null,
    DateTimeOffset? SubscriptionLimitReviewedAt = null,
    int SubscriptionLimitReviewedFailureCount = 0,
    int CriterionRetryCount = 0,
    IReadOnlyList<string>? CriterionRetryFeedback = null,
    int EmptyOutputRetryCount = 0);

public sealed record TaskExecutionSnapshot(
    string AgentId,
    string AgentName,
    string ProviderName,
    string ModelName,
    string Output,
    string StopReason,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset CompletedAt,
    TaskComplexity? TaskComplexity = null,
    int? MaxOutputTokens = null,
    int? PromptCharacterCount = null);

public sealed record TaskVerificationSnapshot(
    string Command,
    string WorkingDirectory,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset CompletedAt,
    string? ModelFitNote = null,
    string? StandardOutputPath = null,
    string? StandardErrorPath = null,
    ProviderFailureKind ProviderFailureKind = ProviderFailureKind.Unknown);

public sealed record TaskDispatchSnapshot(
    string WorkerName,
    string Command,
    string WorkingDirectory,
    DateTimeOffset DispatchedAt,
    string? ProviderName = null,
    string? ModelName = null,
    string? ReasoningEffort = null,
    TaskComplexity? TaskComplexity = null,
    int? PromptCharacterCount = null,
    bool UsesComplexModel = false,
    string? BaseCommit = null,
    string? ResultCommit = null,
    bool SandboxLowIntegrity = false,
    string? PromptPath = null,
    ProviderKind WorkerProviderKind = ProviderKind.Unknown);

public sealed record TaskProcessSnapshot(
    int ProcessId,
    string Command,
    string WorkingDirectory,
    string StandardOutputPath,
    string StandardErrorPath,
    string ExitCodePath,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    bool WasCancelled = false,
    IReadOnlyList<int>? OwnedProcessIds = null,
    TaskProcessResourceAccountingSnapshot? ResourceAccounting = null);

public sealed record TaskProcessResourceAccountingSnapshot(
    long CpuMilliseconds,
    long PeakMemoryBytes,
    long IoBytes,
    bool Reaped = false);

public sealed record ProgressEventSnapshot(
    string GoalId,
    string? TaskId,
    ProgressKind Kind,
    string Message,
    DateTimeOffset OccurredAt);

public sealed record HumanInputRequestSnapshot(
    string Id,
    string GoalId,
    string? TaskId,
    string Question,
    DateTimeOffset RequestedAt,
    HumanWaitKind Kind = HumanWaitKind.SpecClarification,
    bool? IsAutoDefaultable = null,
    bool? IsDismissible = null,
    bool IsAnswerRequired = true,
    bool? IsExternallyBlocked = null,
    string? SuggestedDefaultAnswer = null,
    string? ResumeCommand = null,
    bool IsCompleted = false,
    string? Answer = null,
    DateTimeOffset? AnsweredAt = null,
    bool WasDismissed = false);

public static class VerificationTextBounds
{
    private const int PreviewHeadChars = 4096;
    private const int PreviewTailChars = 4096;
    private const int BoundThreshold = PreviewHeadChars + PreviewTailChars;

    public static string BoundText(string text, string? path)
    {
        if (path is null || text.Length <= BoundThreshold)
            return text;
        var head = text[..PreviewHeadChars];
        var tail = text[^PreviewTailChars..];
        return $"{head}\n...[{text.Length:N0} chars; full output at: {path}]...\n{tail}";
    }
}
