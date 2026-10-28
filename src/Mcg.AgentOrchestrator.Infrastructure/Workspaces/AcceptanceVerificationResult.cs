using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AcceptanceVerificationResult(
    bool Passed,
    bool Skipped,
    int? ExitCode,
    string? OutputTail,
    bool Retried = false,
    string? ArtifactsPath = null,
    IReadOnlyList<AcceptanceCheckResult>? Checks = null,
    IReadOnlyList<string>? TestResultPaths = null)
{
    // Selected from the effective manifest, independently of the completed results.
    public IReadOnlyList<string> RequiredCrosswalkChecks { get; init; } = [];
    public IReadOnlyList<string> RequiredTestCheckNames { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttemptId { get; init; }
}
