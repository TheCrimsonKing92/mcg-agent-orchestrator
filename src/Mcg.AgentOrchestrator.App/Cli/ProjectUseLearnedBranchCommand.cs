using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectUseLearnedBranchCommand
{
    private const string Usage = "Usage: project use-learned-branch <name> [--off]";

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry, TextWriter output)
    {
        if (parts.Count is < 3 or > 4 || string.IsNullOrWhiteSpace(parts[2]) ||
            parts[2].StartsWith("--", StringComparison.Ordinal) || (parts.Count == 4 && parts[3] != "--off"))
            throw new ArgumentException(Usage);

        var enabled = parts.Count == 3;
        registry.SetUseLearnedIntegrationBranch(parts[2], enabled);
        var project = registry.GetRequiredProject(parts[2]);
        output.WriteLine($"Use learned integration branch: {(enabled ? "on" : "off")}; integration branch: {project.IntegrationBranch} (source: {project.IntegrationBranchSource})");
        if (project.IntegrationBranchNote is not null) output.WriteLine(project.IntegrationBranchNote);
        return 0;
    }
}
