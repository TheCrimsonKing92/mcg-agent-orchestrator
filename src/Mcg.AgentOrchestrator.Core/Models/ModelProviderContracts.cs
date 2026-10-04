using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed record ModelMessage(string Role, string Content);

public sealed record ModelOptions(double Temperature = 0.2, int? MaxOutputTokens = null, string? ReasoningEffort = null, string? ModelName = null);

public sealed record ModelRequest(string SystemPrompt, IReadOnlyList<ModelMessage> Messages, ModelOptions Options);

public sealed record ModelUsage(int? InputTokens, int? OutputTokens, int? CachedInputTokens = null);

public sealed record ModelResponse(string Text, ModelUsage? Usage, string StopReason);

public sealed record AgentTaskRunPreview(
    AgentId AgentId,
    string AgentName,
    string ProviderName,
    string ModelName,
    TaskComplexity TaskComplexity,
    int? MaxOutputTokens,
    string? ReasoningEffort,
    int PromptCharacterCount,
    bool UsesComplexModel = false);

public sealed record TaskExecutionRecord(
    AgentId AgentId,
    string AgentName,
    string ProviderName,
    string ModelName,
    string Output,
    string StopReason,
    ModelUsage? Usage,
    DateTimeOffset CompletedAt,
    TaskComplexity? TaskComplexity = null,
    int? MaxOutputTokens = null,
    int? PromptCharacterCount = null,
    string? FullOutput = null,
    bool OutputIsAuthoritative = true)
{
    public string? AuthoritativeOutput { get; init; } = FullOutput ?? (OutputIsAuthoritative ? Output : null);

    public string Output { get; init; } = VerificationTextBounds.BoundText(Output, path: null);
}

public sealed record AgentTaskRunResult(Goal Goal, TaskSpec Task, TaskExecutionRecord Execution);

