namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>An explicitly declared environment binding to an existing tool directory.</summary>
public sealed record DeclaredToolHome(string Variable, string Directory);
