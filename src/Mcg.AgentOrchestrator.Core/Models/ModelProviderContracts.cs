namespace Mcg.AgentOrchestrator.Core;

public sealed record ModelMessage(string Role, string Content);

public sealed record ModelOptions(double Temperature = 0.2, int? MaxOutputTokens = null, string? ReasoningEffort = null, string? ModelName = null);

public sealed record ModelRequest(string SystemPrompt, IReadOnlyList<ModelMessage> Messages, ModelOptions Options);

public sealed record ModelUsage(int? InputTokens, int? OutputTokens);

public sealed record ModelResponse(string Text, ModelUsage? Usage, string StopReason);

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
    int? MaxOutputTokens = null);

public sealed record AgentTaskRunResult(Goal Goal, TaskSpec Task, TaskExecutionRecord Execution);

public sealed record TaskVerificationRecord(
    string Command,
    string WorkingDirectory,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset CompletedAt)
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
    TaskComplexity? TaskComplexity = null);

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
    bool WasCancelled = false)
{
    public bool IsRunning => CompletedAt is null && ExitCode is null;
}

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