public sealed record TaskVerificationRecord(
    string Command,
    string WorkingDirectory,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset CompletedAt,
    string? ModelFitNote = null,
    string? StandardOutputPath = null,
    string? StandardErrorPath = null,
    bool WorkerResultPresent = false,
    bool HasCommittedChanges = false,
    // Worker-self-reported stdout byte count from the live heartbeat, captured at dispatch-record time.
    // Reliable even when the out.log file read races the exit flush (the empty-output flake bug): a worker
    // that streamed bytes per its heartbeat genuinely produced output and must not be re-dispatched as a flake.
    long? HeartbeatStandardOutputBytes = null,
    ProviderFailureKind ProviderFailureKind = ProviderFailureKind.Unknown,
    string? HumanInputQuestion = null,
    IReadOnlyList<ReviewFindingLocation>? ReviewFindingTouchedAnchors = null,
    string? ReviewedCommit = null,
    IReadOnlyList<ReviewFinding>? MergedReviewFindings = null,
    ReviewFindingContractViolation? ReviewFindingContractViolation = null,
    DateTimeOffset? DispatchStartedAt = null,
    int? ChildProcessId = null,
    int? ChildExitCode = null,
    IReadOnlyList<FindingEvidenceReceipt>? FindingEvidenceReceipts = null,
    string? OrchestratorFailureReason = null,
    string? ReviewFindingTouchProofDiagnostic = null,
    string? HumanInputQuestionFingerprint = null,
    string? HumanInputBlockerFingerprint = null,
    int? ObservedRootExitCode = null,
    bool ReconciledToSuccess = false,
    string? ReconciliationOriginRule = null,
    string? FullStandardOutput = null,
    string? FullStandardError = null,
    string? FullStandardOutputUnavailableReason = null,
    string? FullStandardErrorUnavailableReason = null,
    bool StandardOutputIsAuthoritative = true,
    bool StandardErrorIsAuthoritative = true,
    PlannerCandidateDivergenceReceipt? PlannerCandidateDivergence = null,
    bool CompletionVerdictVerifiedSuccess = false,
    string? CompletionVerdictRule = null,
    bool? AssignedScopeComplete = null,
    // Carried alongside the fingerprints because the directive text is stripped from the recorded
    // stdout snapshot, so kernel reparse cannot recover the classification on its own.
    HumanWaitKind? HumanInputKind = null,
    string? HumanInputEvidenceOwner = null,
    CandidateIdentity? CandidateIdentity = null,
    string? AcceptanceCriteriaVersionHash = null,
    FailedGoalInconclusiveRoundInputs? InconclusiveRoundInputs = null,
    PlannerEvidenceStoreReference? HumanInputStoreReference = null)
{
    public string? AuthoritativeStandardOutput { get; init; } = FullStandardOutput ??
        (StandardOutputIsAuthoritative && FullStandardOutputUnavailableReason is null ? StandardOutput : null);

    public string? AuthoritativeStandardError { get; init; } = FullStandardError ??
        (StandardErrorIsAuthoritative && FullStandardErrorUnavailableReason is null ? StandardError : null);

    public string StandardOutput { get; init; } = VerificationTextBounds.BoundText(StandardOutput, StandardOutputPath);

    public string StandardError { get; init; } = VerificationTextBounds.BoundText(StandardError, StandardErrorPath);

    public bool Succeeded => ExitCode == 0 && string.IsNullOrWhiteSpace(OrchestratorFailureReason);

    public bool HasSameRoundIdentity(TaskVerificationRecord other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return CompletedAt == other.CompletedAt &&
               DispatchStartedAt == other.DispatchStartedAt &&
               ChildProcessId == other.ChildProcessId &&
               ChildExitCode == other.ChildExitCode &&
               ExitCode == other.ExitCode &&
               ProviderFailureKind == other.ProviderFailureKind &&
               WorkerResultPresent == other.WorkerResultPresent &&
               string.Equals(Command, other.Command, StringComparison.Ordinal) &&
               string.Equals(WorkingDirectory, other.WorkingDirectory, StringComparison.Ordinal) &&
               string.Equals(StandardOutput, other.StandardOutput, StringComparison.Ordinal) &&
               string.Equals(StandardError, other.StandardError, StringComparison.Ordinal) &&
               string.Equals(StandardOutputPath, other.StandardOutputPath, StringComparison.Ordinal) &&
               string.Equals(StandardErrorPath, other.StandardErrorPath, StringComparison.Ordinal) &&
               string.Equals(ReviewedCommit, other.ReviewedCommit, StringComparison.OrdinalIgnoreCase);
    }

    public TaskVerificationRecord MergeSameRoundEnrichment(TaskVerificationRecord preferred)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        if (!HasSameRoundIdentity(preferred))
        {
            throw new ArgumentException("Verification enrichment can only be merged within one durable round.", nameof(preferred));
        }

        var authoritativeOutput = preferred.AuthoritativeStandardOutput ?? AuthoritativeStandardOutput;
        var authoritativeError = preferred.AuthoritativeStandardError ?? AuthoritativeStandardError;
        var preferredHasCompletionVerdict = preferred.CompletionVerdictVerifiedSuccess ||
                                            !string.IsNullOrWhiteSpace(preferred.CompletionVerdictRule);
        return this with
        {
            ModelFitNote = preferred.ModelFitNote ?? ModelFitNote,
            CandidateIdentity = preferred.CandidateIdentity ?? CandidateIdentity,
            AcceptanceCriteriaVersionHash = preferred.AcceptanceCriteriaVersionHash ?? AcceptanceCriteriaVersionHash,
            InconclusiveRoundInputs = InconclusiveRoundInputs ?? preferred.InconclusiveRoundInputs,
            HasCommittedChanges = HasCommittedChanges || preferred.HasCommittedChanges,
            HeartbeatStandardOutputBytes = preferred.HeartbeatStandardOutputBytes ?? HeartbeatStandardOutputBytes,
            HumanInputQuestion = preferred.HumanInputQuestion ?? HumanInputQuestion,
            ReviewFindingTouchedAnchors = PreferPopulated(preferred.ReviewFindingTouchedAnchors, ReviewFindingTouchedAnchors),
            MergedReviewFindings = MergeFindings(MergedReviewFindings, preferred.MergedReviewFindings),
            ReviewFindingContractViolation = preferred.ReviewFindingContractViolation ?? ReviewFindingContractViolation,
            FindingEvidenceReceipts = MergeReceipts(FindingEvidenceReceipts, preferred.FindingEvidenceReceipts),
            OrchestratorFailureReason = preferred.OrchestratorFailureReason ?? OrchestratorFailureReason,
            ReviewFindingTouchProofDiagnostic = preferred.ReviewFindingTouchProofDiagnostic ?? ReviewFindingTouchProofDiagnostic,
            HumanInputQuestionFingerprint = preferred.HumanInputQuestionFingerprint ?? HumanInputQuestionFingerprint,
            HumanInputBlockerFingerprint = preferred.HumanInputBlockerFingerprint ?? HumanInputBlockerFingerprint,
            HumanInputKind = preferred.HumanInputKind ?? HumanInputKind,
            HumanInputEvidenceOwner = preferred.HumanInputEvidenceOwner ?? HumanInputEvidenceOwner,
            HumanInputStoreReference = preferred.HumanInputStoreReference ?? HumanInputStoreReference,
            ObservedRootExitCode = preferred.ObservedRootExitCode ?? ObservedRootExitCode,
            ReconciledToSuccess = ReconciledToSuccess || preferred.ReconciledToSuccess,
            ReconciliationOriginRule = preferred.ReconciliationOriginRule ?? ReconciliationOriginRule,
            FullStandardOutput = authoritativeOutput,
            FullStandardError = authoritativeError,
            AuthoritativeStandardOutput = authoritativeOutput,
            AuthoritativeStandardError = authoritativeError,
            FullStandardOutputUnavailableReason = authoritativeOutput is null
                ? preferred.FullStandardOutputUnavailableReason ?? FullStandardOutputUnavailableReason
                : null,
            FullStandardErrorUnavailableReason = authoritativeError is null
                ? preferred.FullStandardErrorUnavailableReason ?? FullStandardErrorUnavailableReason
                : null,
            StandardOutputIsAuthoritative = authoritativeOutput is not null,
            StandardErrorIsAuthoritative = authoritativeError is not null,
            PlannerCandidateDivergence = preferred.PlannerCandidateDivergence ?? PlannerCandidateDivergence,
            CompletionVerdictVerifiedSuccess = preferredHasCompletionVerdict
                ? preferred.CompletionVerdictVerifiedSuccess
                : CompletionVerdictVerifiedSuccess,
            CompletionVerdictRule = preferredHasCompletionVerdict
                ? preferred.CompletionVerdictRule
                : CompletionVerdictRule,
            AssignedScopeComplete = preferred.AssignedScopeComplete ?? AssignedScopeComplete
        };
    }

    private static IReadOnlyList<T>? PreferPopulated<T>(IReadOnlyList<T>? preferred, IReadOnlyList<T>? fallback) =>
        preferred is { Count: > 0 } || fallback is null ? preferred : fallback;

    private static IReadOnlyList<ReviewFinding>? MergeFindings(
        IReadOnlyList<ReviewFinding>? existing,
        IReadOnlyList<ReviewFinding>? preferred)
    {
        if (existing is null)
        {
            return preferred;
        }
        if (preferred is null)
        {
            return existing;
        }

        var merged = existing.ToList();
        foreach (var finding in preferred)
        {
            var matchingIndex = merged.FindIndex(candidate =>
                string.Equals(candidate.StableId, finding.StableId, StringComparison.Ordinal) &&
                JsonContentEquals(
                    candidate with { EvidenceOutcome = null },
                    finding with { EvidenceOutcome = null }));
            if (matchingIndex < 0)
            {
                // Preserve genuinely contradictory same-round bodies. The projector's typed
                // ambiguity guard will reject them instead of silently choosing one.
                merged.Add(finding);
                continue;
            }

            var prior = merged[matchingIndex];
            merged[matchingIndex] = finding with
            {
                EvidenceOutcome = finding.EvidenceOutcome ?? prior.EvidenceOutcome
            };
        }

        return merged;
    }

    private static IReadOnlyList<FindingEvidenceReceipt>? MergeReceipts(
        IReadOnlyList<FindingEvidenceReceipt>? existing,
        IReadOnlyList<FindingEvidenceReceipt>? preferred)
    {
        if (existing is null)
        {
            return preferred;
        }
        if (preferred is null)
        {
            return existing;
        }

        var merged = existing.ToList();
        foreach (var receipt in preferred)
        {
            var matchingIndex = merged.FindIndex(candidate =>
                string.Equals(candidate.ReceiptId, receipt.ReceiptId, StringComparison.Ordinal) &&
                JsonContentEquals(candidate, receipt));
            if (matchingIndex >= 0)
            {
                merged[matchingIndex] = receipt;
            }
            else
            {
                // A reused receipt id with a different body remains visible so the projector
                // can fail closed with evidence-identity-conflict.
                merged.Add(receipt);
            }
        }

        return merged;
    }

    private static bool JsonContentEquals<T>(T left, T right) =>
        JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(right));

    public IReadOnlyList<ReviewFinding> GetOpenAdvisoryFindings(
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections) =>
        ReviewFindings.GetOpenAdvisoryFindings(MergedReviewFindings ?? [], criteriaCorrections);
}

