namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Crosswalk owns the stack vocabulary; onboarding retains it without interpretation.</summary>
internal sealed record CrosswalkStackReport(string Stack, string? TestFramework, string Evidence)
{
    internal const string NoStack = "unknown";
}
