namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TestResultAttempt(
    TestResultStatus Status, long? DurationMs, string? Message, string? Trace);