public sealed record TaskDispatchRecord(
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
    PaidRouteClassification PaidRoute = PaidRouteClassification.Unknown,
    // The Claude credential source this dispatch's auth preflight selected and reported, carried so the
    // dispatch start boundary transports that decision to the worker sandbox instead of selecting again.
    // A directory path and an explicit/default source kind only: never credential material.
    string? ClaudeCredentialSourceDirectory = null,
    bool ClaudeCredentialSourceIsExplicit = false,
    string? AssignedAgentId = null,
    int ConductorRoutingRevision = 0,
    PreDispatchIntegrationReceipt? PreDispatchIntegrationReceipt = null,
    GoalId? GoalId = null,
    CandidateIdentity? CandidateIdentity = null,
    DispatchProviderUsage? ProviderUsage = null,
    FailedGoalInconclusiveRoundInputs? InconclusiveRoundInputs = null)
{
    public int BriefVersion { get; internal set; } = BriefVersion;

    public string? BriefSnapshot { get; internal set; } = BriefSnapshot;

    public string? AssignedAgentId { get; internal set; } = AssignedAgentId;

    public int ConductorRoutingRevision { get; internal set; } = ConductorRoutingRevision;

    public GoalId? GoalId { get; internal set; } = GoalId;
}

public sealed record DispatchProviderUsage(
    ProviderUsageValue InputTokens,
    ProviderUsageValue CachedInputTokens,
    ProviderUsageValue OutputTokens)
{
    public static DispatchProviderUsage From(ProviderReportedUsage? usage, string? unavailableReason) => new(
        Value(usage?.InputTokens, unavailableReason),
        Value(usage?.CachedInputTokens, unavailableReason),
        Value(usage?.OutputTokens, unavailableReason));

    private static ProviderUsageValue Value(long? count, string? reason) => count is { } value
        ? ProviderUsageValue.Reported(value)
        : ProviderUsageValue.Unknown(string.IsNullOrWhiteSpace(reason) ? "absent" : reason);
}

