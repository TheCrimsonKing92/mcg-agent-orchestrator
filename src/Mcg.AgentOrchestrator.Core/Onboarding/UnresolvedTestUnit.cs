namespace Mcg.AgentOrchestrator.Core;

/// <summary>A known test unit whose runner still needs an owner answer.</summary>
public sealed record UnresolvedTestUnit(string UnitId, string FactKey);
