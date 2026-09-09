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
    IReadOnlyList<EffectiveAcceptanceCriteriaCorrectionSnapshot>? EffectiveAcceptanceCriteriaCorrections = null,
    bool IsMetadataOnly = false,
    string? ResultCommit = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? TerminatedAt = null,
    int AutomaticAcceptanceRetryCount = 0,
    int OperatorAcceptanceRegateCount = 0,
    GoalHoldSnapshot? CurrentHold = null,
    int ClarificationRoundCount = 0,
    IReadOnlyList<GoalBriefVersion>? BriefVersions = null,
    IReadOnlyList<RefinedSpecVersionSnapshot>? RefinedSpecVersions = null,
    SourceBacklogCoverage? SourceBacklogCoverage = null,
    string? SliceBatchParentId = null,
    bool? AcceptanceFailureDeferredForRetry = null,
    IReadOnlyList<CriterionEvidenceObligation>? CriterionEvidenceObligations = null);

public sealed record GoalHoldSnapshot(
    string Identity,
    string State,
    string Blocker,
    DateTimeOffset StartedAt,
    DateTimeOffset? StalledAt = null);

public sealed record TerminalGoalMetadata(
    GoalId Id,
    GoalStatus Status,
    string Title,
    string? ResultCommit = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? TerminatedAt = null);

public sealed record EffectiveAcceptanceCriteriaCorrectionSnapshot(
    string SupersededCriterion,
    string Correction,
    string Actor,
    DateTimeOffset RecordedAt,
    string? SourceTaskId,
    ProgressKind SourceKind,
    bool IsWaiver = false,
    string? CapturedAcceptanceCriteriaHash = null);

public sealed record AcceptanceFailureSnapshot(
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> FailedChecks,
    string? BranchHeadSha = null,
    string? MainHeadSha = null,
    IReadOnlyList<AcceptanceCheckAttribution>? CheckAttributions = null,
    string? BaselineAttestation = null);

public sealed record RefinedSpecSnapshot(
    string BehavioralContract,
    IReadOnlyList<string> AcceptanceCriteria,
    string VerificationClass,
    IReadOnlyList<RefinedSpecDecisionSnapshot> Decisions,
    IReadOnlyList<RefinedSpecOpenQuestionSnapshot> OpenQuestions,
    IReadOnlyList<string>? OperatorOwnedAcceptanceCriteria = null,
    IReadOnlyList<HumanInputAnswerRecord>? ClarificationAnswerHistory = null);

public sealed record RefinedSpecVersionSnapshot(
    int Version,
    RefinedSpecSnapshot Spec,
    DateTimeOffset RecordedAt,
    int BriefVersion,
    int? SupersededByVersion = null);

public sealed record RefinedSpecDecisionSnapshot(string Question, string Choice, string Rationale);

