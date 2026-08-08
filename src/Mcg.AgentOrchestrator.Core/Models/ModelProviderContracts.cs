namespace Mcg.AgentOrchestrator.Core;

public sealed record ModelMessage(string Role, string Content);

public sealed record ModelOptions(double Temperature = 0.2, int? MaxOutputTokens = null, string? ReasoningEffort = null, string? ModelName = null);

public sealed record ModelRequest(string SystemPrompt, IReadOnlyList<ModelMessage> Messages, ModelOptions Options);

public sealed record ModelUsage(int? InputTokens, int? OutputTokens);

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
    int? PromptCharacterCount = null)
{
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
    IReadOnlyList<FindingEvidenceReceipt>? FindingEvidenceReceipts = null)
{
    public string StandardOutput { get; init; } = VerificationTextBounds.BoundText(StandardOutput, StandardOutputPath);

    public string StandardError { get; init; } = VerificationTextBounds.BoundText(StandardError, StandardErrorPath);

    public bool Succeeded => ExitCode == 0;

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
    IReadOnlyList<ReviewFindingLocation>? ReviewFindingTouchedAnchors = null);

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
    bool WasGracefullyDetachedByConductor = false)
{
    public bool IsRunning => CompletedAt is null && ExitCode is null;

    public IReadOnlyList<int> TrackedProcessIds =>
        OwnedProcessIds is { Count: > 0 }
            ? OwnedProcessIds
            : [ProcessId];
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
