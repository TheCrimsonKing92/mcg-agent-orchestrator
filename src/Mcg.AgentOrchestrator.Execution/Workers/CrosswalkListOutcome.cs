namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record CrosswalkListOutcome(string Status, IReadOnlyList<CrosswalkTestRef> Result, string Detail);