public sealed record RefinedSpecOpenQuestionSnapshot(
    string Id,
    string Question,
    string ForkKind,
    string Status,
    string? Answer = null,
    string? TopicKey = null,
    string? NormalizedQuestionKey = null,
    string? Criterion = null,
    string? BlastRadius = null);

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
    RetryRoundKind? PendingRetryRoundKind = null,
    PreReviewEvidenceReceipt? PreReviewEvidenceReceipt = null,
    string? InterruptedDispatchRecoveryId = null,
    bool WasCancelledByConductor = false,
    IReadOnlyList<TaskDispatchSnapshot>? DispatchHistory = null,
    RetryCause PendingRetryCause = RetryCause.Unknown,
    IReadOnlyList<RetryAdmissionReceipt>? RetryAdmissionHistory = null,
    RetryAdmissionRoute? RetryAdmissionHoldRoute = null,
    ReviewFindingRepairCheckpoint? PendingReviewFindingRepairCheckpoint = null,
    AcceptedRetryFeedback? AcceptedRetryFeedback = null,
    IReadOnlyList<PreReviewEvidenceReceipt>? PreReviewEvidenceHistory = null,
    int PreReviewEvidenceAttemptCount = 0);

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
    int? PromptCharacterCount = null,
    int? CachedInputTokens = null,
    string? AuthoritativeOutput = null)
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
    ProviderFailureKind ProviderFailureKind = ProviderFailureKind.Unknown,
    IReadOnlyList<ReviewFindingLocation>? ReviewFindingTouchedAnchors = null,
    string? ReviewedCommit = null,
    IReadOnlyList<ReviewFinding>? MergedReviewFindings = null,
    bool WorkerResultPresent = false,
    ReviewFindingContractViolation? ReviewFindingContractViolation = null,
    DateTimeOffset? DispatchStartedAt = null,
    int? ChildProcessId = null,
    int? ChildExitCode = null,
    IReadOnlyList<FindingEvidenceReceipt>? FindingEvidenceReceipts = null,
    string? ReviewFindingTouchProofDiagnostic = null,
    string? AuthoritativeStandardOutput = null,
    string? AuthoritativeStandardError = null,
    string? AuthoritativeStandardOutputUnavailableReason = null,
    string? AuthoritativeStandardErrorUnavailableReason = null,
    PlannerCandidateDivergenceReceipt? PlannerCandidateDivergence = null,
    bool CompletionVerdictVerifiedSuccess = false,
    string? CompletionVerdictRule = null,
    bool? AssignedScopeComplete = null)
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
    DateTimeOffset? ProviderSessionRetiredAt = null,
    IReadOnlyList<ReviewFindingLocation>? ReviewFindingTouchedAnchors = null,
    int BriefVersion = 1,
    string? BriefSnapshot = null,
    string? ReviewFindingTouchProofDiagnostic = null,
    ReviewRetryCapReceipt? ReviewRetryCap = null,
    WorkerContextPackageReceipt? ContextPackageReceipt = null,
    int PlannerSampleCount = 1,
    RetryContextFingerprint? RetryContextFingerprint = null,
    PaidRouteClassification PaidRoute = PaidRouteClassification.Unknown);

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
    TaskProcessResourceAccountingSnapshot? ResourceAccounting = null,
    bool WasCancelledByConductor = false,
    string? ChildExitRecordPath = null,
    int? ChildProcessId = null,
    int? ChildExitCode = null,
    DispatchExitArtifactOrigin ExitArtifactOrigin = DispatchExitArtifactOrigin.None,
    string? ExitArtifactReason = null,
    bool WasGracefullyDetachedByConductor = false,
    IReadOnlyList<int>? NonBlockingProcessIds = null);

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
    DateTimeOffset OccurredAt,
    TaskRequeueSkippedPayload? RequeueSkipped = null,
    IReadOnlyList<OperatorGateRecord>? OperatorGates = null);

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
    bool WasDismissed = false,
    string? QuestionFingerprint = null,
    string? BlockerFingerprint = null,
    int SuppressionCount = 0,
    string? SupersededByRequestId = null,
    IReadOnlyList<OperatorGateRecord>? OperatorGates = null,
    IReadOnlyList<HumanInputAnswerRecord>? AnswerHistory = null,
    int SuppressionAnswerRevision = 0,
    long SuppressionRevision = 0);

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

        // Snap both cuts to LINE boundaries. Slicing raw characters severed whatever line straddled the
        // boundary, and for a reviewer's single-line `findings:` JSON array that produced a corrupt
        // half-array: it still looked like a findings field, so the reader picked it up, the deserialize
        // threw ("Expected end of string, but instead reached end of data. Path: $[3].location"), the
        // exception was swallowed, and the operator was told the reviewer had submitted NO findings while
        // twelve valid ones sat in the log on disk. A line must be wholly kept or wholly dropped; a dropped
        // line is an honest absence, a severed one is indistinguishable from a worker error.
        var head = text[..LineSnappedHeadLength(text)];
        var tail = text[LineSnappedTailStart(text)..];
        return BuildBoundedText(head, tail, text.Length, path);
    }

    private static int LineSnappedHeadLength(string text)
    {
        var lastNewline = text.LastIndexOf('\n', PreviewHeadChars - 1);

        // A head window containing no newline at all is one enormous line; keep the raw slice rather than
        // emit an empty head, since there is no boundary to snap to.
        return lastNewline < 0 ? PreviewHeadChars : lastNewline + 1;
    }

    private static int LineSnappedTailStart(string text)
    {
        var rawStart = text.Length - PreviewTailChars;
        var nextNewline = text.IndexOf('\n', rawStart);

        // Likewise: no newline in the tail window means one enormous trailing line.
        return nextNewline < 0 ? rawStart : nextNewline + 1;
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
