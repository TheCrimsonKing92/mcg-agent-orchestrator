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
    int? PromptCharacterCount = null);

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
    ProviderFailureKind ProviderFailureKind = ProviderFailureKind.Unknown)
{
    public bool Succeeded => ExitCode == 0;
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
    string? ReasoningEffortReason = null);

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
    TaskProcessResourceAccounting? ResourceAccounting = null)
{
    public bool IsRunning => CompletedAt is null && ExitCode is null;

    public IReadOnlyList<int> TrackedProcessIds =>
        OwnedProcessIds is { Count: > 0 }
            ? OwnedProcessIds
            : [ProcessId];
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
