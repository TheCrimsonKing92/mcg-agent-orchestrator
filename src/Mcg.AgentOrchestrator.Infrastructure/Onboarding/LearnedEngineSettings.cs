using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Read-time engine derivations, never persisted or applied to the gate.</summary>
public sealed record LearnedEngineSettings(
    int MaxConcurrentShards,
    string MaxConcurrentShardsReason,
    int? DefaultTimeoutMinutes,
    string DefaultTimeoutReason,
    IReadOnlyList<ProjectOwnerQuestion> OwnerQuestions)
{
    public int? BuildServerShutdownMinutes { get; init; }
    public string? BuildServerShutdownReason { get; init; }
    public int? DiscoveryMinutes { get; init; }
    public string? DiscoveryReason { get; init; }
}