public sealed record ReviewRetryCapReceipt(int Round, int StopRound)
{
    public bool IsAtCap => Round >= StopRound;

    public static ReviewRetryCapReceipt Create(Goal goal, int stopRound)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (stopRound <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stopRound), "Review retry stop round must be greater than zero.");
        }

        var priorAutomaticRetries = goal.Timeline.Count(evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        return new ReviewRetryCapReceipt(priorAutomaticRetries + 1, stopRound);
    }
}

public enum DispatchResumeAdmissionKind
{
    WarmResume,
    FreshDispatchOnly,
    Retired
}

public sealed record DispatchResumeAdmissionDecision(
    DispatchResumeAdmissionKind Kind,
    string Reason)
{
    public bool AllowsWarmResume => Kind == DispatchResumeAdmissionKind.WarmResume;
}

public static class DispatchResumeAdmission
{
    public static DispatchResumeAdmissionDecision Evaluate(
        TaskDispatchRecord dispatch,
        string? currentWorktreeHeadSha,
        string? currentDirtyStateHash,
        bool goalCleanedUp = false)
    {
        if (goalCleanedUp || dispatch.ProviderSessionRetiredAt is not null)
        {
            return new DispatchResumeAdmissionDecision(
                DispatchResumeAdmissionKind.Retired,
                "provider session retired with goal cleanup");
        }

        if (string.IsNullOrWhiteSpace(dispatch.ProviderSessionId))
        {
            return new DispatchResumeAdmissionDecision(
                DispatchResumeAdmissionKind.FreshDispatchOnly,
                "missing provider session id");
        }

        if (string.IsNullOrWhiteSpace(dispatch.WorktreeHeadSha) ||
            string.IsNullOrWhiteSpace(dispatch.DirtyStateHash))
        {
            return new DispatchResumeAdmissionDecision(
                DispatchResumeAdmissionKind.FreshDispatchOnly,
                "missing spawn generation tuple");
        }

        if (!string.Equals(dispatch.WorktreeHeadSha.Trim(), currentWorktreeHeadSha?.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(dispatch.DirtyStateHash.Trim(), currentDirtyStateHash?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new DispatchResumeAdmissionDecision(
                DispatchResumeAdmissionKind.FreshDispatchOnly,
                "spawn generation tuple mismatch");
        }

        return new DispatchResumeAdmissionDecision(
            DispatchResumeAdmissionKind.WarmResume,
            "provider session id and spawn generation tuple match");
    }
}

public enum CancellationCandidateEvidenceKind
{
    Indeterminate,
    ConfirmedUnchanged,
    Changed,
    Dirty,
    Unsafe,
    Unavailable
}

public sealed record CancellationCandidateEvidence(
    CancellationCandidateEvidenceKind Kind,
    string? CandidateSha,
    string Reason,
    string GitReceipt)
{
    public static CancellationCandidateEvidence Indeterminate(string reason = "cancellation evidence was not supplied") =>
        new(CancellationCandidateEvidenceKind.Indeterminate, null, reason, "git-not-inspected");

    public static CancellationCandidateEvidence ConfirmedUnchanged(string candidateSha, string gitReceipt) =>
        new(CancellationCandidateEvidenceKind.ConfirmedUnchanged, candidateSha, "cancelled-no-candidate-change", gitReceipt);

    public static CancellationCandidateEvidence Changed(string? candidateSha, string reason, string gitReceipt) =>
        new(CancellationCandidateEvidenceKind.Changed, candidateSha, reason, gitReceipt);

    public static CancellationCandidateEvidence Dirty(string? candidateSha, string reason, string gitReceipt) =>
        new(CancellationCandidateEvidenceKind.Dirty, candidateSha, reason, gitReceipt);

    public static CancellationCandidateEvidence Unsafe(string reason, string gitReceipt) =>
        new(CancellationCandidateEvidenceKind.Unsafe, null, reason, gitReceipt);

    public static CancellationCandidateEvidence Unavailable(string reason, string gitReceipt) =>
        new(CancellationCandidateEvidenceKind.Unavailable, null, reason, gitReceipt);
}

public sealed record TaskProcessRecord(
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
    TaskProcessResourceAccounting? ResourceAccounting = null,
    bool WasCancelledByConductor = false,
    string? ChildExitRecordPath = null,
    int? ChildProcessId = null,
    int? ChildExitCode = null,
    DispatchExitArtifactOrigin ExitArtifactOrigin = DispatchExitArtifactOrigin.None,
    string? ExitArtifactReason = null,
    bool WasGracefullyDetachedByConductor = false,
    IReadOnlyList<int>? NonBlockingProcessIds = null,
    DateTimeOffset? ProcessIdentityStartedAt = null)
{
    public bool IsRunning => CompletedAt is null && ExitCode is null;

    public IReadOnlyList<int> TrackedProcessIds =>
        OwnedProcessIds is { Count: > 0 }
            ? OwnedProcessIds
            : [ProcessId];

    // Planner samples stay non-blocking for generic hang classification; BackgroundDispatchRunner
    // applies their separate artifact-based bounded completion gate before candidate collection.
    public IReadOnlyList<int> CompletionTrackedProcessIds =>
        NonBlockingProcessIds is { Count: > 0 }
            ? TrackedProcessIds.Where(pid => pid == ProcessId || !NonBlockingProcessIds.Contains(pid)).ToArray()
            : TrackedProcessIds;
}

public enum DispatchExitArtifactOrigin
{
    None,
    Native,
    Synthetic,
    UnknownLegacy
}

public sealed record TaskProcessResourceAccounting(
    long CpuMilliseconds,
    long PeakMemoryBytes,
    long IoBytes,
    bool Reaped = false,
    string AccountingSource = "live");

public interface IModelProvider
{
    string ProviderName { get; }

    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
}

public interface IModelProviderRegistry
{
    IModelProvider GetRequired(string providerName);
}

public sealed class InMemoryModelProviderRegistry : IModelProviderRegistry
{
    private readonly Dictionary<string, IModelProvider> _providers;

    public InMemoryModelProviderRegistry(IEnumerable<IModelProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IModelProvider GetRequired(string providerName)
    {
        return _providers.TryGetValue(providerName, out var provider)
            ? provider
            : throw new KeyNotFoundException($"Model provider '{providerName}' is not registered.");
    }
}
