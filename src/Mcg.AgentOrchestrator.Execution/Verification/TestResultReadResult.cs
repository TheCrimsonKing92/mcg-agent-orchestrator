namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TestResultReadResult(
    string Path, TestResultReadStatus Status, IReadOnlyList<TestResult> Records, string? Detail);
