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
    AcceptanceFailureSnapshot? LatestAcceptanceFailure = null,
    IReadOnlyList<EffectiveAcceptanceCriteriaCorrectionSnapshot>? EffectiveAcceptanceCriteriaCorrections = null);

public sealed record EffectiveAcceptanceCriteriaCorrectionSnapshot(
    string SupersededCriterion,
    string Correction,
    string Actor,
    DateTimeOffset RecordedAt,
    string? SourceTaskId,
    ProgressKind SourceKind);

public sealed record AcceptanceFailureSnapshot(
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> FailedChecks,
    string? BranchHeadSha = null,
    string? MainHeadSha = null);

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
    string? Answer = null,
    string? TopicKey = null,
    string? NormalizedQuestionKey = null);

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
    int EmptyOutputRetryCount = 0,
    DateTimeOffset? LatestRetryAt = null,
    RetryRoundKind? PendingRetryRoundKind = null);

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
    int? PromptCharacterCount = null)
{
    public string Output { get; init; } = VerificationTextBounds.BoundText(Output, path: null);
}

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
    ProviderFailureKind ProviderFailureKind = ProviderFailureKind.Unknown)
{
    public string StandardOutput { get; init; } = VerificationTextBounds.BoundText(StandardOutput, StandardOutputPath);

    public string StandardError { get; init; } = VerificationTextBounds.BoundText(StandardError, StandardErrorPath);
}

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
    ProviderKind WorkerProviderKind = ProviderKind.Unknown,
    string? ReasoningEffortReason = null,
    string? DispatchLane = null,
    string? ModelSelectionReason = null,
    string? ProviderSessionId = null,
    string? WorktreeHeadSha = null,
    string? DirtyStateHash = null,
    DateTimeOffset? ProviderSessionRetiredAt = null);

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
    bool Reaped = false,
    string AccountingSource = "live");

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
    public const int PreviewHeadChars = 8192;
    public const int PreviewTailChars = 8192;
    public const int BoundThreshold = PreviewHeadChars + PreviewTailChars;
    public const int MaxRetainedChars = 20_000;

    public static string BoundText(string text, string? path)
    {
        if (text.Length <= BoundThreshold)
        {
            return text;
        }

        if (IsBoundedExcerpt(text))
        {
            return text;
        }

        var head = text[..PreviewHeadChars];
        var tail = text[^PreviewTailChars..];
        return BuildBoundedText(head, tail, text.Length, path);
    }

    public static string BuildBoundedText(string head, string tail, long totalChars, string? path)
    {
        var location = string.IsNullOrWhiteSpace(path)
            ? "full output path not recorded"
            : $"full output at: {path}";
        return $"{head}\n...[{totalChars:N0} chars; {location}]...\n{tail}";
    }

    private static bool IsBoundedExcerpt(string text)
    {
        if (text.Length > MaxRetainedChars)
        {
            return false;
        }

        return text.Contains("\n...[", StringComparison.Ordinal) &&
            text.Contains(" chars; ", StringComparison.Ordinal) &&
            text.Contains("]...\n", StringComparison.Ordinal);
    }
}
