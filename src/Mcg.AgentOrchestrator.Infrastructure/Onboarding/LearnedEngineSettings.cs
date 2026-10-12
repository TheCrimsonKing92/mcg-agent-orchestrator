using System.Collections.Immutable;
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
    private static readonly ImmutableDictionary<string, (int Minutes, string Reason)> EmptyCheckTimeouts =
        ImmutableDictionary<string, (int Minutes, string Reason)>.Empty.WithComparers(StringComparer.Ordinal);

    public int? BuildServerShutdownMinutes { get; init; }
    public string? BuildServerShutdownReason { get; init; }
    public int? DiscoveryMinutes { get; init; }
    public string? DiscoveryReason { get; init; }
    public IReadOnlyDictionary<string, (int Minutes, string Reason)> CheckTimeouts { get; init; } = EmptyCheckTimeouts;
}
