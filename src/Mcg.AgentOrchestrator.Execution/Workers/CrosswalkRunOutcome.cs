namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record CrosswalkRunOutcome(string Status, IReadOnlyList<TestResult> Result, string Detail);
