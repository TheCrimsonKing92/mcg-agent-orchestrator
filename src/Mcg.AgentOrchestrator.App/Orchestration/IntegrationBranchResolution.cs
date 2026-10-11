namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record IntegrationBranchResolution(string Branch, string Source, string? Note)
{
    public const string Registry = "Registry";
    public const string Learned = "Learned";
    public const string Default = "Default";
}
