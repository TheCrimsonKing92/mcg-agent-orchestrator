namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>A source-owned, non-empty opaque identity and its reported test outcome.</summary>
internal sealed record TestResult(
    string Id,
    string DisplayName,
    string? File,
    IReadOnlyList<string> SuitePath,
    TestResultStatus Status,
    long? DurationMs,
    string? Message,
    string? Trace,
    IReadOnlyList<TestResultAttempt> Attempts,
    string? Seed,
    string? OutputRef,
    string ReportedBy);
